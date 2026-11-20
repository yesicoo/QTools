use std::sync::Arc;

use tauri::{Manager, Theme, WebviewWindow};

use crate::config::{is_dark_now, normalize_theme};
use crate::state::AppState;

/// 应用窗口标题栏主题：Tauri 设置 + Windows DWM 刷新。
pub fn apply(window: &WebviewWindow, state: &Arc<AppState>, refresh_frame: bool) {
    let config = state.config_snapshot();
    let theme = normalize_theme(&config.shell.theme);
    let dark = is_dark_now(theme);
    let _ = window.set_theme(Some(if dark { Theme::Dark } else { Theme::Light }));
    #[cfg(windows)]
    {
        if let Ok(handle) = window.hwnd() {
            set_dwm_dark(handle.0, dark);
            if refresh_frame {
                refresh_nc(handle.0);
            }
        }
    }
    let _ = refresh_frame;
}

pub fn broadcast(app: &tauri::AppHandle) {
    for (_label, window) in app.webview_windows() {
        let state = app.try_state::<Arc<AppState>>();
        if let Some(state) = state {
            apply(&window, state.inner(), true);
        }
    }
}

#[cfg(windows)]
fn set_dwm_dark(hwnd: *mut core::ffi::c_void, dark: bool) {
    use windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute;
    let value: i32 = if dark { 1 } else { 0 };
    unsafe {
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20（Win10 早期版本用 19）
        let result = DwmSetWindowAttribute(
            hwnd,
            20,
            &value as *const i32 as *const core::ffi::c_void,
            std::mem::size_of::<i32>() as u32,
        );
        if result != 0 {
            let _ = DwmSetWindowAttribute(
                hwnd,
                19,
                &value as *const i32 as *const core::ffi::c_void,
                std::mem::size_of::<i32>() as u32,
            );
        }
    }
}

#[cfg(windows)]
fn refresh_nc(hwnd: *mut core::ffi::c_void) {
    use windows_sys::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, SendMessageW};
    const WM_NCACTIVATE: u32 = 0x0086;
    unsafe {
        let active = GetForegroundWindow() == hwnd;
        let opposite = if active { 0usize } else { 1usize };
        let actual = if active { 1usize } else { 0usize };
        SendMessageW(hwnd, WM_NCACTIVATE, opposite, 0);
        SendMessageW(hwnd, WM_NCACTIVATE, actual, 0);
    }
}
