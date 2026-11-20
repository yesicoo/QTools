package main

import (
	"archive/zip"
	"context"
	"encoding/json"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/egoist/mygo"
)

type itemExportManifest struct {
	Kind          string  `json:"kind"`
	ID            string  `json:"id"`
	Name          string  `json:"name"`
	Version       *string `json:"version,omitempty"`
	ExportedAtUTC string  `json:"exportedAtUtc"`
	AppVersion    string  `json:"appVersion"`
}

func exportItem(state *AppState, parent *mygo.Window, kind, id string) (any, error) {
	name := ""
	version := ""
	sourceDir := ""
	payloadPrefix := ""
	switch kind {
	case "tool":
		tool := state.Tools.Find(id)
		if tool == nil {
			return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
		}
		name, version = tool.Name(), tool.Manifest.Version
		sourceDir = tool.Directory
		payloadPrefix = "tools/" + id
	case "app":
		app := state.Apps.Find(id)
		if app == nil {
			return nil, bridgeErr("APP_NOT_FOUND", "启动项不存在")
		}
		name, sourceDir, payloadPrefix = app.Name(), app.Directory, "apps/"+id
	case "web":
		web := state.WebTools.Find(id)
		if web == nil {
			return nil, bridgeErr("WEB_NOT_FOUND", "在线工具不存在")
		}
		name, sourceDir, payloadPrefix = web.Name(), web.Directory, "webtools/"+id
	default:
		return nil, bridgeErr("EXPORT_KIND_INVALID", "不支持的导出类型")
	}
	safeName := sanitizeFileStem(name)
	if safeName == "" {
		safeName = id
	}
	path, err := mygo.Dialog.Save(mygo.SaveDialogOptions{
		Parent: parent, Title: "导出 QTools 条目",
		DefaultPath: safeName + ".qtools",
		Filters:     []mygo.FileFilter{{Name: "QTools 导出包", Extensions: []string{"qtools"}}},
	})
	if err != nil {
		return nil, err
	}
	if path == "" {
		return nil, nil
	}
	if !strings.HasSuffix(strings.ToLower(path), ".qtools") {
		path += ".qtools"
	}
	temp := path + ".tmp"
	file, err := os.Create(temp)
	if err != nil {
		return nil, bridgeErr("EXPORT_FAILED", err.Error())
	}
	writer := zip.NewWriter(file)
	writeErr := func() error {
		manifest := itemExportManifest{
			Kind: kind, ID: id, Name: name, ExportedAtUTC: time.Now().UTC().Format(time.RFC3339),
			AppVersion: appVersion,
		}
		if version != "" {
			manifest.Version = &version
		}
		if err := writeZipJSON(writer, "qtools-export.json", manifest); err != nil {
			return err
		}
		return filepath.WalkDir(sourceDir, func(current string, entry fs.DirEntry, err error) error {
			if err != nil {
				return nil
			}
			relative, err := filepath.Rel(sourceDir, current)
			if err != nil || relative == "." {
				return nil
			}
			relative = filepath.ToSlash(relative)
			for _, segment := range strings.Split(relative, "/") {
				if strings.HasPrefix(segment, ".") {
					if entry.IsDir() {
						return filepath.SkipDir
					}
					return nil
				}
			}
			if entry.IsDir() {
				return nil
			}
			if kind == "app" && relative != "app.json" && relative != "icon.png" {
				return nil
			}
			if kind == "web" && relative != "web.json" {
				return nil
			}
			return writeZipFile(writer, payloadPrefix+"/"+relative, current)
		})
	}()
	if writeErr == nil {
		writeErr = writer.Close()
	} else {
		_ = writer.Close()
	}
	_ = file.Close()
	if writeErr != nil {
		_ = os.Remove(temp)
		return nil, bridgeErr("EXPORT_FAILED", writeErr.Error())
	}
	if err := os.Rename(temp, path); err != nil {
		_ = os.Remove(temp)
		return nil, bridgeErr("EXPORT_FAILED", err.Error())
	}
	return map[string]any{"path": path, "fileName": filepath.Base(path)}, nil
}

func writeZipJSON(writer *zip.Writer, name string, value any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	entry, err := writer.Create(name)
	if err != nil {
		return err
	}
	_, err = entry.Write(data)
	return err
}

func writeZipFile(writer *zip.Writer, name, source string) error {
	input, err := os.Open(source)
	if err != nil {
		return err
	}
	defer input.Close()
	info, err := input.Stat()
	if err != nil {
		return err
	}
	header, err := zip.FileInfoHeader(info)
	if err != nil {
		return err
	}
	header.Name = name
	header.Method = zip.Deflate
	entry, err := writer.CreateHeader(header)
	if err != nil {
		return err
	}
	_, err = io.Copy(entry, input)
	return err
}

func pickImportFile(ctx context.Context) (any, error) {
	parent := mygo.CallerWindow(ctx)
	files, err := mygo.Dialog.Open(mygo.OpenDialogOptions{
		Parent: parent, Title: "选择要导入的文件",
		Filters: []mygo.FileFilter{
			{Name: "HTML 或 QTools 导出包", Extensions: []string{"html", "htm", "qtools", "zip"}},
		},
	})
	if err != nil {
		return nil, err
	}
	if len(files) == 0 {
		return nil, nil
	}
	path := files[0]
	info, err := os.Stat(path)
	if err != nil {
		return nil, bridgeErr("IMPORT_FILE_NOT_FOUND", "文件不存在")
	}
	if info.Size() > 100*1024*1024 {
		return nil, bridgeErr("IMPORT_FILE_TOO_LARGE", "导入文件过大")
	}
	ext := strings.ToLower(filepath.Ext(path))
	fileName := filepath.Base(path)
	if ext == ".html" || ext == ".htm" {
		data, err := os.ReadFile(path)
		if err != nil {
			return nil, err
		}
		if len(data) > 512*1024 {
			return nil, bridgeErr("IMPORT_HTML_TOO_LARGE", "HTML 文件过大，上限 512 KB")
		}
		return map[string]any{
			"path": path, "fileName": fileName, "kind": "html",
			"name": strings.TrimSuffix(fileName, filepath.Ext(fileName)), "html": string(data),
		}, nil
	}
	if ext == ".qtools" || ext == ".zip" {
		manifest, hasBackup, err := readPackageManifest(path)
		if err != nil {
			return nil, err
		}
		if hasBackup {
			return nil, bridgeErr("IMPORT_BACKUP_PACKAGE", "这是整机备份包，请到「设置 → 备份与恢复」导入")
		}
		return map[string]any{
			"path": path, "fileName": fileName, "kind": "package",
			"itemKind": manifest.Kind, "name": manifest.Name,
		}, nil
	}
	return nil, bridgeErr("IMPORT_FILE_UNSUPPORTED", "仅支持 .html / .qtools / .zip 文件")
}

func readPackageManifest(path string) (itemExportManifest, bool, error) {
	var manifest itemExportManifest
	reader, err := zip.OpenReader(path)
	if err != nil {
		return manifest, false, bridgeErr("PACKAGE_INVALID", "导出包无法读取")
	}
	defer reader.Close()
	hasBackup := false
	hasManifest := false
	for _, file := range reader.File {
		if file.Name == "backup.json" {
			hasBackup = true
		}
		if file.Name == "qtools-export.json" {
			stream, err := file.Open()
			if err != nil {
				return manifest, hasBackup, bridgeErr("PACKAGE_INVALID", "导出包无法读取")
			}
			err = json.NewDecoder(stream).Decode(&manifest)
			stream.Close()
			if err != nil {
				return manifest, hasBackup, bridgeErr("PACKAGE_INVALID", "导出包清单不是合法 JSON")
			}
			hasManifest = true
		}
	}
	if hasBackup {
		return manifest, true, nil
	}
	if !hasManifest {
		return manifest, false, bridgeErr("PACKAGE_INVALID", "不是 QTools 导出包（缺少 qtools-export.json）")
	}
	return manifest, false, nil
}

func importItemPackage(state *AppState, path string, accept bool) (any, error) {
	if _, err := os.Stat(path); err != nil {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包不存在")
	}
	manifest, hasBackup, err := readPackageManifest(path)
	if err != nil {
		return nil, err
	}
	if hasBackup {
		return nil, bridgeErr("IMPORT_BACKUP_PACKAGE", "这是整机备份包，请到「设置 → 备份与恢复」导入")
	}
	switch manifest.Kind {
	case "tool":
		return importToolPackage(state, path, manifest, accept)
	case "app":
		return importAppPackage(state, path, manifest)
	case "web":
		return importWebPackage(state, path, manifest)
	default:
		return nil, bridgeErr("PACKAGE_INVALID", "导出包类型不受支持")
	}
}

func importToolPackage(state *AppState, path string, manifest itemExportManifest, accept bool) (any, error) {
	reader, err := zip.OpenReader(path)
	if err != nil {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包无法读取")
	}
	defer reader.Close()
	prefix := "tools/" + manifest.ID + "/"
	files := map[string][]byte{}
	var toolManifest ToolManifest
	hasManifest := false
	for _, file := range reader.File {
		name := strings.ReplaceAll(file.Name, "\\", "/")
		if !strings.HasPrefix(name, prefix) || strings.HasSuffix(name, "/") {
			continue
		}
		relative := strings.TrimPrefix(name, prefix)
		for _, segment := range strings.Split(relative, "/") {
			if segment == "" || segment == "." || segment == ".." || strings.HasPrefix(segment, ".") {
				return nil, bridgeErr("PACKAGE_INVALID", "导出包包含非法路径："+file.Name)
			}
		}
		stream, err := file.Open()
		if err != nil {
			return nil, bridgeErr("PACKAGE_INVALID", "读取导出包失败")
		}
		data, err := io.ReadAll(io.LimitReader(stream, 512*1024+1))
		stream.Close()
		if err != nil {
			return nil, bridgeErr("PACKAGE_INVALID", "读取导出包失败")
		}
		if len(data) > 512*1024 && relative != "manifest.json" {
			return nil, bridgeErr("PACKAGE_INVALID", "导出包内文件过大："+relative)
		}
		files[relative] = data
		if relative == "manifest.json" {
			if err := json.Unmarshal(data, &toolManifest); err != nil {
				return nil, bridgeErr("PACKAGE_INVALID", "工具清单不是合法 JSON")
			}
			hasManifest = true
		}
	}
	if !hasManifest {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包缺少 tools/"+manifest.ID+"/manifest.json")
	}
	normalizeManifest(&toolManifest)
	html := string(files["index.html"])
	if strings.TrimSpace(html) == "" {
		if entry := toolManifest.Entry; files[entry] != nil {
			html = string(files[entry])
		}
	}
	if strings.TrimSpace(html) == "" {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包缺少 index.html")
	}
	findings, hasFatal := scanContent(&toolManifest, html)
	if hasFatal {
		return nil, bridgeErr("IMPORT_REJECTED", "导出包中的工具未通过安全审查")
	}
	requires := false
	for _, finding := range findings {
		if finding.Severity == 1 {
			requires = true
		}
	}
	newID := generateSlug(toolManifest.Name, "imported-", state.Tools.Contains)
	if requires && !accept {
		return map[string]any{
			"saved": false, "kind": "tool", "itemId": newID, "itemName": toolManifest.Name,
			"findings": findings, "hasFatal": false, "requiresExplicitAcceptance": true,
			"permissions": toolManifest.Permissions,
		}, nil
	}
	toolManifest.ID = newID
	toolManifest.CreatedAt = time.Now().Format(time.RFC3339)
	directory := state.Paths.ToolDir(newID)
	if err := os.MkdirAll(directory, 0o755); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	for relative, data := range files {
		if relative == "manifest.json" {
			continue
		}
		target := filepath.Join(directory, filepath.FromSlash(relative))
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			return nil, bridgeErr("IMPORT_REJECTED", err.Error())
		}
		if err := os.WriteFile(target, data, 0o644); err != nil {
			return nil, bridgeErr("IMPORT_REJECTED", err.Error())
		}
	}
	if err := writeManifest(directory, toolManifest); err != nil {
		return nil, err
	}
	state.Tools.Rescan()
	loaded := state.Tools.Find(newID)
	if loaded == nil || !loaded.IsValid {
		_ = os.RemoveAll(directory)
		return nil, bridgeErr("IMPORT_REJECTED", "导入后的工具未通过校验")
	}
	state.Usage.EnsureSeeded([]string{newID})
	pushHostEvent(state, "launcher-changed")
	return map[string]any{
		"saved": true, "kind": "tool", "itemId": newID, "itemName": toolManifest.Name,
		"findings": findings, "hasFatal": false, "requiresExplicitAcceptance": false,
		"permissions": toolManifest.Permissions,
	}, nil
}

func importAppPackage(state *AppState, path string, manifest itemExportManifest) (any, error) {
	reader, err := zip.OpenReader(path)
	if err != nil {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包无法读取")
	}
	defer reader.Close()
	var entry AppEntry
	hasApp := false
	for _, file := range reader.File {
		if file.Name == "apps/"+manifest.ID+"/app.json" {
			stream, err := file.Open()
			if err != nil {
				return nil, bridgeErr("PACKAGE_INVALID", "读取导出包失败")
			}
			err = json.NewDecoder(stream).Decode(&entry)
			stream.Close()
			if err != nil {
				return nil, bridgeErr("PACKAGE_INVALID", "app.json 不是合法 JSON")
			}
			hasApp = true
		}
	}
	if !hasApp {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包缺少 app.json")
	}
	newID := generateSlug(entry.Name, "app", state.Apps.Contains)
	entry.ID = newID
	entry.CreatedAt = time.Now().Format(time.RFC3339)
	if errors := validateAppEntry(&entry, newID); len(errors) > 0 {
		return nil, bridgeErr("IMPORT_REJECTED", strings.Join(errors, "；"))
	}
	dir := state.Paths.AppDir(newID)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	if err := writeJSON(filepath.Join(dir, "app.json"), entry); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	state.Apps.Rescan()
	state.Usage.EnsureSeeded([]string{newID})
	pushHostEvent(state, "launcher-changed")
	return map[string]any{"saved": true, "kind": "app", "itemId": newID, "itemName": entry.Name}, nil
}

func importWebPackage(state *AppState, path string, manifest itemExportManifest) (any, error) {
	reader, err := zip.OpenReader(path)
	if err != nil {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包无法读取")
	}
	defer reader.Close()
	var entry WebToolEntry
	hasWeb := false
	for _, file := range reader.File {
		if file.Name == "webtools/"+manifest.ID+"/web.json" {
			stream, err := file.Open()
			if err != nil {
				return nil, bridgeErr("PACKAGE_INVALID", "读取导出包失败")
			}
			err = json.NewDecoder(stream).Decode(&entry)
			stream.Close()
			if err != nil {
				return nil, bridgeErr("PACKAGE_INVALID", "web.json 不是合法 JSON")
			}
			hasWeb = true
		}
	}
	if !hasWeb {
		return nil, bridgeErr("PACKAGE_INVALID", "导出包缺少 web.json")
	}
	newID := generateSlug(entry.Name, "web", state.WebTools.Contains)
	entry.ID = newID
	entry.CreatedAt = time.Now().Format(time.RFC3339)
	if errors := validateWebEntry(&entry, newID); len(errors) > 0 {
		return nil, bridgeErr("IMPORT_REJECTED", strings.Join(errors, "；"))
	}
	dir := state.Paths.WebToolDir(newID)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	if err := writeJSON(filepath.Join(dir, "web.json"), entry); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	state.WebTools.Rescan()
	state.Usage.EnsureSeeded([]string{newID})
	pushHostEvent(state, "launcher-changed")
	return map[string]any{"saved": true, "kind": "web", "itemId": newID, "itemName": entry.Name}, nil
}

func sanitizeFileStem(value string) string {
	value = strings.TrimSpace(value)
	replacer := strings.NewReplacer("<", "_", ">", "_", ":", "_", `"`, "_", "/", "_", "\\", "_", "|", "_", "?", "_", "*", "_")
	value = replacer.Replace(value)
	for _, ch := range value {
		if ch < 0x20 {
			return ""
		}
	}
	value = strings.Trim(value, ". ")
	if len([]rune(value)) > 80 {
		value = string([]rune(value)[:80])
	}
	return value
}
