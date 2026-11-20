package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/mozillazg/go-pinyin"
)

var validToolID = regexp.MustCompile(`^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$`)
var validVersion = regexp.MustCompile(`^[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z.-]+)?$`)

var knownPermissions = map[string]bool{"storage": true, "ai": true, "media": true, "files": true}

type ToolWindowSettings struct {
	Width     int  `json:"width"`
	Height    int  `json:"height"`
	Resizable bool `json:"resizable"`
}

type ToolManifest struct {
	ID          string             `json:"id"`
	Name        string             `json:"name"`
	Version     string             `json:"version"`
	Description *string            `json:"description,omitempty"`
	Entry       string             `json:"entry"`
	Permissions []string           `json:"permissions"`
	Window      ToolWindowSettings `json:"window"`
	CreatedAt   string             `json:"createdAt"`
	AllowEval   bool               `json:"allowEval"`
	CSP         *string            `json:"csp,omitempty"`
	Category    *string            `json:"category,omitempty"`
	Glyph       *string            `json:"glyph,omitempty"`
}

type ToolVersionView struct {
	Version   string  `json:"version"`
	CreatedAt string  `json:"createdAt"`
	SizeBytes int64   `json:"sizeBytes"`
	IsActive  bool    `json:"isActive"`
	Note      *string `json:"note,omitempty"`
}

type LoadedTool struct {
	Manifest        ToolManifest
	Directory       string
	IsValid         bool
	Errors          []string
	Permissions     []string
	Versions        []ToolVersionView
	LastModifiedUTC string
}

func (t *LoadedTool) Name() string {
	if strings.TrimSpace(t.Manifest.Name) == "" {
		return t.Manifest.ID
	}
	return t.Manifest.Name
}

func (t *LoadedTool) Entry() string {
	if strings.TrimSpace(t.Manifest.Entry) == "" {
		return "index.html"
	}
	return t.Manifest.Entry
}

func (t *LoadedTool) HasPermission(permission string) bool {
	for _, value := range t.Permissions {
		if value == permission {
			return true
		}
	}
	return false
}

type ToolRegistry struct {
	mu    sync.RWMutex
	dir   string
	tools map[string]*LoadedTool
}

func NewToolRegistry(dir string) *ToolRegistry {
	r := &ToolRegistry{dir: dir, tools: map[string]*LoadedTool{}}
	r.Rescan()
	return r
}

func (r *ToolRegistry) Dir() string { return r.dir }

func (r *ToolRegistry) Rescan() {
	loaded := map[string]*LoadedTool{}
	entries, err := os.ReadDir(r.dir)
	if err == nil {
		for _, entry := range entries {
			if !entry.IsDir() || strings.HasPrefix(entry.Name(), ".") {
				continue
			}
			directory := filepath.Join(r.dir, entry.Name())
			path := filepath.Join(directory, "manifest.json")
			text, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			var manifest ToolManifest
			if err := json.Unmarshal(text, &manifest); err != nil {
				logWarn("解析工具 %s 失败：%v", entry.Name(), err)
				continue
			}
			normalizeManifest(&manifest)
			errors := validateManifest(&manifest, entry.Name())
			permissions := normalizePermissions(manifest.Permissions)
			loaded[manifest.ID] = &LoadedTool{
				Manifest: manifest, Directory: directory, IsValid: len(errors) == 0,
				Errors: errors, Permissions: permissions,
				Versions:        listVersions(directory, manifest.Version),
				LastModifiedUTC: fileModifiedUTC(path),
			}
		}
	}
	r.mu.Lock()
	r.tools = loaded
	r.mu.Unlock()
}

func (r *ToolRegistry) All() []*LoadedTool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	values := make([]*LoadedTool, 0, len(r.tools))
	for _, tool := range r.tools {
		values = append(values, tool)
	}
	sort.Slice(values, func(i, j int) bool {
		return strings.ToLower(values[i].Name()) < strings.ToLower(values[j].Name())
	})
	return values
}

func (r *ToolRegistry) Find(id string) *LoadedTool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.tools[id]
}

func (r *ToolRegistry) Contains(id string) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	_, ok := r.tools[id]
	return ok
}

func (r *ToolRegistry) Remove(id string) {
	r.mu.Lock()
	delete(r.tools, id)
	r.mu.Unlock()
}

func (r *ToolRegistry) Reload(id string) *LoadedTool {
	r.Rescan()
	return r.Find(id)
}

func (r *ToolRegistry) Rename(id, name string) error {
	tool := r.Find(id)
	if tool == nil {
		return bridgeErr("TOOL_NOT_FOUND", fmt.Sprintf("未找到工具 '%s'", id))
	}
	name = strings.TrimSpace(name)
	if name == "" || len([]rune(name)) > 60 {
		return bridgeErr("TOOL_RENAME_FAILED", "名称必填且不超过 60 字符")
	}
	manifest := tool.Manifest
	manifest.Name = name
	data, _ := json.MarshalIndent(manifest, "", "  ")
	temp := filepath.Join(tool.Directory, "manifest.json.rename-tmp")
	if err := os.WriteFile(temp, append(data, '\n'), 0o644); err != nil {
		return bridgeErr("TOOL_RENAME_FAILED", fmt.Sprintf("重命名失败：%v", err))
	}
	if err := os.Rename(temp, filepath.Join(tool.Directory, "manifest.json")); err != nil {
		return bridgeErr("TOOL_RENAME_FAILED", fmt.Sprintf("重命名失败：%v", err))
	}
	if r.Reload(id) == nil {
		return bridgeErr("TOOL_RENAME_FAILED", "重命名后工具无法加载")
	}
	return nil
}

func (r *ToolRegistry) SwitchVersion(id, version string) (*LoadedTool, error) {
	tool := r.Find(id)
	if tool == nil {
		return nil, bridgeErr("TOOL_NOT_FOUND", fmt.Sprintf("未找到工具 '%s'", id))
	}
	if err := activateVersion(tool.Directory, tool.Manifest.Version, version); err != nil {
		return nil, err
	}
	r.Rescan()
	updated := r.Find(id)
	if updated == nil || !updated.IsValid {
		return nil, bridgeErr("VERSION_SWITCH_FAILED", "切换后的版本未通过校验")
	}
	return updated, nil
}

func (r *ToolRegistry) DeleteVersion(id, version string) error {
	tool := r.Find(id)
	if tool == nil {
		return bridgeErr("TOOL_NOT_FOUND", fmt.Sprintf("未找到工具 '%s'", id))
	}
	if version == tool.Manifest.Version {
		return bridgeErr("VERSION_DELETE_FAILED", "不能删除正在运行的版本")
	}
	if !validVersion.MatchString(version) {
		return bridgeErr("VERSION_INVALID", fmt.Sprintf("非法版本号 '%s'", version))
	}
	target := filepath.Join(tool.Directory, ".versions", version)
	if _, err := os.Stat(target); err != nil {
		return bridgeErr("VERSION_NOT_FOUND", fmt.Sprintf("版本 %s 不存在", version))
	}
	if err := os.RemoveAll(target); err != nil {
		return bridgeErr("VERSION_DELETE_FAILED", fmt.Sprintf("删除失败：%v", err))
	}
	return nil
}

func normalizeManifest(manifest *ToolManifest) {
	if manifest.Version == "" {
		manifest.Version = "1.0.0"
	}
	if manifest.Entry == "" {
		manifest.Entry = "index.html"
	}
	if manifest.Window.Width == 0 {
		manifest.Window.Width = 720
	}
	if manifest.Window.Height == 0 {
		manifest.Window.Height = 720
	}
}

func validateManifest(manifest *ToolManifest, directoryName string) []string {
	var errors []string
	if !validToolID.MatchString(manifest.ID) {
		errors = append(errors, "id 必须匹配小写字母/数字/短横线规则（1~63 字符）")
	} else if manifest.ID != directoryName {
		errors = append(errors, fmt.Sprintf("id(%s) 必须与工具目录名(%s) 一致", manifest.ID, directoryName))
	}
	if strings.TrimSpace(manifest.Name) == "" || len([]rune(manifest.Name)) > 60 {
		errors = append(errors, "name 必填且不超过 60 字符")
	}
	if !validVersion.MatchString(manifest.Version) {
		errors = append(errors, "version 必填且为语义化版本（如 1.0.0）")
	}
	if manifest.Description != nil && len([]rune(*manifest.Description)) > 300 {
		errors = append(errors, "description 不超过 300 字符")
	}
	if manifest.Category != nil && len([]rune(*manifest.Category)) > 30 {
		errors = append(errors, "category 不超过 30 字符")
	}
	if manifest.Glyph != nil && len([]rune(*manifest.Glyph)) > 4 {
		errors = append(errors, "glyph 不超过 4 字符")
	}
	if !isSafeRelativePath(manifest.Entry) {
		errors = append(errors, "entry 必须是工具目录内的相对路径（禁止 ..、绝对路径、盘符、反斜杠）")
	}
	if manifest.CSP != nil && (strings.ContainsAny(*manifest.CSP, "\r\n") || len(*manifest.CSP) > 4096) {
		errors = append(errors, "csp 不能包含换行且不超过 4096 字符")
	}
	for _, permission := range manifest.Permissions {
		if !knownPermissions[strings.ToLower(strings.TrimSpace(permission))] {
			errors = append(errors, fmt.Sprintf("未知权限 '%s'（仅支持 storage / ai / media / files）", permission))
		}
	}
	if manifest.Window.Width < 320 || manifest.Window.Width > 3840 ||
		manifest.Window.Height < 320 || manifest.Window.Height > 3840 {
		errors = append(errors, "window.width/height 必须在 320~3840 之间")
	}
	if _, err := time.Parse(time.RFC3339, manifest.CreatedAt); err != nil {
		errors = append(errors, "createdAt 必填且为 ISO-8601 日期时间")
	}
	return errors
}

func normalizePermissions(values []string) []string {
	result := []string{}
	seen := map[string]bool{}
	for _, value := range values {
		normalized := strings.ToLower(strings.TrimSpace(value))
		if knownPermissions[normalized] && !seen[normalized] {
			seen[normalized] = true
			result = append(result, normalized)
		}
	}
	return result
}

func detectPermissions(html string) []string {
	result := []string{}
	if strings.Contains(html, "window.host.storage") || strings.Contains(html, "host.storage") {
		result = append(result, "storage")
	}
	if strings.Contains(html, "window.host.ai") || strings.Contains(html, "host.ai") {
		result = append(result, "ai")
	}
	if strings.Contains(html, "window.host.files") || strings.Contains(html, "host.files") {
		result = append(result, "files")
	}
	if strings.Contains(html, "getUserMedia") || strings.Contains(html, "getDisplayMedia") {
		result = append(result, "media")
	}
	return result
}

func isSafeRelativePath(value string) bool {
	if strings.TrimSpace(value) == "" || strings.Contains(value, "\\") || strings.ContainsRune(value, 0) ||
		strings.HasPrefix(value, "/") || (len(value) > 1 && value[1] == ':') {
		return false
	}
	segments := strings.Split(value, "/")
	nonEmpty := 0
	for _, segment := range segments {
		if segment == "" {
			continue
		}
		nonEmpty++
		if strings.HasPrefix(segment, ".") || strings.ContainsRune(segment, 0) {
			return false
		}
	}
	return nonEmpty > 0
}

func fileModifiedUTC(path string) string {
	info, err := os.Stat(path)
	if err != nil {
		return ""
	}
	return info.ModTime().UTC().Format(time.RFC3339)
}

func listVersions(toolDir, active string) []ToolVersionView {
	root := filepath.Join(toolDir, ".versions")
	entries, err := os.ReadDir(root)
	if err != nil {
		return nil
	}
	result := []ToolVersionView{}
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		version := entry.Name()
		if !validVersion.MatchString(version) {
			continue
		}
		dir := filepath.Join(root, version)
		created := ""
		if info, err := os.Stat(filepath.Join(dir, "manifest.json")); err == nil {
			created = info.ModTime().UTC().Format(time.RFC3339)
		}
		var note *string
		if data, err := os.ReadFile(filepath.Join(dir, "note.txt")); err == nil && strings.TrimSpace(string(data)) != "" {
			text := strings.TrimSpace(string(data))
			note = &text
		}
		result = append(result, ToolVersionView{
			Version: version, CreatedAt: created, SizeBytes: directorySize(dir),
			IsActive: version == active, Note: note,
		})
	}
	sort.Slice(result, func(i, j int) bool {
		return compareVersion(result[i].Version, result[j].Version) > 0
	})
	return result
}

func compareVersion(a, b string) int {
	parse := func(value string) [3]int {
		var out [3]int
		core := strings.SplitN(value, "-", 2)[0]
		parts := strings.Split(core, ".")
		for i := 0; i < 3 && i < len(parts); i++ {
			out[i], _ = strconv.Atoi(parts[i])
		}
		return out
	}
	left, right := parse(a), parse(b)
	for i := 0; i < 3; i++ {
		if left[i] != right[i] {
			if left[i] > right[i] {
				return 1
			}
			return -1
		}
	}
	return strings.Compare(a, b)
}

func directorySize(dir string) int64 {
	var size int64
	_ = filepath.WalkDir(dir, func(path string, entry fs.DirEntry, err error) error {
		if err == nil && !entry.IsDir() {
			if info, statErr := entry.Info(); statErr == nil {
				size += info.Size()
			}
		}
		return nil
	})
	return size
}

func snapshotVersion(toolDir, version string, note string) error {
	if !validVersion.MatchString(version) {
		return bridgeErr("VERSION_INVALID", fmt.Sprintf("非法版本号 '%s'", version))
	}
	target := filepath.Join(toolDir, ".versions", version)
	if _, err := os.Stat(target); err == nil {
		return nil
	}
	if err := os.MkdirAll(target, 0o755); err != nil {
		return bridgeErr("VERSION_SNAPSHOT_FAILED", err.Error())
	}
	for _, name := range []string{"manifest.json", "index.html"} {
		source := filepath.Join(toolDir, name)
		if data, err := os.ReadFile(source); err == nil {
			_ = os.WriteFile(filepath.Join(target, name), data, 0o644)
		}
	}
	if strings.TrimSpace(note) != "" {
		_ = os.WriteFile(filepath.Join(target, "note.txt"), []byte(note), 0o644)
	}
	return nil
}

func activateVersion(toolDir, current, targetVersion string) error {
	if !validVersion.MatchString(targetVersion) {
		return bridgeErr("VERSION_INVALID", fmt.Sprintf("非法版本号 '%s'", targetVersion))
	}
	source := filepath.Join(toolDir, ".versions", targetVersion)
	if info, err := os.Stat(source); err != nil || !info.IsDir() {
		return bridgeErr("VERSION_NOT_FOUND", fmt.Sprintf("版本 %s 不存在", targetVersion))
	}
	if current != targetVersion {
		_ = snapshotVersion(toolDir, current, "")
	}
	for _, name := range []string{"manifest.json", "index.html"} {
		from := filepath.Join(source, name)
		if data, err := os.ReadFile(from); err == nil {
			to := filepath.Join(toolDir, name)
			temp := to + ".switch-tmp"
			if err := os.WriteFile(temp, data, 0o644); err != nil {
				return bridgeErr("VERSION_SWITCH_FAILED", err.Error())
			}
			if err := os.Rename(temp, to); err != nil {
				return bridgeErr("VERSION_SWITCH_FAILED", err.Error())
			}
		}
	}
	return nil
}

func nextVersion(versions []ToolVersionView, current, bump string) string {
	base := current
	if len(versions) > 0 && versions[0].Version != "" {
		base = versions[0].Version
	}
	parts := strings.Split(strings.SplitN(base, "-", 2)[0], ".")
	nums := []int{0, 0, 0}
	for i := 0; i < 3 && i < len(parts); i++ {
		nums[i], _ = strconv.Atoi(parts[i])
	}
	switch bump {
	case "major":
		nums[0]++
		nums[1], nums[2] = 0, 0
	case "minor":
		nums[1]++
		nums[2] = 0
	default:
		nums[2]++
	}
	return fmt.Sprintf("%d.%d.%d", nums[0], nums[1], nums[2])
}

func buildSearchKey(name, id, extra string) string {
	source := strings.ToLower(strings.Join([]string{name, id, extra}, " "))
	args := pinyin.NewArgs()
	args.Heteronym = false
	args.Style = pinyin.Normal
	rows := pinyin.Pinyin(name, args)
	var initials, compact, syllables []string
	for _, row := range rows {
		if len(row) == 0 {
			continue
		}
		syllable := strings.ToLower(row[0])
		syllables = append(syllables, syllable)
		compact = append(compact, syllable)
		initials = append(initials, string([]rune(syllable)[0]))
	}
	parts := []string{source, strings.Join(initials, ""), strings.Join(compact, ""), strings.Join(syllables, " ")}
	return strings.Join(parts, " ")
}

func buildIDFromTarget(target string) string {
	sum := sha256.Sum256([]byte(strings.ToLower(target)))
	return "idx-" + hex.EncodeToString(sum[:])[:12]
}

func seedSampleTools(paths *AppPaths) {
	root := assetFS()
	entries, err := fs.ReadDir(root, "samples")
	if err != nil {
		return
	}
	seeded := []string{}
	for _, entry := range entries {
		if !entry.IsDir() || strings.HasPrefix(entry.Name(), ".") {
			continue
		}
		id := entry.Name()
		target := paths.ToolDir(id)
		if _, err := os.Stat(filepath.Join(target, "manifest.json")); err == nil {
			continue
		}
		if err := os.MkdirAll(target, 0o755); err != nil {
			continue
		}
		_ = fs.WalkDir(root, "samples/"+id, func(path string, item fs.DirEntry, err error) error {
			if err != nil || item.IsDir() {
				return nil
			}
			relative := strings.TrimPrefix(path, "samples/"+id+"/")
			destination := filepath.Join(target, filepath.FromSlash(relative))
			_ = os.MkdirAll(filepath.Dir(destination), 0o755)
			if data, err := fs.ReadFile(root, path); err == nil {
				_ = os.WriteFile(destination, data, 0o644)
			}
			return nil
		})
		seeded = append(seeded, id)
	}
	if len(seeded) > 0 {
		logInfo("已写入示例工具：%s", strings.Join(seeded, ", "))
	}
}
