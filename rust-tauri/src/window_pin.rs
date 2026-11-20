//! 窗口位置锁定（钉住）。Tauri 的 `set_resizable(false)` 只挡边框缩放，挡不住标题栏拖动；
//! Windows 下子类化窗口过程，拦截 SC_MOVE / SC_SIZE / 标题栏拖动 / WM_MOVING / WM_SIZING。

#[cfg(windows)]
mod platform {
    use std::collections::HashMap;
    use std::sync::OnceLock;

    use parking_lot::Mutex;
    use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, RECT, TRUE, WPARAM};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CallWindowProcW, GWLP_WNDPROC, GetWindowLongPtrW, GetWindowRect, HTCAPTION, SC_MAXIMIZE,
        SC_MOVE, SC_SIZE, SetWindowLongPtrW, WM_MOVING, WM_NCDESTROY, WM_NCLBUTTONDBLCLK,
        WM_NCLBUTTONDOWN, WM_SIZING, WM_SYSCOMMAND, WNDPROC,
    };

    struct Hook {
        original: WNDPROC,
        locked: bool,
    }

    fn hooks() -> &'static Mutex<HashMap<usize, Hook>> {
        static HOOKS: OnceLock<Mutex<HashMap<usize, Hook>>> = OnceLock::new();
        HOOKS.get_or_init(|| Mutex::new(HashMap::new()))
    }

    unsafe extern "system" fn window_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        let key = hwnd as usize;
        let (original, locked) = {
            let map = hooks().lock();
            match map.get(&key) {
                Some(hook) => (hook.original, hook.locked),
                None => return 0,
            }
        };

        if locked {
            match msg {
                WM_NCLBUTTONDOWN | WM_NCLBUTTONDBLCLK if wparam as u32 == HTCAPTION => return 0,
                WM_SYSCOMMAND => {
                    let command = (wparam as u32) & 0xFFF0;
                    if command == SC_MOVE || command == SC_SIZE || command == SC_MAXIMIZE {
                        return 0;
                    }
                }
                WM_MOVING | WM_SIZING => {
                    if lparam != 0 {
                        let target = lparam as *mut RECT;
                        let mut current = RECT::default();
                        if unsafe { GetWindowRect(hwnd, &mut current) } != 0 {
                            unsafe { *target = current };
                        }
                    }
                    return TRUE as LRESULT;
                }
                WM_NCDESTROY => {
                    hooks().lock().remove(&key);
                    return unsafe { CallWindowProcW(original, hwnd, msg, wparam, lparam) };
                }
                _ => {}
            }
        } else if msg == WM_NCDESTROY {
            hooks().lock().remove(&key);
            return unsafe { CallWindowProcW(original, hwnd, msg, wparam, lparam) };
        }

        unsafe { CallWindowProcW(original, hwnd, msg, wparam, lparam) }
    }

    pub fn set_position_locked(window: &tauri::WebviewWindow, locked: bool) {
        let Ok(hwnd) = window.hwnd() else {
            crate::logging::warn("钉住窗口失败：拿不到窗口句柄");
            return;
        };
        let hwnd: HWND = hwnd.0;
        let key = hwnd as usize;
        let mut map = hooks().lock();
        if let Some(hook) = map.get_mut(&key) {
            hook.locked = locked;
            return;
        }
        if !locked {
            return;
        }
        let original: WNDPROC = unsafe {
            let raw = GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
            if raw == 0 {
                None
            } else {
                Some(std::mem::transmute::<
                    isize,
                    unsafe extern "system" fn(HWND, u32, WPARAM, LPARAM) -> LRESULT,
                >(raw))
            }
        };
        let Some(original) = original else {
            crate::logging::warn("钉住窗口失败：读取原窗口过程为空");
            return;
        };
        unsafe {
            SetWindowLongPtrW(
                hwnd,
                GWLP_WNDPROC,
                window_proc as *const () as usize as isize,
            );
        }
        map.insert(
            key,
            Hook {
                original: Some(original),
                locked,
            },
        );
    }
}

#[cfg(windows)]
pub fn set_position_locked(window: &tauri::WebviewWindow, locked: bool) {
    platform::set_position_locked(window, locked);
}

#[cfg(not(windows))]
pub fn set_position_locked(_window: &tauri::WebviewWindow, _locked: bool) {}
