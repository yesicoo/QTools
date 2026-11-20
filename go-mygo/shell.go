package main

import (
	"encoding/json"
	"fmt"
	"io/fs"
	"net/url"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/egoist/mygo"
)

const (
	mainLabel     = "main"
	contentLabel  = "content"
	settingsLabel = "settings"
)

var (
	hotkeyRegistered atomic.Bool
	hotkeyMu         sync.Mutex
	trayMu           sync.Mutex
	geometryTimers   sync.Map
)

func trustedOrigins(state *AppState) []string {
	return []string{state.Origin(), fmt.Sprintf("http://localhost:%d", state.Port())}
}

func pageOptions(state *AppState, trusted bool, dev bool) mygo.PageOptions {
	options := mygo.PageOptions{DevTools: mygo.DevToolsDisabled}
	if trusted {
		options.TrustedOrigins = trustedOrigins(state)
	}
	if dev {
		options.DevTools = mygo.DevToolsEnabled
	}
	return options
}

func createMainWindow(state *AppState, dev bool) *mygo.Window {
	cfg := state.Config.Snapshot()
	width := clampInt(valueOr(cfg.Shell.MainWindowWidth, 340), 280, 8000)
	height := clampInt(valueOr(cfg.Shell.MainWindowHeight, 720), 480, 6000)
	options := mygo.WindowOptions{
		Title: "QTools", Width: width, Height: height, MinWidth: 280, MinHeight: 480,
		URL:             hostURL(state.Port(), "index.html"),
		Page:            pageOptions(state, true, dev),
		BackgroundColor: "light-dark(#f6f7f9, #0f1115)",
	}
	if validSavedPoint(state, cfg.Shell.MainWindowLeft, cfg.Shell.MainWindowTop) {
		options.X, options.Y = *cfg.Shell.MainWindowLeft, *cfg.Shell.MainWindowTop
	}
	window := mygo.NewWindow(options)
	state.RegisterWindow(window, mainLabel, "host")
	applyWindowIcon(window)
	window.OnClose(func(event *mygo.CloseEvent) {
		saveMainGeometry(state, window)
		if runtime.GOOS == "windows" && !state.ExitRequested() && state.Tray() != nil {
			event.PreventDefault()
			window.Hide()
			logInfo("主窗口已收起到托盘")
		}
	})
	window.OnClosed(func() { state.UnregisterWindow(mainLabel) })
	window.OnMove(func() { scheduleGeometrySave(state, mainLabel, window) })
	window.OnResize(func() { scheduleGeometrySave(state, mainLabel, window) })
	return window
}

func openToolWindow(state *AppState, tool *LoadedTool) error {
	label := "tool-" + tool.Manifest.ID
	if existing := state.Window(label); existing != nil {
		existing.Show()
		existing.Focus()
		return nil
	}
	cfg := state.Config.Snapshot()
	saved := cfg.Shell.ToolWindowStates[tool.Manifest.ID]
	width := tool.Manifest.Window.Width
	height := tool.Manifest.Window.Height
	if saved.Width != nil && *saved.Width >= 280 && *saved.Width <= 8000 {
		width = *saved.Width
	}
	if saved.Height != nil && *saved.Height >= 220 && *saved.Height <= 8000 {
		height = *saved.Height
	}
	options := mygo.WindowOptions{
		Title: tool.Name(), Width: width, Height: height, MinWidth: 280, MinHeight: 220,
		URL:  toolURL(state.Port(), tool.Manifest.ID, tool.Entry()),
		Page: pageOptions(state, true, false), AlwaysOnTop: saved.Topmost,
	}
	if validSavedPoint(state, saved.Left, saved.Top) {
		options.X, options.Y = *saved.Left, *saved.Top
	}
	window := mygo.NewWindow(options)
	state.RegisterWindow(window, label, "tool:"+tool.Manifest.ID)
	applyWindowIcon(window)
	applyPin(window, saved.Pinned)
	window.OnClose(func(*mygo.CloseEvent) { saveToolGeometry(state, tool.Manifest.ID, window) })
	window.OnClosed(func() { state.UnregisterWindow(label) })
	window.OnMove(func() { scheduleToolGeometrySave(state, tool.Manifest.ID, window) })
	window.OnResize(func() { scheduleToolGeometrySave(state, tool.Manifest.ID, window) })
	return nil
}

func openContentWindow(state *AppState, view, toolID string) error {
	query := url.Values{}
	query.Set("mode", "content")
	query.Set("view", view)
	if toolID != "" {
		query.Set("toolId", toolID)
	}
	pageURL := hostURL(state.Port(), "index.html") + "?" + query.Encode()
	if existing := state.Window(contentLabel); existing != nil {
		_ = existing.Page().LoadURL(pageURL)
		existing.Show()
		existing.Focus()
		return nil
	}
	cfg := state.Config.Snapshot()
	width := clampInt(valueOr(cfg.Shell.ContentWindowWidth, 920), 560, 8000)
	height := clampInt(valueOr(cfg.Shell.ContentWindowHeight, 680), 420, 6000)
	options := mygo.WindowOptions{
		Title: "QTools", Width: width, Height: height, MinWidth: 560, MinHeight: 420,
		URL: pageURL, Page: pageOptions(state, true, false),
		AlwaysOnTop: cfg.Shell.ContentWindowTopmost,
	}
	if validSavedPoint(state, cfg.Shell.ContentWindowLeft, cfg.Shell.ContentWindowTop) {
		options.X, options.Y = *cfg.Shell.ContentWindowLeft, *cfg.Shell.ContentWindowTop
	}
	window := mygo.NewWindow(options)
	state.RegisterWindow(window, contentLabel, "host")
	applyWindowIcon(window)
	applyPin(window, cfg.Shell.ContentWindowPinned)
	window.OnClose(func(*mygo.CloseEvent) { saveContentGeometry(state, window) })
	window.OnClosed(func() { state.UnregisterWindow(contentLabel) })
	window.OnMove(func() { scheduleGeometrySave(state, contentLabel, window) })
	window.OnResize(func() { scheduleGeometrySave(state, contentLabel, window) })
	return nil
}

func closeContentWindow(state *AppState) {
	closeWindowByLabel(state, contentLabel)
}

func openSettingsWindow(state *AppState) error {
	if existing := state.Window(settingsLabel); existing != nil {
		existing.Show()
		existing.Focus()
		return nil
	}
	cfg := state.Config.Snapshot()
	width := clampInt(valueOr(cfg.Shell.SettingsWindowWidth, 680), 560, 8000)
	height := clampInt(valueOr(cfg.Shell.SettingsWindowHeight, 760), 520, 6000)
	options := mygo.WindowOptions{
		Title: "QTools 设置", Width: width, Height: height, MinWidth: 560, MinHeight: 520,
		URL: hostURL(state.Port(), "settings.html"), Page: pageOptions(state, true, false),
	}
	if validSavedPoint(state, cfg.Shell.SettingsWindowLeft, cfg.Shell.SettingsWindowTop) {
		options.X, options.Y = *cfg.Shell.SettingsWindowLeft, *cfg.Shell.SettingsWindowTop
	}
	window := mygo.NewWindow(options)
	state.RegisterWindow(window, settingsLabel, "host")
	applyWindowIcon(window)
	window.OnClose(func(*mygo.CloseEvent) { saveSettingsGeometry(state, window) })
	window.OnClosed(func() { state.UnregisterWindow(settingsLabel) })
	window.OnMove(func() { scheduleGeometrySave(state, settingsLabel, window) })
	window.OnResize(func() { scheduleGeometrySave(state, settingsLabel, window) })
	return nil
}

func openWebToolWindow(state *AppState, web *LoadedWebTool) error {
	label := "web-" + web.Entry.ID
	if existing := state.Window(label); existing != nil {
		existing.Show()
		existing.Focus()
		return nil
	}
	window := mygo.NewWindow(mygo.WindowOptions{
		Title: web.Name(), Width: 1100, Height: 780, MinWidth: 420, MinHeight: 320,
		URL: web.Entry.URL, Page: pageOptions(state, false, false),
		BackgroundColor: "light-dark(#f6f7f9, #0f1115)",
	})
	state.RegisterWindow(window, label, "web:"+web.Entry.ID)
	applyWindowIcon(window)
	window.OnClosed(func() { state.UnregisterWindow(label) })
	return nil
}

func closeWindowByLabel(state *AppState, label string) {
	if window := state.Window(label); window != nil {
		window.Close()
	}
}

func windowStateView(state *AppState, toolID string) map[string]any {
	saved := state.Config.ToolState(toolID)
	return map[string]any{
		"topmost": saved.Topmost, "pinned": saved.Pinned,
		"chipLeft": saved.ChipLeft, "chipTop": saved.ChipTop,
	}
}

func setToolTopmost(state *AppState, toolID string, window *mygo.Window, enabled bool) {
	window.SetAlwaysOnTop(enabled)
	state.Config.UpdateToolState(toolID, func(saved *ToolWindowState) { saved.Topmost = enabled })
}

func setToolPinned(state *AppState, toolID string, window *mygo.Window, enabled bool) {
	applyPin(window, enabled)
	state.Config.UpdateToolState(toolID, func(saved *ToolWindowState) { saved.Pinned = enabled })
}

func setContentTopmost(state *AppState, enabled bool) {
	if window := state.Window(contentLabel); window != nil {
		window.SetAlwaysOnTop(enabled)
	}
	state.Config.Update(func(cfg *AppConfig) { cfg.Shell.ContentWindowTopmost = enabled })
}

func setContentPinned(state *AppState, enabled bool) {
	if window := state.Window(contentLabel); window != nil {
		applyPin(window, enabled)
	}
	state.Config.Update(func(cfg *AppConfig) { cfg.Shell.ContentWindowPinned = enabled })
}

func applyPin(window *mygo.Window, pinned bool) {
	window.SetResizable(!pinned)
	window.SetMovable(!pinned)
}

func applyWindowIcon(window *mygo.Window) {
	if png := appIconPNG(); len(png) > 0 {
		_ = window.SetIcon(png)
	}
}

func appIconPNG() []byte {
	data, _ := fs.ReadFile(assetFS(), "brand/icon.png")
	return data
}

func saveMainGeometry(state *AppState, window *mygo.Window) {
	bounds := window.Bounds()
	state.Config.Update(func(cfg *AppConfig) {
		cfg.Shell.MainWindowLeft, cfg.Shell.MainWindowTop = intPtr(bounds.X), intPtr(bounds.Y)
		cfg.Shell.MainWindowWidth, cfg.Shell.MainWindowHeight = intPtr(bounds.Width), intPtr(bounds.Height)
	})
}

func saveContentGeometry(state *AppState, window *mygo.Window) {
	bounds := window.Bounds()
	state.Config.Update(func(cfg *AppConfig) {
		cfg.Shell.ContentWindowLeft, cfg.Shell.ContentWindowTop = intPtr(bounds.X), intPtr(bounds.Y)
		cfg.Shell.ContentWindowWidth, cfg.Shell.ContentWindowHeight = intPtr(bounds.Width), intPtr(bounds.Height)
	})
}

func saveSettingsGeometry(state *AppState, window *mygo.Window) {
	bounds := window.Bounds()
	state.Config.Update(func(cfg *AppConfig) {
		cfg.Shell.SettingsWindowLeft, cfg.Shell.SettingsWindowTop = intPtr(bounds.X), intPtr(bounds.Y)
		cfg.Shell.SettingsWindowWidth, cfg.Shell.SettingsWindowHeight = intPtr(bounds.Width), intPtr(bounds.Height)
	})
}

func saveToolGeometry(state *AppState, toolID string, window *mygo.Window) {
	bounds := window.Bounds()
	state.Config.UpdateToolState(toolID, func(saved *ToolWindowState) {
		saved.Left, saved.Top = intPtr(bounds.X), intPtr(bounds.Y)
		saved.Width, saved.Height = intPtr(bounds.Width), intPtr(bounds.Height)
	})
}

func scheduleGeometrySave(state *AppState, label string, window *mygo.Window) {
	key := "window:" + label
	if value, ok := geometryTimers.Load(key); ok {
		if timer, ok := value.(*time.Timer); ok {
			timer.Stop()
		}
	}
	timer := time.AfterFunc(500*time.Millisecond, func() {
		switch label {
		case mainLabel:
			saveMainGeometry(state, window)
		case contentLabel:
			saveContentGeometry(state, window)
		case settingsLabel:
			saveSettingsGeometry(state, window)
		}
	})
	geometryTimers.Store(key, timer)
}

func scheduleToolGeometrySave(state *AppState, toolID string, window *mygo.Window) {
	key := "tool:" + toolID
	if value, ok := geometryTimers.Load(key); ok {
		if timer, ok := value.(*time.Timer); ok {
			timer.Stop()
		}
	}
	timer := time.AfterFunc(500*time.Millisecond, func() { saveToolGeometry(state, toolID, window) })
	geometryTimers.Store(key, timer)
}

func validSavedPoint(state *AppState, left, top *int) bool {
	if left == nil || top == nil {
		return false
	}
	for _, display := range mygo.Screen.Displays() {
		area := display.WorkArea
		if *left >= area.X && *top >= area.Y &&
			*left < area.X+area.Width && *top < area.Y+area.Height {
			return true
		}
	}
	return false
}

func intPtr(value int) *int { return &value }

func valueOr(value *int, fallback int) int {
	if value == nil {
		return fallback
	}
	return *value
}

func clampInt(value, low, high int) int {
	if value < low {
		return low
	}
	if value > high {
		return high
	}
	return value
}

func pushHostEvent(state *AppState, event string) {
	envelope, _ := json.Marshal(map[string]any{"id": "event-" + event, "event": event})
	literal, _ := json.Marshal(string(envelope))
	script := fmt.Sprintf("if (window.__qtoolsReceive) { window.__qtoolsReceive(%s); }", literal)
	for _, window := range state.AllWindows() {
		_, _ = window.Page().Eval(script)
	}
}

func applyTheme(state *AppState, theme string) {
	switch normalizeTheme(theme) {
	case "light":
		mygo.Theme.SetSource(mygo.ThemeLight)
	case "dark":
		mygo.Theme.SetSource(mygo.ThemeDark)
	default:
		mygo.Theme.SetSource(mygo.ThemeSystem)
	}
}

func initTray(state *AppState) error {
	if runtime.GOOS != "windows" {
		return fmt.Errorf("当前平台暂不提供托盘")
	}
	icon := appIconPNG()
	tray, err := mygo.NewTray(mygo.TrayOptions{
		ID: "qtools", Icon: icon, ToolTip: "QTools",
	})
	if err != nil {
		return err
	}
	tray.OnClick(toggleMainWindow)
	tray.OnRightClick(func() {
		menu := buildTrayMenu(state)
		tray.SetMenu(menu)
		tray.PopUpMenu()
		tray.SetMenu(nil)
	})
	state.SetTray(tray)
	return nil
}

func buildTrayMenu(state *AppState) *mygo.Menu {
	items := []*mygo.MenuItem{}
	list := trayMenuList(state)
	rawItems, _ := list["items"].([]any)
	for _, raw := range rawItems {
		entry, _ := raw.(map[string]any)
		kind, _ := entry["kind"].(string)
		id, _ := entry["id"].(string)
		name, _ := entry["name"].(string)
		label := fmt.Sprintf("%s  ·  %s", name, trayKindLabel(kind))
		items = append(items, &mygo.MenuItem{Label: label, Click: func(*mygo.MenuItem, *mygo.Window) {
			go activateTrayItem(state, kind, id)
		}})
	}
	if len(items) > 0 {
		items = append(items, mygo.Separator())
	}
	items = append(items,
		&mygo.MenuItem{Label: "打开 QTools", Click: func(*mygo.MenuItem, *mygo.Window) { showMainWindow(state) }},
		&mygo.MenuItem{Label: "设置", Click: func(*mygo.MenuItem, *mygo.Window) { _ = openSettingsWindow(state) }},
		mygo.Separator(),
		&mygo.MenuItem{Label: "退出 QTools", Click: func(*mygo.MenuItem, *mygo.Window) {
			state.RequestExit()
			mygo.App.Quit()
		}},
	)
	return mygo.NewMenu(items)
}

func trayKindLabel(kind string) string {
	switch kind {
	case "tool":
		return "小工具"
	case "web":
		return "在线工具"
	default:
		return "本机程序"
	}
}

func activateTrayItem(state *AppState, kind, id string) {
	switch kind {
	case "tool":
		if tool := state.Tools.Find(id); tool != nil && tool.IsValid {
			if err := openToolWindow(state, tool); err == nil {
				state.Usage.Record(id)
			}
		}
	case "web":
		if web := state.WebTools.Find(id); web != nil && web.IsValid {
			if err := openWebToolWindow(state, web); err == nil {
				state.Usage.Record(id)
			}
		}
	case "app":
		if app := state.Apps.Find(id); app != nil {
			if _, err := launchAppEntry(app); err == nil {
				state.Usage.Record(id)
			}
		}
	case "program":
		for _, entry := range state.ProgramIndex.Snapshot().Entries {
			if entry.ID == id {
				if err := openPath(entry.Target); err == nil {
					state.Usage.Record(id)
				}
				break
			}
		}
	}
}

func showMainWindow(state *AppState) {
	if window := state.Window(mainLabel); window != nil {
		window.Show()
		window.Focus()
	}
}

func toggleMainWindow() {
	state := globalState
	if state == nil {
		return
	}
	window := state.Window(mainLabel)
	if window == nil {
		return
	}
	if window.IsVisible() {
		window.Hide()
	} else {
		window.Show()
		window.Focus()
	}
}

func setHotkeyEnabled(enabled bool) {
	if runtime.GOOS != "windows" {
		return
	}
	hotkeyMu.Lock()
	defer hotkeyMu.Unlock()
	if enabled {
		if hotkeyRegistered.Load() {
			return
		}
		if err := mygo.GlobalShortcut.Register("Alt+Space", toggleMainWindow); err == nil {
			hotkeyRegistered.Store(true)
			logInfo("全局热键已注册：Alt+Space")
		} else {
			logWarn("全局热键注册失败：%v", err)
		}
	} else if hotkeyRegistered.Load() {
		mygo.GlobalShortcut.Unregister("Alt+Space")
		hotkeyRegistered.Store(false)
	}
}

func notifyUser(title, body string) error {
	title = truncateRunes(strings.TrimSpace(title), 64)
	body = truncateRunes(strings.TrimSpace(body), 256)
	if title == "" {
		return bridgeErr("NOTIFY_UNAVAILABLE", "通知标题不能为空")
	}
	notification := mygo.NewNotification(mygo.NotificationOptions{Title: title, Body: body})
	if err := notification.Show(); err != nil {
		return bridgeErr("NOTIFY_UNAVAILABLE", fmt.Sprintf("系统通知不可用：%v", err))
	}
	return nil
}

func truncateRunes(value string, limit int) string {
	runes := []rune(value)
	if len(runes) <= limit {
		return value
	}
	return string(runes[:limit])
}

func sleepMillis(value int) { time.Sleep(time.Duration(value) * time.Millisecond) }

func webViewRuntimeVersion() string {
	return windowsWebViewRuntimeVersion()
}

var globalState *AppState
