use std::sync::Arc;

#[cfg(not(windows))]
use tauri::LogicalPosition;
use tauri::Url;
use tauri::{
    AppHandle, LogicalSize, Manager, PhysicalPosition, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder, WindowEvent,
};

use crate::config::ToolWindowState;
use crate::error::{BridgeError, BridgeResult};
use crate::launcher::{LoadedApp, LoadedWebTool};
use crate::server::{host_url, tool_url};
use crate::state::AppState;
use crate::tools::LoadedTool;

pub const MAIN_LABEL: &str = "main";
pub const CONTENT_LABEL: &str = "content";
pub const SETTINGS_LABEL: &str = "settings";
pub const TRAY_MENU_LABEL: &str = "tray-menu";

pub fn open_tool_window(
    app: &AppHandle,
    state: &Arc<AppState>,
    tool: &LoadedTool,
) -> BridgeResult<()> {
    let label = format!("tool-{}", tool.manifest.id);
    if let Some(existing) = app.get_webview_window(&label) {
        let _ = existing.show();
        let _ = existing.unminimize();
        let _ = existing.set_focus();
        return Ok(());
    }

    let port = state.port();
    let url = tool_url(port, &tool.manifest.id, tool.entry());
    let parsed = Url::parse(&url)
        .map_err(|error| BridgeError::internal(format!("工具 URL 无效：{error}")))?;
    let (width, height) = tool.manifest.window.sane(720, 720);
    let (saved_width, saved_height, left, top, topmost, pinned) = {
        let config = state.config_snapshot();
        let saved = config.shell.tool_window_states.get(&tool.manifest.id);
        (
            saved.and_then(|value| value.width).unwrap_or(width),
            saved.and_then(|value| value.height).unwrap_or(height),
            saved.and_then(|value| value.left),
            saved.and_then(|value| value.top),
            saved.map(|value| value.topmost).unwrap_or(false),
            saved.map(|value| value.pinned).unwrap_or(false),
        )
    };

    let mut builder = WebviewWindowBuilder::new(app, &label, WebviewUrl::External(parsed))
        .title(tool.name())
        .inner_size(saved_width as f64, saved_height as f64)
        .min_inner_size(280.0, 220.0)
        .resizable(tool.manifest.window.resizable)
        .visible(false);
    if let (Some(left), Some(top)) = (left, top) {
        builder = builder.position(left as f64, top as f64);
    }
    let window = builder
        .build()
        .map_err(|error| BridgeError::internal(format!("创建工具窗口失败：{error}")))?;
    if let (Some(left), Some(top)) = (left, top) {
        restore_saved_position(&window, left, top);
    }

    restore_tool_window_flags(&window, topmost, pinned);
    let _ = window.show();
    let _ = window.set_focus();
    crate::window_theme::apply(&window, state, true);

    spawn_geometry_persistence(
        app.clone(),
        state.clone(),
        window.clone(),
        label,
        Some(tool.manifest.id.clone()),
    );
    Ok(())
}

pub fn open_content_window(
    app: &AppHandle,
    state: &Arc<AppState>,
    view: &str,
    tool_id: Option<&str>,
) -> BridgeResult<()> {
    let mut query = format!("mode=content&view={}", urlencoding::encode(view));
    if let Some(tool_id) = tool_id {
        query.push_str("&toolId=");
        query.push_str(&urlencoding::encode(tool_id));
    }
    let url = format!("{}?{}", host_url(state.port(), "index.html"), query);
    if let Some(existing) = app.get_webview_window(CONTENT_LABEL) {
        let parsed = Url::parse(&url)
            .map_err(|error| BridgeError::internal(format!("内容窗口 URL 无效：{error}")))?;
        let _ = existing.navigate(parsed);
        let _ = existing.show();
        let _ = existing.unminimize();
        let _ = existing.set_focus();
        return Ok(());
    }
    let parsed = Url::parse(&url)
        .map_err(|error| BridgeError::internal(format!("内容窗口 URL 无效：{error}")))?;
    let config = state.config_snapshot();
    let width = config
        .shell
        .content_window_width
        .unwrap_or(920)
        .clamp(560, 8000);
    let height = config
        .shell
        .content_window_height
        .unwrap_or(680)
        .clamp(420, 8000);
    let mut builder = WebviewWindowBuilder::new(app, CONTENT_LABEL, WebviewUrl::External(parsed))
        .title("QTools — 管理")
        .inner_size(width as f64, height as f64)
        .min_inner_size(560.0, 420.0)
        .visible(false);
    if let (Some(left), Some(top)) = (
        config.shell.content_window_left,
        config.shell.content_window_top,
    ) {
        builder = builder.position(left as f64, top as f64);
    }
    let window = builder
        .build()
        .map_err(|error| BridgeError::internal(format!("创建内容窗口失败：{error}")))?;
    if let (Some(left), Some(top)) = (
        config.shell.content_window_left,
        config.shell.content_window_top,
    ) {
        restore_saved_position(&window, left, top);
    }
    if config.shell.content_window_topmost {
        let _ = window.set_always_on_top(true);
    }
    if config.shell.content_window_pinned {
        let _ = window.set_resizable(false);
    }
    let _ = window.show();
    let _ = window.set_focus();
    crate::window_theme::apply(&window, state, true);
    spawn_geometry_persistence(
        app.clone(),
        state.clone(),
        window,
        CONTENT_LABEL.to_string(),
        None,
    );
    Ok(())
}

pub fn open_settings_window(app: &AppHandle, state: &Arc<AppState>) -> BridgeResult<()> {
    if let Some(existing) = app.get_webview_window(SETTINGS_LABEL) {
        let _ = existing.show();
        let _ = existing.unminimize();
        let _ = existing.set_focus();
        return Ok(());
    }
    let url = host_url(state.port(), "settings.html");
    let parsed = Url::parse(&url)
        .map_err(|error| BridgeError::internal(format!("设置窗口 URL 无效：{error}")))?;
    let config = state.config_snapshot();
    let width = config
        .shell
        .settings_window_width
        .unwrap_or(680)
        .clamp(560, 8000);
    let height = config
        .shell
        .settings_window_height
        .unwrap_or(760)
        .clamp(520, 8000);
    let mut builder = WebviewWindowBuilder::new(app, SETTINGS_LABEL, WebviewUrl::External(parsed))
        .title("QTools — 设置")
        .inner_size(width as f64, height as f64)
        .min_inner_size(560.0, 520.0)
        .visible(false);
    if let (Some(left), Some(top)) = (
        config.shell.settings_window_left,
        config.shell.settings_window_top,
    ) {
        builder = builder.position(left as f64, top as f64);
    }
    let window = builder
        .build()
        .map_err(|error| BridgeError::internal(format!("创建设置窗口失败：{error}")))?;
    if let (Some(left), Some(top)) = (
        config.shell.settings_window_left,
        config.shell.settings_window_top,
    ) {
        restore_saved_position(&window, left, top);
    }
    let _ = window.show();
    let _ = window.set_focus();
    crate::window_theme::apply(&window, state, true);
    spawn_geometry_persistence(
        app.clone(),
        state.clone(),
        window,
        SETTINGS_LABEL.to_string(),
        None,
    );
    Ok(())
}

pub fn open_web_tool_window(
    app: &AppHandle,
    state: &Arc<AppState>,
    web: &LoadedWebTool,
) -> BridgeResult<()> {
    let label = format!("web-{}", web.entry.id);
    if let Some(existing) = app.get_webview_window(&label) {
        let _ = existing.show();
        let _ = existing.set_focus();
        return Ok(());
    }
    let parsed = Url::parse(&web.entry.url)
        .map_err(|error| BridgeError::new("WEB_URL_INVALID", format!("网址无效：{error}")))?;
    let window = WebviewWindowBuilder::new(app, &label, WebviewUrl::External(parsed))
        .title(web.name())
        .inner_size(1100.0, 780.0)
        .min_inner_size(420.0, 320.0)
        .visible(false)
        .build()
        .map_err(|error| BridgeError::internal(format!("创建在线工具窗口失败：{error}")))?;
    let _ = window.show();
    let _ = window.set_focus();
    crate::window_theme::apply(&window, state, true);
    Ok(())
}

pub fn close_content_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window(CONTENT_LABEL) {
        let _ = window.close();
    }
}

pub fn open_version_manager(
    app: &AppHandle,
    state: &Arc<AppState>,
    tool_id: &str,
) -> BridgeResult<()> {
    open_content_window(app, state, "version", Some(tool_id))
}

pub fn window_state_view(
    state: &Arc<AppState>,
    tool_id: &str,
) -> (bool, bool, Option<f64>, Option<f64>) {
    let config = state.config_snapshot();
    let Some(saved) = config.shell.tool_window_states.get(tool_id) else {
        return (false, false, None, None);
    };
    (saved.topmost, saved.pinned, saved.chip_left, saved.chip_top)
}

pub fn set_tool_topmost(
    window: &WebviewWindow,
    state: &Arc<AppState>,
    tool_id: &str,
    enabled: bool,
) -> BridgeResult<()> {
    window
        .set_always_on_top(enabled)
        .map_err(|error| BridgeError::internal(format!("设置置顶失败：{error}")))?;
    state.config.update(|config| {
        let entry = config
            .shell
            .tool_window_states
            .entry(tool_id.to_string())
            .or_insert_with(ToolWindowState::default);
        entry.topmost = enabled;
    });
    Ok(())
}

pub fn set_tool_pinned(
    window: &WebviewWindow,
    state: &Arc<AppState>,
    tool_id: &str,
    enabled: bool,
) -> BridgeResult<()> {
    window
        .set_resizable(!enabled)
        .map_err(|error| BridgeError::internal(format!("设置钉住失败：{error}")))?;
    #[cfg(windows)]
    crate::window_pin::set_position_locked(window, enabled);
    state.config.update(|config| {
        let entry = config
            .shell
            .tool_window_states
            .entry(tool_id.to_string())
            .or_insert_with(ToolWindowState::default);
        entry.pinned = enabled;
    });
    Ok(())
}

pub fn save_chip_position(
    state: &Arc<AppState>,
    tool_id: &str,
    left: f64,
    top: f64,
) -> BridgeResult<()> {
    state.config.update(|config| {
        let entry = config
            .shell
            .tool_window_states
            .entry(tool_id.to_string())
            .or_insert_with(ToolWindowState::default);
        entry.chip_left = Some(left);
        entry.chip_top = Some(top);
    });
    Ok(())
}

fn restore_tool_window_flags(window: &WebviewWindow, topmost: bool, pinned: bool) {
    if topmost {
        let _ = window.set_always_on_top(true);
    }
    if pinned {
        let _ = window.set_resizable(false);
        #[cfg(windows)]
        crate::window_pin::set_position_locked(window, true);
    }
}

fn spawn_geometry_persistence(
    app: AppHandle,
    state: Arc<AppState>,
    window: WebviewWindow,
    label: String,
    tool_id: Option<String>,
) {
    let observed = window.clone();
    window.on_window_event(move |event| {
        if let WindowEvent::CloseRequested { .. } | WindowEvent::Destroyed = event {
            persist_geometry(&state, &observed, &label, tool_id.as_deref());
            if label == CONTENT_LABEL {
                // 内容窗口关闭后不退出应用
            }
            let _ = &app;
        }
    });
}

/// 恢复窗口位置：Windows 下配置里的位置是物理像素（与 .NET 版一致），
/// Tauri builder 只接受逻辑坐标，所以在窗口创建后、首次显示前用物理坐标再定位一次。
pub fn restore_saved_position(window: &WebviewWindow, left: i32, top: i32) {
    #[cfg(windows)]
    {
        use windows_sys::Win32::Foundation::POINT;
        use windows_sys::Win32::Graphics::Gdi::{MONITOR_DEFAULTTONULL, MonitorFromPoint};
        let on_screen = unsafe {
            !MonitorFromPoint(POINT { x: left, y: top }, MONITOR_DEFAULTTONULL).is_null()
        };
        if on_screen {
            let _ = window.set_position(PhysicalPosition::new(left, top));
        } else {
            // 保存的位置已不在任何显示器上（拔屏/改布局）：退回居中，避免窗口开到屏幕外。
            let _ = window.center();
        }
    }
    #[cfg(not(windows))]
    {
        let _ = window.set_position(LogicalPosition::new(left as f64, top as f64));
    }
}

pub fn persist_geometry(
    state: &Arc<AppState>,
    window: &WebviewWindow,
    label: &str,
    tool_id: Option<&str>,
) {
    let Ok(position) = window.outer_position() else {
        return;
    };
    let Ok(size) = window.inner_size() else {
        return;
    };
    let scale = window.scale_factor().unwrap_or(1.0).max(0.1);
    // 尺寸统一存 DIP（与 .NET 版 config 语义一致）；Windows 位置存物理像素。
    let width = (size.width as f64 / scale).round().max(1.0) as i32;
    let height = (size.height as f64 / scale).round().max(1.0) as i32;
    #[cfg(windows)]
    let (left, top) = (position.x, position.y);
    #[cfg(not(windows))]
    let (left, top) = (
        (position.x as f64 / scale).round() as i32,
        (position.y as f64 / scale).round() as i32,
    );
    state.config.update(|config| match label {
        MAIN_LABEL => {
            config.shell.main_window_left = Some(left);
            config.shell.main_window_top = Some(top);
            config.shell.main_window_width = Some(width);
            config.shell.main_window_height = Some(height);
        }
        CONTENT_LABEL => {
            config.shell.content_window_left = Some(left);
            config.shell.content_window_top = Some(top);
            config.shell.content_window_width = Some(width);
            config.shell.content_window_height = Some(height);
        }
        SETTINGS_LABEL => {
            config.shell.settings_window_left = Some(left);
            config.shell.settings_window_top = Some(top);
            config.shell.settings_window_width = Some(width);
            config.shell.settings_window_height = Some(height);
        }
        _ => {
            if let Some(tool_id) = tool_id {
                let entry = config
                    .shell
                    .tool_window_states
                    .entry(tool_id.to_string())
                    .or_insert_with(ToolWindowState::default);
                entry.left = Some(left);
                entry.top = Some(top);
                entry.width = Some(width);
                entry.height = Some(height);
            }
        }
    });
}

const TRAY_MENU_WIDTH: f64 = 288.0;

pub fn tray_menu_height(count: i32) -> f64 {
    90.0 + 42.0 * count.clamp(1, 20) as f64
}

/// 托盘右键：记录光标位置，加载/刷新弹层页面；真正显示等页面渲染完成后回传 manager.trayMenuReady。
pub fn show_tray_menu(
    app: &AppHandle,
    state: &Arc<AppState>,
    cursor: (f64, f64),
) -> BridgeResult<()> {
    *state.tray_menu_cursor.lock() = Some(cursor);
    state
        .tray_menu_pending
        .store(true, std::sync::atomic::Ordering::SeqCst);
    let count = state.config_snapshot().shell.tray_menu_items;
    let height = tray_menu_height(count);
    let url = host_url(
        state.port(),
        &format!("tray-menu.html?t={}", chrono::Utc::now().timestamp_millis()),
    );

    if let Some(window) = app.get_webview_window(TRAY_MENU_LABEL) {
        let parsed = Url::parse(&url)
            .map_err(|error| BridgeError::internal(format!("托盘菜单 URL 无效：{error}")))?;
        let _ = window.set_size(LogicalSize::new(TRAY_MENU_WIDTH, height));
        let _ = window.navigate(parsed);
        schedule_tray_menu_fallback(app.clone(), state.clone());
        return Ok(());
    }

    let parsed = Url::parse(&url)
        .map_err(|error| BridgeError::internal(format!("托盘菜单 URL 无效：{error}")))?;
    let window = WebviewWindowBuilder::new(app, TRAY_MENU_LABEL, WebviewUrl::External(parsed))
        .title("QTools")
        .inner_size(TRAY_MENU_WIDTH, height)
        .decorations(false)
        .resizable(false)
        .always_on_top(true)
        .skip_taskbar(true)
        .visible(false)
        .build()
        .map_err(|error| BridgeError::internal(format!("创建托盘菜单失败：{error}")))?;
    crate::window_theme::apply(&window, state, false);
    schedule_tray_menu_fallback(app.clone(), state.clone());
    Ok(())
}

fn schedule_tray_menu_fallback(app: AppHandle, state: Arc<AppState>) {
    tauri::async_runtime::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_millis(1200)).await;
        if state
            .tray_menu_pending
            .load(std::sync::atomic::Ordering::SeqCst)
        {
            show_tray_menu_window(&app, &state);
        }
    });
}

/// manager.trayMenuReady：页面已经渲染，按光标位置定位并显示。
pub fn show_tray_menu_window(app: &AppHandle, state: &Arc<AppState>) {
    let Some(window) = app.get_webview_window(TRAY_MENU_LABEL) else {
        return;
    };
    if !state
        .tray_menu_pending
        .swap(false, std::sync::atomic::Ordering::SeqCst)
    {
        return;
    }
    let Some(cursor) = *state.tray_menu_cursor.lock() else {
        return;
    };
    let scale = window.scale_factor().unwrap_or(1.0);
    let physical_width = TRAY_MENU_WIDTH * scale;
    let physical_height = tray_menu_height(state.config_snapshot().shell.tray_menu_items) * scale;
    let work_areas: Vec<(f64, f64, f64, f64)> = app
        .available_monitors()
        .unwrap_or_default()
        .iter()
        .map(|monitor| {
            let area = monitor.work_area();
            (
                area.position.x as f64,
                area.position.y as f64,
                area.size.width as f64,
                area.size.height as f64,
            )
        })
        .collect();
    let work_area = work_areas
        .iter()
        .copied()
        .find(|(left, top, width, height)| {
            cursor.0 >= *left
                && cursor.0 <= left + width
                && cursor.1 >= *top
                && cursor.1 <= top + height
        })
        .or_else(|| work_areas.first().copied())
        .unwrap_or((0.0, 0.0, 1920.0, 1080.0));
    let (left, top) = tray_menu_placement(cursor, physical_width, physical_height, work_area);
    let _ = window.set_position(PhysicalPosition::new(
        left.round() as i32,
        top.round() as i32,
    ));
    let _ = window.show();
    let _ = window.set_focus();
    *state.tray_menu_shown_at.lock() = Some(std::time::Instant::now());
    crate::logging::info("托盘菜单已显示");
}

pub fn hide_tray_menu(app: &AppHandle, state: &Arc<AppState>) {
    state
        .tray_menu_pending
        .store(false, std::sync::atomic::Ordering::SeqCst);
    *state.tray_menu_shown_at.lock() = None;
    if let Some(window) = app.get_webview_window(TRAY_MENU_LABEL) {
        let _ = window.hide();
    }
}

/// 弹层定位（纯函数）：优先在光标左上方展开，上方空间不足改到下方，最后夹回工作区。
pub fn tray_menu_placement(
    cursor: (f64, f64),
    width: f64,
    height: f64,
    work_area: (f64, f64, f64, f64),
) -> (f64, f64) {
    const GAP: f64 = 10.0;
    let (area_left, area_top, area_width, area_height) = work_area;
    let mut left = cursor.0 - width + GAP * 2.0;
    let mut top = cursor.1 - height - GAP;
    if top < area_top + GAP {
        top = cursor.1 + GAP;
    }
    let max_left = area_left + area_width - width - GAP;
    let max_top = area_top + area_height - height - GAP;
    left = left.clamp(area_left + GAP, (area_left + GAP).max(max_left));
    top = top.clamp(area_top + GAP, (area_top + GAP).max(max_top));
    (left, top)
}

/// 系统通知唯一出口（仅 Windows，经系统通知中心；托盘气泡由系统统一呈现）。
pub fn notify(app: &AppHandle, title: &str, body: &str) -> BridgeResult<()> {
    if title.trim().is_empty() {
        return Err(BridgeError::bad_request("通知标题不能为空"));
    }
    #[cfg(windows)]
    {
        use tauri_plugin_notification::NotificationExt;
        app.notification()
            .builder()
            .title(title)
            .body(body)
            .show()
            .map_err(|error| {
                BridgeError::new("NOTIFY_UNAVAILABLE", format!("系统通知发送失败：{error}"))
            })?;
        crate::logging::info(&format!("已显示系统通知：{title}"));
        Ok(())
    }
    #[cfg(not(windows))]
    {
        let _ = (app, body);
        Err(BridgeError::new(
            "NOTIFY_UNAVAILABLE",
            "系统通知仅支持 Windows",
        ))
    }
}

pub fn launch_entry(app_state: &Arc<AppState>, app: &LoadedApp) -> BridgeResult<Option<u32>> {
    let result = crate::launcher::launch_app(app)?;
    app_state.usage.record(app.id());
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::tray_menu_placement;

    #[test]
    fn tray_menu_opens_above_cursor_and_flips_below_when_needed() {
        assert_eq!(
            tray_menu_placement((100.0, 100.0), 100.0, 100.0, (0.0, 0.0, 1000.0, 1000.0)),
            (20.0, 110.0)
        );
        assert_eq!(
            tray_menu_placement((700.0, 700.0), 100.0, 100.0, (0.0, 0.0, 1000.0, 1000.0)),
            (620.0, 590.0)
        );
    }

    #[test]
    fn tray_menu_is_clamped_inside_work_area() {
        let (left, top) = tray_menu_placement((5.0, 5.0), 300.0, 200.0, (0.0, 0.0, 1920.0, 1080.0));
        assert_eq!(left, 10.0);
        assert_eq!(top, 15.0);
    }

    #[test]
    fn tray_menu_height_follows_configured_count() {
        assert_eq!(super::tray_menu_height(1), 132.0);
        assert_eq!(super::tray_menu_height(10), 510.0);
        assert_eq!(super::tray_menu_height(999), super::tray_menu_height(20));
    }
}
