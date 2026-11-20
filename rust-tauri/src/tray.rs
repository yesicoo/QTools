use std::sync::Arc;

use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Manager};

use crate::shell;
use crate::state::AppState;

/// Windows 托盘：左键显示主窗口，右键弹出与界面一致的 HTML 条目弹层。
/// 原生菜单不再使用；弹层条目点击走 manager.openTool / openWebTool / launchApp。
pub fn init(app: &AppHandle) -> tauri::Result<()> {
    let mut builder = TrayIconBuilder::with_id("qtools")
        .tooltip("QTools")
        .show_menu_on_left_click(false)
        .on_tray_icon_event(|tray, event| {
            let app = tray.app_handle();
            match event {
                TrayIconEvent::Click {
                    button: MouseButton::Left,
                    button_state: MouseButtonState::Up,
                    ..
                } => show_main(app),
                TrayIconEvent::Click {
                    button: MouseButton::Right,
                    button_state: MouseButtonState::Up,
                    position,
                    ..
                } => {
                    if let Some(state) = app.try_state::<Arc<AppState>>() {
                        if let Err(error) =
                            shell::show_tray_menu(app, state.inner(), (position.x, position.y))
                        {
                            crate::logging::error(&format!("显示托盘菜单失败：{error}"));
                        }
                    }
                }
                _ => {}
            }
        });
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder.build(app)?;
    Ok(())
}

pub fn show_main(app: &AppHandle) {
    if let Some(window) = app.get_webview_window(shell::MAIN_LABEL) {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

/// 全局热键：前台可见时隐藏，否则显示并置前。
pub fn toggle_main(app: &AppHandle) {
    let Some(window) = app.get_webview_window(shell::MAIN_LABEL) else {
        return;
    };
    let visible = window.is_visible().unwrap_or(false);
    let focused = window.is_focused().unwrap_or(false);
    if visible && focused {
        let _ = window.hide();
    } else {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}
