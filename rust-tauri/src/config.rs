use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::io::Write;
use std::path::Path;

use crate::paths::AppPaths;

/// 宿主配置（data/config.json）。**绝不包含 API Key**，Key 只进系统保护存储。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AppConfig {
    pub ai: AiConfigSection,
    pub server: ServerConfigSection,
    pub storage: StorageConfigSection,
    pub backup: BackupConfigSection,
    pub shell: ShellConfigSection,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AiConfigSection {
    pub base_url: String,
    pub model: String,
    pub temperature: f64,
    pub timeout_seconds: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ServerConfigSection {
    pub max_request_body_bytes: u64,
    pub max_file_bytes: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StorageConfigSection {
    pub max_value_bytes: u64,
    pub max_total_bytes_per_tool: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct BackupConfigSection {
    pub git_remote_url: Option<String>,
    pub last_push_utc: Option<String>,
    pub last_push_status: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct ToolWindowState {
    pub topmost: bool,
    pub pinned: bool,
    pub chip_left: Option<f64>,
    pub chip_top: Option<f64>,
    pub left: Option<i32>,
    pub top: Option<i32>,
    pub width: Option<i32>,
    pub height: Option<i32>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ShellConfigSection {
    pub hotkey_enabled: bool,
    pub main_window_width: Option<i32>,
    pub main_window_height: Option<i32>,
    pub main_window_left: Option<i32>,
    pub main_window_top: Option<i32>,
    pub tray_menu_items: i32,
    pub content_window_left: Option<i32>,
    pub content_window_top: Option<i32>,
    pub content_window_width: Option<i32>,
    pub content_window_height: Option<i32>,
    pub settings_window_left: Option<i32>,
    pub settings_window_top: Option<i32>,
    pub settings_window_width: Option<i32>,
    pub settings_window_height: Option<i32>,
    pub content_window_topmost: bool,
    pub content_window_pinned: bool,
    pub tool_window_states: HashMap<String, ToolWindowState>,
    pub theme: String,
}

impl Default for AiConfigSection {
    fn default() -> Self {
        Self {
            base_url: "https://api.deepseek.com".into(),
            model: "deepseek-flash".into(),
            temperature: 0.7,
            timeout_seconds: 60,
        }
    }
}

impl Default for ServerConfigSection {
    fn default() -> Self {
        Self {
            max_request_body_bytes: 1024 * 1024,
            max_file_bytes: 8 * 1024 * 1024,
        }
    }
}

impl Default for StorageConfigSection {
    fn default() -> Self {
        Self {
            max_value_bytes: 256 * 1024,
            max_total_bytes_per_tool: 5 * 1024 * 1024,
        }
    }
}

impl Default for ShellConfigSection {
    fn default() -> Self {
        Self {
            hotkey_enabled: true,
            main_window_width: None,
            main_window_height: None,
            main_window_left: None,
            main_window_top: None,
            tray_menu_items: 10,
            content_window_left: None,
            content_window_top: None,
            content_window_width: None,
            content_window_height: None,
            settings_window_left: None,
            settings_window_top: None,
            settings_window_width: None,
            settings_window_height: None,
            content_window_topmost: false,
            content_window_pinned: false,
            tool_window_states: HashMap::new(),
            theme: "system".into(),
        }
    }
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            ai: AiConfigSection::default(),
            server: ServerConfigSection::default(),
            storage: StorageConfigSection::default(),
            backup: BackupConfigSection::default(),
            shell: ShellConfigSection::default(),
        }
    }
}

impl AppConfig {
    pub fn load(path: &Path) -> Self {
        if !path.exists() {
            let defaults = Self::default();
            let _ = save_config(path, &defaults);
            return defaults;
        }

        match fs::read_to_string(path) {
            Ok(text) => match serde_json::from_str::<Self>(&text) {
                Ok(mut config) => {
                    config.normalize();
                    config
                }
                Err(_) => {
                    let backup = path.with_file_name(format!(
                        "{}.corrupt-{}",
                        path.file_name().unwrap_or_default().to_string_lossy(),
                        chrono::Local::now().format("%Y%m%d%H%M%S")
                    ));
                    let _ = fs::rename(path, backup);
                    Self::default()
                }
            },
            Err(_) => Self::default(),
        }
    }

    fn normalize(&mut self) {
        if self.ai.base_url.trim().is_empty() {
            self.ai.base_url = AiConfigSection::default().base_url;
        }
        if self.ai.model.trim().is_empty() {
            self.ai.model = AiConfigSection::default().model;
        }
        if !(0.0..=2.0).contains(&self.ai.temperature) || !self.ai.temperature.is_finite() {
            self.ai.temperature = 0.7;
        }
        if self.ai.timeout_seconds == 0 || self.ai.timeout_seconds > 600 {
            self.ai.timeout_seconds = 60;
        }
        if self.shell.tray_menu_items < 1 || self.shell.tray_menu_items > 20 {
            self.shell.tray_menu_items = 10;
        }
        self.shell.theme = normalize_theme(&self.shell.theme).to_string();
    }
}

pub fn normalize_theme(value: &str) -> &'static str {
    match value.trim().to_ascii_lowercase().as_str() {
        "light" => "light",
        "dark" => "dark",
        _ => "system",
    }
}

pub fn explicit_theme(theme: &str) -> Option<&'static str> {
    match normalize_theme(theme) {
        "light" => Some("light"),
        "dark" => Some("dark"),
        _ => None,
    }
}

pub fn is_dark_now(theme: &str) -> bool {
    match normalize_theme(theme) {
        "light" => false,
        "dark" => true,
        _ => system_prefers_dark(),
    }
}

#[cfg(windows)]
fn system_prefers_dark() -> bool {
    use std::ffi::c_void;
    use windows_sys::Win32::System::Registry::{
        HKEY, HKEY_CURRENT_USER, KEY_READ, REG_DWORD, RegCloseKey, RegOpenKeyExW, RegQueryValueExW,
    };
    unsafe {
        let mut key: HKEY = std::ptr::null_mut();
        let sub = wide("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
        if RegOpenKeyExW(HKEY_CURRENT_USER, sub.as_ptr(), 0, KEY_READ, &mut key) != 0 {
            return false;
        }
        let name = wide("AppsUseLightTheme");
        let mut value: u32 = 1;
        let mut size: u32 = std::mem::size_of::<u32>() as u32;
        let mut kind: u32 = 0;
        let status = RegQueryValueExW(
            key,
            name.as_ptr(),
            std::ptr::null(),
            &mut kind,
            &mut value as *mut u32 as *mut u8,
            &mut size,
        );
        let _ = RegCloseKey(key);
        let _ = std::mem::size_of::<*mut c_void>();
        status == 0 && kind == REG_DWORD && value == 0
    }
}

#[cfg(windows)]
fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(not(windows))]
fn system_prefers_dark() -> bool {
    false
}

pub fn save_config(path: &Path, config: &AppConfig) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let json = serde_json::to_string(config).map_err(std::io::Error::other)?;
    let temp = path.with_extension("json.tmp");
    {
        let mut file = fs::File::create(&temp)?;
        file.write_all(json.as_bytes())?;
        file.sync_all()?;
    }
    fs::rename(&temp, path)
}

pub struct ConfigStore {
    pub path: std::path::PathBuf,
    inner: parking_lot::RwLock<AppConfig>,
}

impl ConfigStore {
    pub fn new(paths: &AppPaths) -> Self {
        let path = paths.config_file();
        let config = AppConfig::load(&path);
        Self {
            path,
            inner: parking_lot::RwLock::new(config),
        }
    }

    pub fn get(&self) -> AppConfig {
        self.inner.read().clone()
    }

    pub fn reload(&self) {
        let config = AppConfig::load(&self.path);
        *self.inner.write() = config;
    }

    pub fn update<F>(&self, change: F)
    where
        F: FnOnce(&mut AppConfig),
    {
        let mut config = self.inner.write();
        change(&mut config);
        let _ = save_config(&self.path, &config);
    }
}

#[cfg(test)]
mod tests {
    use super::{explicit_theme, normalize_theme};

    #[test]
    fn theme_values_are_normalized() {
        assert_eq!(normalize_theme("dark"), "dark");
        assert_eq!(normalize_theme("DARK"), "dark");
        assert_eq!(normalize_theme(" light "), "light");
        assert_eq!(normalize_theme("unknown"), "system");
    }

    #[test]
    fn system_theme_is_left_to_css_media_query() {
        assert_eq!(explicit_theme("system"), None);
        assert_eq!(explicit_theme("dark"), Some("dark"));
        assert_eq!(explicit_theme("light"), Some("light"));
    }
}
