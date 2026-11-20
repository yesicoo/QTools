package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

type AppEntry struct {
	ID               string  `json:"id"`
	Name             string  `json:"name"`
	Target           string  `json:"target"`
	Arguments        *string `json:"arguments,omitempty"`
	WorkingDirectory *string `json:"workingDirectory,omitempty"`
	Category         *string `json:"category,omitempty"`
	Glyph            *string `json:"glyph,omitempty"`
	CreatedAt        string  `json:"createdAt"`
}

type LoadedApp struct {
	Entry     AppEntry
	Directory string
	IsValid   bool
	Errors    []string
}

func (a *LoadedApp) Name() string {
	if strings.TrimSpace(a.Entry.Name) == "" {
		return a.Entry.ID
	}
	return a.Entry.Name
}

func (a *LoadedApp) TargetExists() bool {
	_, err := os.Stat(a.Entry.Target)
	return err == nil
}

type AppRegistry struct {
	mu   sync.RWMutex
	dir  string
	apps map[string]*LoadedApp
}

func NewAppRegistry(dir string) *AppRegistry {
	r := &AppRegistry{dir: dir, apps: map[string]*LoadedApp{}}
	r.Rescan()
	return r
}

func (r *AppRegistry) Rescan() {
	apps := map[string]*LoadedApp{}
	entries, err := os.ReadDir(r.dir)
	if err == nil {
		for _, entry := range entries {
			if !entry.IsDir() || strings.HasPrefix(entry.Name(), ".") {
				continue
			}
			path := filepath.Join(r.dir, entry.Name(), "app.json")
			text, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			var appEntry AppEntry
			if json.Unmarshal(text, &appEntry) != nil {
				continue
			}
			errors := validateAppEntry(&appEntry, entry.Name())
			apps[appEntry.ID] = &LoadedApp{
				Entry: appEntry, Directory: filepath.Join(r.dir, entry.Name()),
				IsValid: len(errors) == 0, Errors: errors,
			}
		}
	}
	r.mu.Lock()
	r.apps = apps
	r.mu.Unlock()
}

func (r *AppRegistry) All() []*LoadedApp {
	r.mu.RLock()
	defer r.mu.RUnlock()
	result := make([]*LoadedApp, 0, len(r.apps))
	for _, app := range r.apps {
		result = append(result, app)
	}
	sort.Slice(result, func(i, j int) bool {
		return strings.ToLower(result[i].Name()) < strings.ToLower(result[j].Name())
	})
	return result
}

func (r *AppRegistry) Find(id string) *LoadedApp {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.apps[id]
}

func (r *AppRegistry) Contains(id string) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	_, ok := r.apps[id]
	return ok
}

func (r *AppRegistry) Remove(id string) {
	r.mu.Lock()
	delete(r.apps, id)
	r.mu.Unlock()
}

func validateAppEntry(entry *AppEntry, directory string) []string {
	var errors []string
	if !validToolID.MatchString(entry.ID) || entry.ID != directory {
		errors = append(errors, "id 必须与目录名一致且符合小写字母/数字/短横线规则")
	}
	if strings.TrimSpace(entry.Name) == "" || len([]rune(entry.Name)) > 60 {
		errors = append(errors, "name 必填且不超过 60 字符")
	}
	if strings.TrimSpace(entry.Target) == "" || strings.ContainsRune(entry.Target, 0) {
		errors = append(errors, "target 必填且不能包含非法字符")
	}
	if entry.Arguments != nil && len([]rune(*entry.Arguments)) > 2000 {
		errors = append(errors, "arguments 不超过 2000 字符")
	}
	if entry.Category != nil && len([]rune(*entry.Category)) > 30 {
		errors = append(errors, "category 不超过 30 字符")
	}
	if entry.Glyph != nil && len([]rune(*entry.Glyph)) > 4 {
		errors = append(errors, "glyph 不超过 4 字符")
	}
	return errors
}

func (r *AppRegistry) Add(name, target, arguments, workingDirectory, category, glyph string) (*LoadedApp, error) {
	name = strings.TrimSpace(name)
	target = strings.TrimSpace(target)
	if name == "" {
		return nil, bridgeErr("APP_NAME_REQUIRED", "请填写程序名称")
	}
	if len([]rune(name)) > 60 {
		return nil, bridgeErr("APP_NAME_TOO_LONG", "程序名称不超过 60 字符")
	}
	if target == "" {
		return nil, bridgeErr("APP_TARGET_REQUIRED", "请填写程序路径")
	}
	id := generateSlug(name, "app", r.Contains)
	entry := AppEntry{
		ID: id, Name: name, Target: target,
		Arguments: optionalText(arguments), WorkingDirectory: optionalText(workingDirectory),
		Category: optionalText(category), Glyph: optionalText(glyph),
		CreatedAt: time.Now().Format(time.RFC3339),
	}
	errors := validateAppEntry(&entry, id)
	if len(errors) > 0 {
		return nil, bridgeErr("APP_INVALID", strings.Join(errors, "；"))
	}
	dir := filepath.Join(r.dir, id)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, bridgeErr("APP_WRITE_FAILED", err.Error())
	}
	if err := writeJSON(filepath.Join(dir, "app.json"), entry); err != nil {
		return nil, bridgeErr("APP_WRITE_FAILED", err.Error())
	}
	r.Rescan()
	app := r.Find(id)
	if app == nil || !app.IsValid {
		return nil, bridgeErr("APP_INVALID", "启动项未通过校验")
	}
	return app, nil
}

func (r *AppRegistry) Delete(id string) (bool, error) {
	app := r.Find(id)
	if app == nil {
		return false, nil
	}
	if err := os.RemoveAll(app.Directory); err != nil {
		return false, bridgeErr("APP_DELETE_FAILED", fmt.Sprintf("删除失败：%v", err))
	}
	r.Remove(id)
	return true, nil
}

type WebToolEntry struct {
	ID          string  `json:"id"`
	Name        string  `json:"name"`
	URL         string  `json:"url"`
	Category    *string `json:"category,omitempty"`
	Glyph       *string `json:"glyph,omitempty"`
	CreatedAt   string  `json:"createdAt"`
	Description *string `json:"description,omitempty"`
}

type LoadedWebTool struct {
	Entry     WebToolEntry
	Directory string
	IsValid   bool
	Errors    []string
}

func (w *LoadedWebTool) Name() string {
	if strings.TrimSpace(w.Entry.Name) == "" {
		return w.Entry.ID
	}
	return w.Entry.Name
}

type WebToolRegistry struct {
	mu    sync.RWMutex
	dir   string
	tools map[string]*LoadedWebTool
}

func NewWebToolRegistry(dir string) *WebToolRegistry {
	r := &WebToolRegistry{dir: dir, tools: map[string]*LoadedWebTool{}}
	r.Rescan()
	return r
}

func (r *WebToolRegistry) Rescan() {
	tools := map[string]*LoadedWebTool{}
	entries, err := os.ReadDir(r.dir)
	if err == nil {
		for _, entry := range entries {
			if !entry.IsDir() || strings.HasPrefix(entry.Name(), ".") {
				continue
			}
			text, err := os.ReadFile(filepath.Join(r.dir, entry.Name(), "web.json"))
			if err != nil {
				continue
			}
			var webEntry WebToolEntry
			if json.Unmarshal(text, &webEntry) != nil {
				continue
			}
			errors := validateWebEntry(&webEntry, entry.Name())
			tools[webEntry.ID] = &LoadedWebTool{
				Entry: webEntry, Directory: filepath.Join(r.dir, entry.Name()),
				IsValid: len(errors) == 0, Errors: errors,
			}
		}
	}
	r.mu.Lock()
	r.tools = tools
	r.mu.Unlock()
}

func (r *WebToolRegistry) All() []*LoadedWebTool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	result := make([]*LoadedWebTool, 0, len(r.tools))
	for _, tool := range r.tools {
		result = append(result, tool)
	}
	sort.Slice(result, func(i, j int) bool {
		return strings.ToLower(result[i].Name()) < strings.ToLower(result[j].Name())
	})
	return result
}

func (r *WebToolRegistry) Find(id string) *LoadedWebTool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.tools[id]
}

func (r *WebToolRegistry) Contains(id string) bool {
	r.mu.RLock()
	defer r.mu.RUnlock()
	_, ok := r.tools[id]
	return ok
}

func (r *WebToolRegistry) Remove(id string) {
	r.mu.Lock()
	delete(r.tools, id)
	r.mu.Unlock()
}

func validateWebEntry(entry *WebToolEntry, directory string) []string {
	var errors []string
	if !validToolID.MatchString(entry.ID) || entry.ID != directory {
		errors = append(errors, "id 必须与目录名一致且符合小写字母/数字/短横线规则")
	}
	if strings.TrimSpace(entry.Name) == "" || len([]rune(entry.Name)) > 60 {
		errors = append(errors, "name 必填且不超过 60 字符")
	}
	if entry.Description != nil && len([]rune(*entry.Description)) > 300 {
		errors = append(errors, "description 不超过 300 字符")
	}
	value := strings.TrimSpace(entry.URL)
	if value == "" {
		errors = append(errors, "url 必填（http:// 或 https:// 开头的网址）")
	} else if len(value) > 2048 {
		errors = append(errors, "url 不超过 2048 字符")
	} else if parsed, err := url.Parse(value); err != nil || (parsed.Scheme != "http" && parsed.Scheme != "https") || parsed.Host == "" {
		errors = append(errors, "url 必须是 http:// 或 https:// 开头的合法网址")
	}
	return errors
}

func (r *WebToolRegistry) Add(name, rawURL, category, glyph, description string) (*LoadedWebTool, error) {
	name = strings.TrimSpace(name)
	rawURL = strings.TrimSpace(rawURL)
	if name == "" {
		return nil, bridgeErr("WEB_NAME_REQUIRED", "请填写工具名称")
	}
	if len([]rune(name)) > 60 {
		return nil, bridgeErr("WEB_NAME_TOO_LONG", "工具名称不超过 60 字符")
	}
	if rawURL == "" {
		return nil, bridgeErr("WEB_URL_REQUIRED", "请填写工具网址")
	}
	id := generateSlug(name, "web", r.Contains)
	entry := WebToolEntry{
		ID: id, Name: name, URL: rawURL, Category: optionalText(category),
		Glyph: optionalText(glyph), CreatedAt: time.Now().Format(time.RFC3339),
		Description: optionalText(description),
	}
	errors := validateWebEntry(&entry, id)
	if len(errors) > 0 {
		return nil, bridgeErr("WEB_INVALID", strings.Join(errors, "；"))
	}
	dir := filepath.Join(r.dir, id)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, bridgeErr("WEB_WRITE_FAILED", err.Error())
	}
	if err := writeJSON(filepath.Join(dir, "web.json"), entry); err != nil {
		return nil, bridgeErr("WEB_WRITE_FAILED", err.Error())
	}
	r.Rescan()
	web := r.Find(id)
	if web == nil || !web.IsValid {
		return nil, bridgeErr("WEB_INVALID", "在线工具未通过校验")
	}
	return web, nil
}

func (r *WebToolRegistry) Rename(id, name string) error {
	tool := r.Find(id)
	if tool == nil {
		return bridgeErr("WEB_NOT_FOUND", fmt.Sprintf("未找到在线工具 '%s'", id))
	}
	entry := tool.Entry
	entry.Name = strings.TrimSpace(name)
	errors := validateWebEntry(&entry, id)
	if len(errors) > 0 {
		return bridgeErr("WEB_INVALID", strings.Join(errors, "；"))
	}
	if err := writeJSON(filepath.Join(tool.Directory, "web.json"), entry); err != nil {
		return bridgeErr("WEB_RENAME_FAILED", err.Error())
	}
	r.Rescan()
	return nil
}

func (r *WebToolRegistry) Delete(id string) (bool, error) {
	tool := r.Find(id)
	if tool == nil {
		return false, nil
	}
	if err := os.RemoveAll(tool.Directory); err != nil {
		return false, bridgeErr("WEB_DELETE_FAILED", fmt.Sprintf("删除失败：%v", err))
	}
	r.Remove(id)
	return true, nil
}

type UsageEntry struct {
	ID          string `json:"id"`
	Count       int    `json:"count"`
	LastUsedUTC string `json:"lastUsedUtc"`
}

type usageFile struct {
	SchemaVersion int          `json:"schemaVersion"`
	Entries       []UsageEntry `json:"entries"`
}

type UsageStore struct {
	mu      sync.Mutex
	path    string
	entries map[string]UsageEntry
}

func NewUsageStore(path string) *UsageStore {
	store := &UsageStore{path: path, entries: map[string]UsageEntry{}}
	if data, err := os.ReadFile(path); err == nil {
		var file usageFile
		if json.Unmarshal(data, &file) == nil {
			for _, entry := range file.Entries {
				if entry.ID != "" && entry.Count > 0 {
					store.entries[entry.ID] = entry
				}
			}
		}
	}
	return store
}

func (s *UsageStore) Get(id string) (UsageEntry, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	entry, ok := s.entries[id]
	return entry, ok
}

func (s *UsageStore) Record(id string) {
	if strings.TrimSpace(id) == "" {
		return
	}
	s.mu.Lock()
	entry := s.entries[id]
	entry.ID = id
	entry.Count++
	entry.LastUsedUTC = time.Now().UTC().Format(time.RFC3339)
	s.entries[id] = entry
	s.saveLocked()
	s.mu.Unlock()
}

func (s *UsageStore) EnsureSeeded(ids []string) {
	s.mu.Lock()
	changed := false
	for _, id := range ids {
		if strings.TrimSpace(id) == "" {
			continue
		}
		if _, ok := s.entries[id]; ok {
			continue
		}
		s.entries[id] = UsageEntry{ID: id, Count: 1}
		changed = true
	}
	if changed {
		s.saveLocked()
	}
	s.mu.Unlock()
}

func (s *UsageStore) saveLocked() {
	entries := make([]UsageEntry, 0, len(s.entries))
	for _, entry := range s.entries {
		entries = append(entries, entry)
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].ID < entries[j].ID })
	_ = os.MkdirAll(filepath.Dir(s.path), 0o755)
	if data, err := json.MarshalIndent(usageFile{SchemaVersion: 1, Entries: entries}, "", "  "); err == nil {
		temp := s.path + ".tmp"
		if os.WriteFile(temp, data, 0o644) == nil {
			_ = os.Rename(temp, s.path)
		}
	}
}

type stringSetFile struct {
	IDs  []string `json:"ids"`
	Keys []string `json:"keys"`
}

type HiddenProgramStore struct {
	mu   sync.Mutex
	path string
	ids  map[string]bool
}

func NewHiddenProgramStore(path string) *HiddenProgramStore {
	store := &HiddenProgramStore{path: path, ids: map[string]bool{}}
	if data, err := os.ReadFile(path); err == nil {
		var file stringSetFile
		if json.Unmarshal(data, &file) == nil {
			for _, id := range file.IDs {
				store.ids[id] = true
			}
		}
	}
	return store
}

func (s *HiddenProgramStore) IsHidden(id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.ids[id]
}

func (s *HiddenProgramStore) SetHidden(id string, hidden bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if hidden {
		s.ids[id] = true
	} else {
		delete(s.ids, id)
	}
	ids := make([]string, 0, len(s.ids))
	for key := range s.ids {
		ids = append(ids, key)
	}
	sort.Strings(ids)
	if data, err := json.Marshal(stringSetFile{IDs: ids}); err == nil {
		_ = os.WriteFile(s.path, data, 0o644)
	}
}

type PinnedItemStore struct {
	mu   sync.Mutex
	path string
	keys map[string]bool
}

func NewPinnedItemStore(path string) *PinnedItemStore {
	store := &PinnedItemStore{path: path, keys: map[string]bool{}}
	if data, err := os.ReadFile(path); err == nil {
		var file stringSetFile
		if json.Unmarshal(data, &file) == nil {
			for _, key := range file.Keys {
				store.keys[key] = true
			}
		}
	}
	return store
}

func (s *PinnedItemStore) IsPinned(kind, id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.keys[kind+":"+id]
}

func (s *PinnedItemStore) SetPinned(kind, id string, pinned bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := kind + ":" + id
	if pinned {
		s.keys[key] = true
	} else {
		delete(s.keys, key)
	}
	keys := make([]string, 0, len(s.keys))
	for item := range s.keys {
		keys = append(keys, item)
	}
	sort.Strings(keys)
	if data, err := json.Marshal(stringSetFile{Keys: keys}); err == nil {
		_ = os.WriteFile(s.path, data, 0o644)
	}
}

func (s *PinnedItemStore) Forget(kind, id string) { s.SetPinned(kind, id, false) }

type LauncherItemView struct {
	ID           string  `json:"id"`
	Kind         string  `json:"kind"`
	Name         string  `json:"name"`
	Version      *string `json:"version"`
	IconURL      *string `json:"iconUrl"`
	SearchKey    string  `json:"searchKey"`
	IsValid      bool    `json:"isValid"`
	TargetExists bool    `json:"targetExists"`
	UseCount     int     `json:"useCount"`
	Category     *string `json:"category"`
	Glyph        *string `json:"glyph"`
	Target       *string `json:"target"`
	Description  *string `json:"description"`
	Pinned       bool    `json:"pinned"`
}

type LauncherIndexStatus struct {
	Indexing       bool    `json:"indexing"`
	LastIndexedUTC *string `json:"lastIndexedUtc"`
	ProgramCount   int     `json:"programCount"`
}

type LauncherListView struct {
	Items []LauncherItemView  `json:"items"`
	Index LauncherIndexStatus `json:"index"`
}

type listRow struct {
	view     LauncherItemView
	lastUsed string
}

func buildLauncherList(state *AppState) LauncherListView {
	rows := []listRow{}
	for _, tool := range state.Tools.All() {
		used, ok := state.Usage.Get(tool.Manifest.ID)
		version := tool.Manifest.Version
		rows = append(rows, listRow{
			view: LauncherItemView{
				ID: tool.Manifest.ID, Kind: "tool", Name: tool.Name(), Version: &version,
				SearchKey: buildSearchKey(tool.Name(), tool.Manifest.ID, deref(tool.Manifest.Description)),
				IsValid:   tool.IsValid, TargetExists: true, UseCount: usageCount(used, ok),
				Category: tool.Manifest.Category, Glyph: tool.Manifest.Glyph,
				Description: tool.Manifest.Description,
				Pinned:      state.Pinned.IsPinned("tool", tool.Manifest.ID),
			},
			lastUsed: usageLast(used, ok),
		})
	}
	manualTargets := map[string]bool{}
	for _, app := range state.Apps.All() {
		manualTargets[strings.TrimSpace(app.Entry.Target)] = true
		ensureAppIcon(app)
		iconPath := filepath.Join(app.Directory, "icon.png")
		var iconURL *string
		if _, err := os.Stat(iconPath); err == nil {
			value := "/__host__/app-icon/" + url.PathEscape(app.Entry.ID)
			iconURL = &value
		}
		used, ok := state.Usage.Get(app.Entry.ID)
		target := app.Entry.Target
		rows = append(rows, listRow{
			view: LauncherItemView{
				ID: app.Entry.ID, Kind: "app", Name: app.Name(), IconURL: iconURL,
				SearchKey: buildSearchKey(app.Name(), app.Entry.ID, app.Entry.Target),
				IsValid:   app.IsValid, TargetExists: app.TargetExists(), UseCount: usageCount(used, ok),
				Category: app.Entry.Category, Glyph: app.Entry.Glyph, Target: &target,
				Pinned: state.Pinned.IsPinned("app", app.Entry.ID),
			},
			lastUsed: usageLast(used, ok),
		})
	}
	index := state.ProgramIndex.Snapshot()
	for _, entry := range index.Entries {
		if manualTargets[strings.TrimSpace(entry.Target)] || state.Hidden.IsHidden(entry.ID) {
			continue
		}
		var iconURL *string
		if _, err := os.Stat(filepath.Join(state.Paths.IconCacheDir(), entry.ID+".png")); err == nil {
			value := "/__host__/app-icon/" + url.PathEscape(entry.ID)
			iconURL = &value
		}
		used, ok := state.Usage.Get(entry.ID)
		target := entry.Target
		rows = append(rows, listRow{
			view: LauncherItemView{
				ID: entry.ID, Kind: "program", Name: entry.Name, IconURL: iconURL,
				SearchKey: buildSearchKey(entry.Name, entry.ID, entry.Target), IsValid: true,
				TargetExists: fileExists(entry.Target), UseCount: usageCount(used, ok),
				Category: entry.Category, Target: &target,
				Pinned: state.Pinned.IsPinned("program", entry.ID),
			},
			lastUsed: usageLast(used, ok),
		})
	}
	for _, web := range state.WebTools.All() {
		used, ok := state.Usage.Get(web.Entry.ID)
		target := web.Entry.URL
		extra := web.Entry.URL
		if web.Entry.Description != nil {
			extra = *web.Entry.Description
		}
		rows = append(rows, listRow{
			view: LauncherItemView{
				ID: web.Entry.ID, Kind: "web", Name: web.Name(), IsValid: web.IsValid,
				SearchKey: buildSearchKey(web.Name(), web.Entry.ID, extra), TargetExists: true,
				UseCount: usageCount(used, ok), Category: web.Entry.Category, Glyph: web.Entry.Glyph,
				Target: &target, Description: web.Entry.Description,
				Pinned: state.Pinned.IsPinned("web", web.Entry.ID),
			},
			lastUsed: usageLast(used, ok),
		})
	}
	sort.SliceStable(rows, func(i, j int) bool {
		if rows[i].view.Pinned != rows[j].view.Pinned {
			return rows[i].view.Pinned
		}
		if rows[i].view.UseCount != rows[j].view.UseCount {
			return rows[i].view.UseCount > rows[j].view.UseCount
		}
		if rows[i].lastUsed != rows[j].lastUsed {
			return rows[i].lastUsed > rows[j].lastUsed
		}
		return strings.ToLower(rows[i].view.Name) < strings.ToLower(rows[j].view.Name)
	})
	items := make([]LauncherItemView, 0, len(rows))
	for _, row := range rows {
		items = append(items, row.view)
	}
	return LauncherListView{
		Items: items,
		Index: LauncherIndexStatus{
			Indexing: index.Indexing, LastIndexedUTC: index.LastIndexedUTC,
			ProgramCount: len(index.Entries),
		},
	}
}

func usageCount(entry UsageEntry, ok bool) int {
	if !ok {
		return 0
	}
	return entry.Count
}

func usageLast(entry UsageEntry, ok bool) string {
	if !ok {
		return ""
	}
	return entry.LastUsedUTC
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func optionalText(value string) *string {
	value = strings.TrimSpace(value)
	if value == "" {
		return nil
	}
	return &value
}

func deref(value *string) string {
	if value == nil {
		return ""
	}
	return *value
}

func generateSlug(name, prefix string, exists func(string) bool) string {
	var builder strings.Builder
	lastDash := false
	for _, ch := range strings.ToLower(name) {
		if (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') {
			builder.WriteRune(ch)
			lastDash = false
		} else if !lastDash && builder.Len() > 0 {
			builder.WriteByte('-')
			lastDash = true
		}
	}
	slug := strings.Trim(builder.String(), "-")
	if len(slug) > 63 {
		slug = strings.Trim(slug[:63], "-")
	}
	if slug == "" {
		sum := sha256.Sum256([]byte(name))
		separator := "-"
		if strings.HasSuffix(prefix, "-") {
			separator = ""
		}
		slug = prefix + separator + hex.EncodeToString(sum[:])[:8]
	}
	if !exists(slug) {
		return slug
	}
	for i := 2; i < 1000; i++ {
		candidate := fmt.Sprintf("%s-%d", slug, i)
		if !exists(candidate) {
			return candidate
		}
	}
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s-%d", slug, time.Now().UnixNano())))
	return slug + "-" + hex.EncodeToString(sum[:])[:6]
}

func writeJSON(path string, value any) error {
	data, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	temp := path + ".tmp"
	if err := os.WriteFile(temp, append(data, '\n'), 0o644); err != nil {
		return err
	}
	return os.Rename(temp, path)
}
