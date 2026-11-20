//! 全局热键 Alt+Space：呼出/隐藏主窗口（仅 Windows）。
//! RegisterHotKey 绑定调用线程，因此在独立线程里注册并泵消息；
//! 热键被其他程序占用时注册失败，不抛异常。

#[cfg(windows)]
mod platform {
    use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
    use std::sync::mpsc;
    use std::time::Duration;

    use tauri::AppHandle;
    use windows_sys::Win32::System::Threading::GetCurrentThreadId;
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        MOD_ALT, RegisterHotKey, UnregisterHotKey, VK_SPACE,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        GetMessageW, MSG, PostQuitMessage, PostThreadMessageW, WM_HOTKEY,
    };

    const HOTKEY_ID: i32 = 0x5154;
    const WM_APP_STOP: u32 = 0x8001;
    static REGISTERED: AtomicBool = AtomicBool::new(false);
    static THREAD_ID: AtomicU32 = AtomicU32::new(0);

    pub fn start(app: AppHandle) -> bool {
        if REGISTERED.load(Ordering::SeqCst) {
            return true;
        }
        let (ready_tx, ready_rx) = mpsc::channel::<bool>();
        let spawned = std::thread::Builder::new()
            .name("qtools-hotkey".into())
            .spawn(move || {
                let registered = unsafe {
                    RegisterHotKey(std::ptr::null_mut(), HOTKEY_ID, MOD_ALT, VK_SPACE as u32)
                } != 0;
                REGISTERED.store(registered, Ordering::SeqCst);
                if registered {
                    THREAD_ID.store(unsafe { GetCurrentThreadId() }, Ordering::SeqCst);
                }
                let _ = ready_tx.send(registered);
                if !registered {
                    crate::logging::warn("全局热键 Alt+Space 注册失败（可能被其他程序占用）");
                    return;
                }
                crate::logging::info("全局热键 Alt+Space 已注册");
                let mut message = MSG::default();
                loop {
                    let result = unsafe { GetMessageW(&mut message, std::ptr::null_mut(), 0, 0) };
                    if result <= 0 {
                        break;
                    }
                    if message.message == WM_HOTKEY && message.wParam as i32 == HOTKEY_ID {
                        crate::tray::toggle_main(&app);
                    } else if message.message == WM_APP_STOP {
                        unsafe {
                            UnregisterHotKey(std::ptr::null_mut(), HOTKEY_ID);
                            PostQuitMessage(0);
                        }
                        REGISTERED.store(false, Ordering::SeqCst);
                        THREAD_ID.store(0, Ordering::SeqCst);
                        break;
                    }
                }
            });
        if spawned.is_err() {
            return false;
        }
        ready_rx
            .recv_timeout(Duration::from_secs(3))
            .unwrap_or(false)
    }

    pub fn stop() {
        let thread_id = THREAD_ID.swap(0, Ordering::SeqCst);
        if thread_id != 0 {
            unsafe {
                PostThreadMessageW(thread_id, WM_APP_STOP, 0, 0);
            }
        }
        REGISTERED.store(false, Ordering::SeqCst);
    }
}

#[cfg(windows)]
pub fn start(app: tauri::AppHandle) -> bool {
    platform::start(app)
}

#[cfg(windows)]
pub fn stop() {
    platform::stop();
}

#[cfg(not(windows))]
pub fn start(_app: tauri::AppHandle) -> bool {
    false
}

#[cfg(not(windows))]
pub fn stop() {}
