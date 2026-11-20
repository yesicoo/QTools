package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/egoist/mygo"
)

const maxBridgeMessage = 1024 * 1024

type BridgeService struct {
	state *AppState
}

type bridgeRequest struct {
	ID      string          `json:"id"`
	ToolID  string          `json:"toolId"`
	Method  string          `json:"method"`
	Token   string          `json:"token"`
	Payload json.RawMessage `json:"payload"`
}

type bridgeCall struct {
	ctx       context.Context
	state     *AppState
	window    *mygo.Window
	role      string
	toolID    string
	requestID string
	payload   map[string]any
	stream    func(delta string, reasoning bool)
}

// Call 是前端 window.external.sendMessage 的唯一入口：
// 出站增量走 channel，最终响应作为返回值交给 go-shim。
func (s *BridgeService) Call(ctx context.Context, raw string, output *mygo.Channel[string]) (string, error) {
	requestID := "unknown"
	if len(raw) > maxBridgeMessage {
		return errorEnvelope(requestID, "BRIDGE_MESSAGE_TOO_LARGE", "单条桥接消息不超过 1 MB"), nil
	}
	var request bridgeRequest
	if err := json.Unmarshal([]byte(raw), &request); err != nil {
		return errorEnvelope(requestID, "BRIDGE_BAD_REQUEST", "消息不是合法 JSON"), nil
	}
	if request.ID == "" || request.Method == "" {
		return errorEnvelope(requestID, "BRIDGE_BAD_REQUEST", "缺少 id 或 method 字段"), nil
	}
	requestID = request.ID
	window := mygo.CallerWindow(ctx)
	if window == nil {
		return errorEnvelope(requestID, "PERMISSION_DENIED", "无法识别调用窗口"), nil
	}
	role := s.state.RoleOf(window)
	if strings.HasPrefix(role, "web:") {
		return errorEnvelope(requestID, "PERMISSION_DENIED", "在线工具窗口不提供宿主桥接"), nil
	}
	if role == "" {
		return errorEnvelope(requestID, "PERMISSION_DENIED", "未知窗口不提供宿主桥接"), nil
	}
	hostWindow := role == "host"
	toolID := "__host__"
	if strings.HasPrefix(role, "tool:") {
		toolID = strings.TrimPrefix(role, "tool:")
	}
	if hostWindow && request.Token != "" {
		resolved := s.state.ResolveEmbedToken(request.Token)
		if resolved == "" {
			return errorEnvelope(requestID, "PERMISSION_DENIED", "内嵌凭证无效或已过期"), nil
		}
		toolID = resolved
	}
	spec, ok := bridgeMethodSpec(request.Method)
	if !ok {
		return errorEnvelope(requestID, "UNKNOWN_METHOD", fmt.Sprintf("未知方法 '%s'", request.Method)), nil
	}
	if toolID == "__host__" {
		if !spec.hostOnly {
			return errorEnvelope(requestID, "PERMISSION_DENIED", "该方法不对宿主管理界面开放"), nil
		}
	} else {
		if spec.hostOnly {
			return errorEnvelope(requestID, "PERMISSION_DENIED", "该方法仅限宿主管理界面调用"), nil
		}
		tool := s.state.Tools.Find(toolID)
		if tool == nil {
			return errorEnvelope(requestID, "TOOL_NOT_FOUND", fmt.Sprintf("未找到工具 '%s'", toolID)), nil
		}
		if spec.permission != "" && !tool.HasPermission(spec.permission) {
			return errorEnvelope(requestID, "PERMISSION_DENIED",
				fmt.Sprintf("权限不足：manifest 未声明 %s", spec.permission)), nil
		}
	}
	payload := map[string]any{}
	if len(request.Payload) > 0 {
		_ = json.Unmarshal(request.Payload, &payload)
	}
	stream := func(delta string, reasoning bool) {
		if output == nil || delta == "" {
			return
		}
		envelope := map[string]any{
			"id":     requestID,
			"stream": map[string]any{"delta": delta, "reasoning": reasoning},
		}
		if data, err := json.Marshal(envelope); err == nil {
			_ = output.Send(string(data))
		}
	}
	call := &bridgeCall{
		ctx: ctx, state: s.state, window: window, role: role,
		toolID: toolID, requestID: requestID, payload: payload, stream: stream,
	}
	childCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	s.state.AddCancel(requestID, cancel)
	defer s.state.RemoveCancel(requestID)
	result, err := call.execute(childCtx, request.Method)
	if err != nil {
		code := errorCode(err)
		if code == "INTERNAL_ERROR" {
			logError("桥接 %s 失败：%v", request.Method, err)
		} else {
			logInfo("桥接 %s -> %s: %s", request.Method, code, errorMessage(err))
		}
		if childCtx.Err() != nil {
			if strings.HasPrefix(request.Method, "manager.generate") ||
				request.Method == "manager.fixTool" || request.Method == "ai.chat" {
				code = "AI_CANCELLED"
			} else {
				code = "REQUEST_CANCELLED"
			}
		}
		return errorEnvelope(requestID, code, errorMessage(err)), nil
	}
	return successEnvelope(requestID, result), nil
}

type methodSpec struct {
	hostOnly   bool
	permission string
}

func bridgeMethodSpec(method string) (methodSpec, bool) {
	spec := func(hostOnly bool, permission string) (methodSpec, bool) {
		return methodSpec{hostOnly: hostOnly, permission: permission}, true
	}
	switch method {
	case "storage.get", "storage.set", "storage.remove", "storage.list":
		return spec(false, "storage")
	case "ai.chat":
		return spec(false, "ai")
	case "files.save", "files.list", "files.read", "files.delete":
		return spec(false, "files")
	case "tool.getManifest", "theme.get", "window.getState", "window.setTopmost",
		"window.setPinned", "window.openVersionManager", "window.saveChipPosition":
		return spec(false, "")
	}
	hostOnlyMethods := map[string]bool{
		"manager.cancelRequest": true, "manager.getServerInfo": true, "manager.listTools": true,
		"manager.openTool": true, "manager.deleteTool": true, "manager.renameTool": true,
		"manager.createToolShortcut": true, "manager.getAiConfig": true, "manager.setAiConfig": true,
		"manager.openSettings": true, "manager.importTool": true, "manager.listVersions": true,
		"manager.switchVersion": true, "manager.deleteVersion": true, "manager.generateUpdate": true,
		"manager.applyUpdate": true, "manager.listApps": true, "manager.launchApp": true,
		"manager.addApp": true, "manager.deleteApp": true, "manager.discoverPrograms": true,
		"manager.pickProgram": true, "manager.launcherList": true, "manager.reindexPrograms": true,
		"manager.addWebTool": true, "manager.deleteWebTool": true, "manager.renameWebTool": true,
		"manager.openWebTool": true, "manager.hideProgram": true, "manager.unhideProgram": true,
		"manager.revealItem": true, "manager.setItemPinned": true, "manager.exportItem": true,
		"manager.pickImportFile": true, "manager.importItemPackage": true,
		"manager.getContentWindowState": true, "manager.setContentWindowTopmost": true,
		"manager.setContentWindowPinned": true, "manager.generateTool": true,
		"manager.createTool": true, "manager.fixTool": true, "manager.embedTool": true,
		"manager.listBackups": true, "manager.createBackup": true, "manager.restoreBackup": true,
		"manager.deleteBackup": true, "manager.importBackupFile": true,
		"manager.openBackupDirectory": true, "manager.getBackupConfig": true,
		"manager.setBackupConfig": true, "manager.gitPush": true, "manager.gitRestore": true,
		"manager.setHotkeyEnabled": true, "manager.setTrayMenuItems": true, "manager.getTheme": true,
		"manager.setTheme": true, "manager.notify": true, "manager.openContent": true,
		"manager.closeContent": true, "manager.trayMenuList": true, "manager.trayMenuReady": true,
		"manager.hideTrayMenu": true, "manager.exitApp": true,
	}
	if hostOnlyMethods[method] {
		return spec(true, "")
	}
	return methodSpec{}, false
}

func (c *bridgeCall) execute(ctx context.Context, method string) (any, error) {
	switch method {
	case "storage.get":
		key, err := readString(c.payload, "key")
		if err != nil {
			return nil, err
		}
		value, found, err := c.state.Storage.Get(c.toolID, key)
		if err != nil {
			return nil, err
		}
		if found {
			return map[string]any{"found": true, "value": json.RawMessage(value)}, nil
		}
		return map[string]any{"found": false, "value": nil}, nil
	case "storage.set":
		key, err := readString(c.payload, "key")
		if err != nil {
			return nil, err
		}
		value, ok := c.payload["value"]
		if !ok {
			return nil, bridgeErr("BRIDGE_BAD_REQUEST", "payload.value 必填")
		}
		encoded, err := json.Marshal(value)
		if err != nil {
			return nil, bridgeErr("BRIDGE_BAD_REQUEST", "value 不是合法 JSON")
		}
		if err := c.state.Storage.Set(c.toolID, key, encoded); err != nil {
			return nil, err
		}
		return map[string]any{"ok": true}, nil
	case "storage.remove":
		key, err := readString(c.payload, "key")
		if err != nil {
			return nil, err
		}
		return map[string]any{"ok": true}, c.state.Storage.Remove(c.toolID, key)
	case "storage.list":
		return map[string]any{"keys": c.state.Storage.List(c.toolID)}, nil
	case "files.save":
		return c.saveFile()
	case "files.list":
		tool := c.state.Tools.Find(c.toolID)
		if tool == nil {
			return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
		}
		return map[string]any{"files": newToolFiles(tool.Directory).List()}, nil
	case "files.read":
		name, err := readString(c.payload, "name")
		if err != nil {
			return nil, err
		}
		tool := c.state.Tools.Find(c.toolID)
		if tool == nil {
			return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
		}
		data, contentType, err := newToolFiles(tool.Directory).Read(name)
		if err != nil {
			return nil, err
		}
		return map[string]any{"name": name, "contentType": contentType, "dataBase64": encodeBase64(data)}, nil
	case "files.delete":
		name, err := readString(c.payload, "name")
		if err != nil {
			return nil, err
		}
		tool := c.state.Tools.Find(c.toolID)
		if tool == nil {
			return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
		}
		deleted, err := newToolFiles(tool.Directory).Delete(name)
		return map[string]any{"deleted": deleted}, err
	case "tool.getManifest":
		tool := c.state.Tools.Find(c.toolID)
		if tool == nil {
			return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
		}
		return tool.Manifest, nil
	case "theme.get":
		return themeView(c.state), nil
	case "window.getState":
		if err := c.requireToolWindow(); err != nil {
			return nil, err
		}
		return windowStateView(c.state, c.toolID), nil
	case "window.setTopmost":
		if err := c.requireToolWindow(); err != nil {
			return nil, err
		}
		enabled, err := readBool(c.payload, "enabled")
		if err != nil {
			return nil, err
		}
		setToolTopmost(c.state, c.toolID, c.window, enabled)
		return windowStateView(c.state, c.toolID), nil
	case "window.setPinned":
		if err := c.requireToolWindow(); err != nil {
			return nil, err
		}
		enabled, err := readBool(c.payload, "enabled")
		if err != nil {
			return nil, err
		}
		setToolPinned(c.state, c.toolID, c.window, enabled)
		return windowStateView(c.state, c.toolID), nil
	case "window.openVersionManager":
		if err := c.requireToolWindow(); err != nil {
			return nil, err
		}
		if err := openContentWindow(c.state, "version", c.toolID); err != nil {
			return nil, err
		}
		return map[string]any{"ok": true}, nil
	case "window.saveChipPosition":
		if err := c.requireToolWindow(); err != nil {
			return nil, err
		}
		left, err := readNumber(c.payload, "left")
		if err != nil {
			return nil, err
		}
		top, err := readNumber(c.payload, "top")
		if err != nil {
			return nil, err
		}
		c.state.Config.UpdateToolState(c.toolID, func(state *ToolWindowState) {
			state.ChipLeft, state.ChipTop = &left, &top
		})
		return windowStateView(c.state, c.toolID), nil
	case "manager.cancelRequest":
		id, err := readString(c.payload, "id")
		if err != nil {
			return nil, err
		}
		return map[string]any{"ok": c.state.CancelRequest(id)}, nil
	case "manager.getServerInfo":
		platform := runtime.GOOS
		webView := "WebView2（Chromium）"
		if runtime.GOOS == "darwin" {
			webView = "WKWebView"
		} else if runtime.GOOS == "linux" {
			webView = "WebKitGTK"
		}
		return map[string]any{
			"version": appVersion, "port": c.state.Port(), "platform": platform,
			"webView": webView, "webViewRuntimeVersion": webViewRuntimeVersion(),
			"nativeVersion": "Go + mygo " + appVersion,
			"dataDirectory": c.state.Paths.Root,
		}, nil
	case "manager.listTools":
		return map[string]any{"tools": listToolsView(c.state)}, nil
	case "manager.launcherList":
		return buildLauncherList(c.state), nil
	case "manager.openTool":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		tool := c.state.Tools.Find(id)
		if tool == nil || !tool.IsValid {
			return nil, bridgeErr("TOOL_NOT_FOUND", fmt.Sprintf("工具不存在或 manifest 无效：%s", id))
		}
		if err := openToolWindow(c.state, tool); err != nil {
			return nil, err
		}
		c.state.Usage.Record(id)
		return map[string]any{"ok": true}, nil
	case "manager.deleteTool":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		deleteStorage := readOptionalBool(c.payload, "deleteStorage")
		return c.deleteTool(id, deleteStorage)
	case "manager.renameTool":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		name, err := readString(c.payload, "name")
		if err != nil {
			return nil, err
		}
		return map[string]any{"ok": true}, c.state.Tools.Rename(id, name)
	case "manager.listVersions":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		tool := c.state.Tools.Find(id)
		if tool == nil {
			return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
		}
		return map[string]any{
			"toolId": id, "activeVersion": tool.Manifest.Version, "versions": tool.Versions,
		}, nil
	case "manager.switchVersion":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		version, err := readString(c.payload, "version")
		if err != nil {
			return nil, err
		}
		tool, err := c.state.Tools.SwitchVersion(id, version)
		if err != nil {
			return nil, err
		}
		if !tool.IsValid {
			return nil, bridgeErr("VERSION_SWITCH_FAILED", "切换后的版本未通过校验")
		}
		pushHostEvent(c.state, "launcher-changed")
		return map[string]any{"ok": true}, nil
	case "manager.deleteVersion":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		version, err := readString(c.payload, "version")
		if err != nil {
			return nil, err
		}
		return map[string]any{"ok": true}, c.state.Tools.DeleteVersion(id, version)
	case "manager.openContent":
		view, err := readString(c.payload, "view")
		if err != nil {
			return nil, err
		}
		if !oneOf(view, "tool", "version", "create", "import", "web") {
			return nil, bridgeErr("BRIDGE_BAD_REQUEST", "view 必须是 tool / version / create / import / web")
		}
		targetID := ""
		if view == "tool" || view == "version" {
			targetID, err = readString(c.payload, "toolId")
			if err != nil {
				return nil, err
			}
			tool := c.state.Tools.Find(targetID)
			if tool == nil || !tool.IsValid {
				return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在或 manifest 无效")
			}
			if view == "tool" {
				c.state.Usage.Record(targetID)
			}
		}
		return map[string]any{"ok": true}, openContentWindow(c.state, view, targetID)
	case "manager.closeContent":
		closeContentWindow(c.state)
		return map[string]any{"ok": true}, nil
	case "manager.getContentWindowState":
		cfg := c.state.Config.Snapshot()
		return map[string]any{"topmost": cfg.Shell.ContentWindowTopmost, "pinned": cfg.Shell.ContentWindowPinned}, nil
	case "manager.setContentWindowTopmost":
		enabled, err := readBool(c.payload, "enabled")
		if err != nil {
			return nil, err
		}
		setContentTopmost(c.state, enabled)
		cfg := c.state.Config.Snapshot()
		return map[string]any{"topmost": cfg.Shell.ContentWindowTopmost, "pinned": cfg.Shell.ContentWindowPinned}, nil
	case "manager.setContentWindowPinned":
		enabled, err := readBool(c.payload, "enabled")
		if err != nil {
			return nil, err
		}
		setContentPinned(c.state, enabled)
		cfg := c.state.Config.Snapshot()
		return map[string]any{"topmost": cfg.Shell.ContentWindowTopmost, "pinned": cfg.Shell.ContentWindowPinned}, nil
	case "manager.openSettings":
		return map[string]any{"ok": true}, openSettingsWindow(c.state)
	case "manager.embedTool":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		tool := c.state.Tools.Find(id)
		if tool == nil || !tool.IsValid {
			return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在或 manifest 无效")
		}
		token := c.state.RegisterEmbedToken(id)
		return map[string]any{
			"toolId": id, "name": tool.Name(), "permissions": tool.Permissions,
			"url": toolURL(c.state.Port(), id, tool.Entry()) + "?bt=" + token,
		}, nil
	case "manager.addWebTool":
		name, _ := readString(c.payload, "name")
		rawURL, _ := readString(c.payload, "url")
		web, err := c.state.WebTools.Add(name, rawURL,
			readOptionalString(c.payload, "category"), readOptionalString(c.payload, "glyph"),
			readOptionalString(c.payload, "description"))
		if err != nil {
			return nil, err
		}
		c.state.Usage.EnsureSeeded([]string{web.Entry.ID})
		return map[string]any{"ok": true, "webId": web.Entry.ID}, nil
	case "manager.deleteWebTool":
		id, err := readString(c.payload, "webId")
		if err != nil {
			return nil, err
		}
		deleted, err := c.state.WebTools.Delete(id)
		if err != nil {
			return nil, err
		}
		if deleted {
			c.state.Pinned.Forget("web", id)
			closeWindowByLabel(c.state, "web-"+id)
		}
		return map[string]any{"ok": true}, nil
	case "manager.renameWebTool":
		id, err := readString(c.payload, "webId")
		if err != nil {
			return nil, err
		}
		name, err := readString(c.payload, "name")
		if err != nil {
			return nil, err
		}
		return map[string]any{"ok": true}, c.state.WebTools.Rename(id, name)
	case "manager.openWebTool":
		id, err := readString(c.payload, "webId")
		if err != nil {
			return nil, err
		}
		web := c.state.WebTools.Find(id)
		if web == nil || !web.IsValid {
			return nil, bridgeErr("WEB_NOT_FOUND", "在线工具不存在或配置无效")
		}
		if err := openWebToolWindow(c.state, web); err != nil {
			return nil, err
		}
		c.state.Usage.Record(id)
		return map[string]any{"ok": true}, nil
	case "manager.addApp":
		name, _ := readString(c.payload, "name")
		target, _ := readString(c.payload, "target")
		app, err := c.state.Apps.Add(name, target,
			readOptionalString(c.payload, "arguments"), readOptionalString(c.payload, "workingDirectory"),
			readOptionalString(c.payload, "category"), readOptionalString(c.payload, "glyph"))
		if err != nil {
			return nil, err
		}
		c.state.Usage.EnsureSeeded([]string{app.Entry.ID})
		return map[string]any{"id": app.Entry.ID}, nil
	case "manager.deleteApp":
		id, err := readString(c.payload, "appId")
		if err != nil {
			return nil, err
		}
		_, err = c.state.Apps.Delete(id)
		c.state.Pinned.Forget("app", id)
		return map[string]any{"ok": true}, err
	case "manager.launchApp":
		id, err := readString(c.payload, "appId")
		if err != nil {
			return nil, err
		}
		app := c.state.Apps.Find(id)
		if app == nil {
			return nil, bridgeErr("APP_NOT_FOUND", "未找到启动项")
		}
		pid, err := launchAppEntry(app)
		if err != nil {
			return nil, err
		}
		c.state.Usage.Record(id)
		var processID any
		if pid > 0 {
			processID = pid
		}
		return map[string]any{
			"appId": id, "appName": app.Name(), "started": true,
			"processId": processID, "message": nil,
		}, nil
	case "manager.listApps":
		return map[string]any{"apps": listAppsView(c.state)}, nil
	case "manager.discoverPrograms":
		entries := scanPrograms()
		programs := make([]any, 0, len(entries))
		for _, entry := range entries {
			programs = append(programs, map[string]any{
				"name": entry.Name, "target": entry.Target, "source": entry.Source,
				"category": entry.Category,
			})
		}
		return map[string]any{"programs": programs}, nil
	case "manager.pickProgram":
		parent := mygo.CallerWindow(ctx)
		files, err := mygo.Dialog.Open(mygo.OpenDialogOptions{
			Parent: parent, Title: "选择程序",
			Filters: []mygo.FileFilter{{
				Name: "程序", Extensions: []string{"exe", "lnk", "bat", "cmd", "app", "desktop"},
			}},
		})
		if err != nil {
			return nil, err
		}
		path := ""
		if len(files) > 0 {
			path = files[0]
		}
		return map[string]any{"path": nullableString(path)}, nil
	case "manager.pickImportFile":
		return pickImportFile(c.ctx)
	case "manager.openBackupDirectory":
		return map[string]any{"ok": true}, openPath(c.state.Paths.DataDir())
	case "manager.setItemPinned":
		kind, _ := readString(c.payload, "kind")
		if kind != "tool" {
			return nil, bridgeErr("BRIDGE_BAD_REQUEST", "当前仅支持小工具置顶")
		}
		id, err := readString(c.payload, "id")
		if err != nil {
			return nil, err
		}
		pinned, err := readBool(c.payload, "pinned")
		if err != nil {
			return nil, err
		}
		c.state.Pinned.SetPinned(kind, id, pinned)
		return map[string]any{"ok": true}, nil
	case "manager.hideProgram":
		id, _ := readString(c.payload, "programId")
		c.state.Hidden.SetHidden(id, true)
		pushHostEvent(c.state, "launcher-changed")
		return map[string]any{"ok": true}, nil
	case "manager.unhideProgram":
		id, _ := readString(c.payload, "programId")
		c.state.Hidden.SetHidden(id, false)
		pushHostEvent(c.state, "launcher-changed")
		return map[string]any{"ok": true}, nil
	case "manager.revealItem":
		kind, _ := readString(c.payload, "kind")
		id, _ := readString(c.payload, "id")
		target := findItemTarget(c.state, kind, id)
		if target == "" {
			return nil, bridgeErr("ITEM_NOT_FOUND", "未找到条目目标")
		}
		return map[string]any{"ok": true}, revealTarget(target)
	case "manager.getTheme", "manager.setTheme":
		if method == "manager.getTheme" {
			return themeView(c.state), nil
		}
		theme, _ := readString(c.payload, "theme")
		theme = normalizeTheme(theme)
		c.state.Config.Update(func(cfg *AppConfig) { cfg.Shell.Theme = theme })
		applyTheme(c.state, theme)
		pushHostEvent(c.state, "theme-changed")
		return themeView(c.state), nil
	case "manager.getBackupConfig":
		return backupConfigView(c.state), nil
	case "manager.setBackupConfig":
		remote := readOptionalString(c.payload, "gitRemoteUrl")
		c.state.Config.Update(func(cfg *AppConfig) {
			if remote == "" {
				cfg.Backup.GitRemoteURL = nil
			} else {
				value := strings.TrimSpace(remote)
				cfg.Backup.GitRemoteURL = &value
			}
		})
		return backupConfigView(c.state), nil
	case "manager.setHotkeyEnabled":
		enabled, err := readBool(c.payload, "enabled")
		if err != nil {
			return nil, err
		}
		c.state.Config.Update(func(cfg *AppConfig) { cfg.Shell.HotkeyEnabled = enabled })
		setHotkeyEnabled(enabled)
		return backupConfigView(c.state), nil
	case "manager.setTrayMenuItems":
		count, err := readInt(c.payload, "count")
		if err != nil || count < 1 || count > 20 {
			return nil, bridgeErr("BRIDGE_BAD_REQUEST", "count 必填，且为 1~20 的整数")
		}
		c.state.Config.Update(func(cfg *AppConfig) { cfg.Shell.TrayMenuItems = count })
		return backupConfigView(c.state), nil
	case "manager.trayMenuList":
		return trayMenuList(c.state), nil
	case "manager.trayMenuReady", "manager.hideTrayMenu":
		return map[string]any{"ok": true}, nil
	case "manager.exitApp":
		c.state.RequestExit()
		mygo.App.Quit()
		return map[string]any{"ok": true}, nil
	case "manager.reindexPrograms":
		go c.state.refreshPrograms()
		return map[string]any{"ok": true}, nil
	case "manager.getAiConfig":
		cfg := c.state.Config.Snapshot()
		return map[string]any{
			"configured": c.state.Secrets.IsConfigured() && strings.TrimSpace(cfg.AI.BaseURL) != "",
			"baseUrl":    cfg.AI.BaseURL, "model": cfg.AI.Model,
			"temperature": cfg.AI.Temperature, "timeoutSeconds": cfg.AI.TimeoutSeconds,
			"secretStoreMode": c.state.Secrets.Mode(),
		}, nil
	case "manager.setAiConfig":
		return c.setAiConfig()
	case "ai.chat":
		return c.aiChat(ctx)
	case "manager.generateTool":
		return c.generateTool(ctx)
	case "manager.generateUpdate":
		return c.generateUpdate(ctx)
	case "manager.fixTool":
		return c.fixTool(ctx)
	case "manager.createTool":
		return c.createTool()
	case "manager.applyUpdate":
		return c.applyUpdate()
	case "manager.importTool":
		return c.importTool()
	case "manager.createToolShortcut":
		id, err := readString(c.payload, "toolId")
		if err != nil {
			return nil, err
		}
		return createToolShortcut(c.state, id)
	case "manager.exportItem":
		kind, _ := readString(c.payload, "kind")
		id, _ := readString(c.payload, "id")
		return exportItem(c.state, mygo.CallerWindow(ctx), kind, id)
	case "manager.importItemPackage":
		path, _ := readString(c.payload, "path")
		accept := readOptionalBool(c.payload, "acceptHighSeverity")
		return importItemPackage(c.state, path, accept)
	case "manager.listBackups":
		return map[string]any{"backups": listBackups(c.state)}, nil
	case "manager.createBackup":
		note := readOptionalString(c.payload, "note")
		name, size, createdAt, err := createBackupWithNote(c.state, note)
		if err != nil {
			return nil, err
		}
		return map[string]any{"name": name, "sizeBytes": size, "createdAtUtc": createdAt}, nil
	case "manager.restoreBackup":
		name, err := readString(c.payload, "name")
		if err != nil {
			return nil, err
		}
		return restoreBackup(c.state, name)
	case "manager.deleteBackup":
		name, err := readString(c.payload, "name")
		if err != nil {
			return nil, err
		}
		return deleteBackup(c.state, name)
	case "manager.importBackupFile":
		return importBackupFile(c.state, mygo.CallerWindow(ctx))
	case "manager.gitPush":
		return c.gitPush(ctx)
	case "manager.gitRestore":
		return c.gitRestore(ctx)
	case "manager.notify":
		title, err := readString(c.payload, "title")
		if err != nil {
			return nil, err
		}
		body := readOptionalString(c.payload, "body")
		return map[string]any{"ok": true}, notifyUser(title, body)
	}
	return nil, bridgeErr("UNKNOWN_METHOD", fmt.Sprintf("未知方法 '%s'", method))
}

func (c *bridgeCall) requireToolWindow() error {
	if !strings.HasPrefix(c.role, "tool:") {
		return bridgeErr("PERMISSION_DENIED", "该方法仅限独立工具窗口调用")
	}
	if c.state.RoleOf(c.window) != "tool:"+c.toolID {
		return bridgeErr("PERMISSION_DENIED", "窗口身份不匹配")
	}
	return nil
}

func (c *bridgeCall) deleteTool(id string, deleteStorage bool) (any, error) {
	tool := c.state.Tools.Find(id)
	if tool == nil {
		return nil, bridgeErr("TOOL_NOT_FOUND", fmt.Sprintf("工具不存在：%s", id))
	}
	closeWindowByLabel(c.state, "tool-"+id)
	var lastErr error
	for i := 0; i < 20; i++ {
		if err := os.RemoveAll(tool.Directory); err == nil {
			lastErr = nil
			break
		} else {
			lastErr = err
			sleepMillis(50)
		}
	}
	if lastErr != nil {
		return nil, bridgeErr("TOOL_DELETE_FAILED", fmt.Sprintf("删除失败：%v", lastErr))
	}
	if deleteStorage {
		c.state.Storage.Delete(id)
	}
	c.state.Tools.Remove(id)
	c.state.Pinned.Forget("tool", id)
	c.state.RevokeEmbedTokens(id)
	pushHostEvent(c.state, "launcher-changed")
	return map[string]any{"ok": true}, nil
}

func (c *bridgeCall) saveFile() (any, error) {
	tool := c.state.Tools.Find(c.toolID)
	if tool == nil {
		return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
	}
	name, err := readString(c.payload, "name")
	if err != nil {
		return nil, err
	}
	encoded, err := readString(c.payload, "dataBase64")
	if err != nil {
		return nil, err
	}
	data, err := decodeBase64(encoded)
	if err != nil {
		return nil, err
	}
	size, err := newToolFiles(tool.Directory).Save(name, data, readOptionalString(c.payload, "contentType"))
	if err != nil {
		return nil, err
	}
	return map[string]any{
		"name": name, "sizeBytes": size,
		"url": fmt.Sprintf("/tools/%s/files/%s", c.toolID, url.PathEscape(name)),
	}, nil
}

func (c *bridgeCall) setAiConfig() (any, error) {
	baseURL := readOptionalString(c.payload, "baseUrl")
	if baseURL != "" && !strings.HasPrefix(baseURL, "http://") && !strings.HasPrefix(baseURL, "https://") {
		return nil, bridgeErr("BRIDGE_BAD_REQUEST", "baseUrl 必须是绝对 HTTP(S) 地址")
	}
	model := readOptionalString(c.payload, "model")
	temperature, hasTemperature := c.payload["temperature"].(float64)
	timeout, hasTimeout := c.payload["timeoutSeconds"].(float64)
	if hasTemperature && (temperature < 0 || temperature > 2) {
		return nil, bridgeErr("BRIDGE_BAD_REQUEST", "temperature 必须是 0~2 之间的有限数值")
	}
	if hasTimeout && (timeout < 1 || timeout > 600) {
		return nil, bridgeErr("BRIDGE_BAD_REQUEST", "timeoutSeconds 必须是 1~600 之间的整数")
	}
	apiKey := readOptionalString(c.payload, "apiKey")
	clearKey, _ := c.payload["clearKey"].(bool)
	if clearKey {
		if err := c.state.Secrets.Clear(); err != nil {
			return nil, bridgeErr("SECRET_STORE_FAILED", err.Error())
		}
	} else if apiKey != "" {
		if err := c.state.Secrets.Set(apiKey); err != nil {
			return nil, bridgeErr("SECRET_STORE_FAILED", err.Error())
		}
	}
	c.state.Config.Update(func(cfg *AppConfig) {
		if baseURL != "" {
			cfg.AI.BaseURL = strings.TrimSpace(baseURL)
		}
		if model != "" {
			cfg.AI.Model = strings.TrimSpace(model)
		}
		if hasTemperature {
			cfg.AI.Temperature = temperature
		}
		if hasTimeout {
			cfg.AI.TimeoutSeconds = int(timeout)
		}
	})
	return map[string]any{"ok": true}, nil
}

func successEnvelope(id string, result any) string {
	data, _ := json.Marshal(map[string]any{"id": id, "result": result})
	return string(data)
}

func errorEnvelope(id, code, message string) string {
	data, _ := json.Marshal(map[string]any{
		"id":    id,
		"error": map[string]any{"code": code, "message": message},
	})
	return string(data)
}

func readString(payload map[string]any, key string) (string, error) {
	value, ok := payload[key].(string)
	if !ok || strings.TrimSpace(value) == "" {
		return "", bridgeErr("BRIDGE_BAD_REQUEST", fmt.Sprintf("payload.%s 必填", key))
	}
	return value, nil
}

func readOptionalString(payload map[string]any, key string) string {
	value, _ := payload[key].(string)
	return strings.TrimSpace(value)
}

func readBool(payload map[string]any, key string) (bool, error) {
	value, ok := payload[key].(bool)
	if !ok {
		return false, bridgeErr("BRIDGE_BAD_REQUEST", fmt.Sprintf("payload.%s 必填且为布尔值", key))
	}
	return value, nil
}

func readOptionalBool(payload map[string]any, key string) bool {
	value, _ := payload[key].(bool)
	return value
}

func readNumber(payload map[string]any, key string) (float64, error) {
	value, ok := payload[key].(float64)
	if !ok {
		return 0, bridgeErr("BRIDGE_BAD_REQUEST", fmt.Sprintf("payload.%s 必填且为数值", key))
	}
	return value, nil
}

func readInt(payload map[string]any, key string) (int, error) {
	value, err := readNumber(payload, key)
	return int(value), err
}

func oneOf(value string, values ...string) bool {
	for _, item := range values {
		if value == item {
			return true
		}
	}
	return false
}

func nullableString(value string) any {
	if strings.TrimSpace(value) == "" {
		return nil
	}
	return value
}

func listToolsView(state *AppState) []any {
	tools := state.Tools.All()
	result := make([]any, 0, len(tools))
	for _, tool := range tools {
		result = append(result, map[string]any{
			"id": tool.Manifest.ID, "name": tool.Name(), "version": tool.Manifest.Version,
			"description": tool.Manifest.Description, "permissions": tool.Permissions,
			"lastModifiedUtc": tool.LastModifiedUTC, "isValid": tool.IsValid, "errors": tool.Errors,
			"hasStorageData": state.Storage.SizeBytes(tool.Manifest.ID) > 0,
			"searchKey":      buildSearchKey(tool.Name(), tool.Manifest.ID, deref(tool.Manifest.Description)),
		})
	}
	return result
}

func listAppsView(state *AppState) []any {
	apps := state.Apps.All()
	result := make([]any, 0, len(apps))
	for _, app := range apps {
		result = append(result, map[string]any{
			"id": app.Entry.ID, "name": app.Name(), "target": app.Entry.Target,
			"category": app.Entry.Category, "glyph": app.Entry.Glyph, "isValid": app.IsValid,
			"targetExists": app.TargetExists(), "arguments": app.Entry.Arguments,
			"iconUrl":   nil,
			"searchKey": buildSearchKey(app.Name(), app.Entry.ID, app.Entry.Target),
		})
	}
	return result
}

func themeView(state *AppState) map[string]any {
	theme := normalizeTheme(state.Config.Snapshot().Shell.Theme)
	return map[string]any{"theme": theme, "dark": themeIsDark(theme)}
}

func backupConfigView(state *AppState) map[string]any {
	cfg := state.Config.Snapshot()
	return map[string]any{
		"gitRemoteUrl": cfg.Backup.GitRemoteURL, "lastPushUtc": cfg.Backup.LastPushUTC,
		"lastPushStatus": cfg.Backup.LastPushStatus, "hotkeyEnabled": cfg.Shell.HotkeyEnabled,
		"hotkeySupported": runtime.GOOS == "windows", "hotkeyRegistered": hotkeyRegistered.Load(),
		"trayMenuItems": cfg.Shell.TrayMenuItems,
	}
}

func trayMenuList(state *AppState) map[string]any {
	list := buildLauncherList(state)
	count := state.Config.Snapshot().Shell.TrayMenuItems
	if count < 1 {
		count = 10
	}
	if count > 20 {
		count = 20
	}
	items := []any{}
	for _, item := range list.Items {
		if len(items) >= count {
			break
		}
		kindLabel := "本机程序"
		switch item.Kind {
		case "tool":
			kindLabel = "小工具"
		case "web":
			kindLabel = "在线工具"
		}
		items = append(items, map[string]any{
			"kind": item.Kind, "id": item.ID, "name": item.Name, "kindLabel": kindLabel,
			"glyph": item.Glyph, "iconUrl": item.IconURL,
		})
	}
	return map[string]any{"items": items}
}

func findItemTarget(state *AppState, kind, id string) string {
	switch kind {
	case "tool":
		if tool := state.Tools.Find(id); tool != nil {
			return tool.Directory
		}
	case "app":
		if app := state.Apps.Find(id); app != nil {
			return app.Entry.Target
		}
	case "web":
		if web := state.WebTools.Find(id); web != nil {
			return web.Entry.URL
		}
	case "program":
		for _, entry := range state.ProgramIndex.Snapshot().Entries {
			if entry.ID == id {
				return entry.Target
			}
		}
	}
	return ""
}

func (s *AppState) refreshPrograms() {
	if !s.ProgramIndex.startIndexing() {
		return
	}
	entries := scanPrograms()
	for _, entry := range entries {
		ensureIconAt(entry.Target, filepath.Join(s.Paths.IconCacheDir(), entry.ID+".png"))
	}
	s.ProgramIndex.finishIndexing(entries)
	pushHostEvent(s, "launcher-changed")
	logInfo("程序索引完成，列表已刷新")
}

func (s *AppState) refreshAll() {
	s.Config.Reload()
	s.Tools.Rescan()
	s.Apps.Rescan()
	s.WebTools.Rescan()
	s.seedUsage()
	pushHostEvent(s, "launcher-changed")
	pushHostEvent(s, "theme-changed")
}
