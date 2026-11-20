use std::fs;
use std::path::{Path, PathBuf};

use base64::Engine;

use crate::error::{BridgeError, BridgeResult};

const MAX_FILE_BYTES: u64 = 50 * 1024 * 1024;
const MAX_TOTAL_BYTES: u64 = 200 * 1024 * 1024;
const MAX_NAME_LENGTH: usize = 128;
const ALLOWED_EXTENSIONS: &[&str] = &[
    "webm", "wav", "mp3", "ogg", "m4a", "aac", "flac", "opus", "mp4", "mov", "avi", "mkv", "png",
    "jpg", "jpeg", "gif", "webp", "bmp", "svg", "pdf", "txt", "csv", "json", "zip",
];

pub struct ToolFiles {
    root: PathBuf,
}

impl ToolFiles {
    pub fn new(tool_directory: &Path) -> Self {
        let root = tool_directory.join("files");
        let _ = fs::create_dir_all(&root);
        Self { root }
    }

    pub fn validate_name(name: &str) -> BridgeResult<()> {
        if name.trim().is_empty() || name.chars().count() > MAX_NAME_LENGTH {
            return Err(BridgeError::new(
                "FILE_NAME_INVALID",
                format!("文件名必填且不超过 {MAX_NAME_LENGTH} 字符"),
            ));
        }
        if name.contains('/') || name.contains('\\') || name.contains('\0') || name.contains("..") {
            return Err(BridgeError::new(
                "FILE_NAME_INVALID",
                "文件名不能包含路径分隔符或 ..",
            ));
        }
        if name.starts_with('.') || name.ends_with('.') || name.ends_with(' ') {
            return Err(BridgeError::new(
                "FILE_NAME_INVALID",
                "文件名不能以 . 或空格结尾",
            ));
        }
        let extension = Path::new(name)
            .extension()
            .map(|value| value.to_string_lossy().to_ascii_lowercase())
            .unwrap_or_default();
        if !ALLOWED_EXTENSIONS.contains(&extension.as_str()) {
            return Err(BridgeError::new(
                "FILE_TYPE_NOT_ALLOWED",
                format!("不支持的扩展名 .{extension}"),
            ));
        }
        Ok(())
    }

    fn path_for(&self, name: &str) -> PathBuf {
        self.root.join(name)
    }

    pub fn save(
        &self,
        name: &str,
        data: &[u8],
        _content_type: Option<&str>,
    ) -> BridgeResult<(u64, String)> {
        Self::validate_name(name)?;
        if data.is_empty() {
            return Err(BridgeError::new("FILE_EMPTY", "文件内容不能为空"));
        }
        if data.len() as u64 > MAX_FILE_BYTES {
            return Err(BridgeError::new("FILE_TOO_LARGE", "单个文件不超过 50 MB"));
        }
        let path = self.path_for(name);
        let existing = fs::metadata(&path).map(|meta| meta.len()).unwrap_or(0);
        let total: u64 = fs::read_dir(&self.root)
            .map(|entries| {
                entries
                    .flatten()
                    .filter_map(|entry| entry.metadata().ok())
                    .filter(|meta| meta.is_file())
                    .map(|meta| meta.len())
                    .sum()
            })
            .unwrap_or(0);
        if total.saturating_sub(existing) + data.len() as u64 > MAX_TOTAL_BYTES {
            return Err(BridgeError::new(
                "TOOL_QUOTA_EXCEEDED",
                "工具文件总容量不超过 200 MB",
            ));
        }
        let temp = path.with_extension(format!(
            "{}.tmp",
            path.extension()
                .map(|value| value.to_string_lossy())
                .unwrap_or_default()
        ));
        fs::write(&temp, data).and_then(|_| fs::rename(&temp, &path))?;
        let size = fs::metadata(&path)
            .map(|meta| meta.len())
            .unwrap_or(data.len() as u64);
        let modified = fs::metadata(&path)
            .and_then(|meta| meta.modified())
            .map(|value| {
                let datetime: chrono::DateTime<chrono::Utc> = value.into();
                datetime.to_rfc3339()
            })
            .unwrap_or_default();
        Ok((size, modified))
    }

    pub fn list(&self) -> Vec<(String, u64, String)> {
        let Ok(entries) = fs::read_dir(&self.root) else {
            return Vec::new();
        };
        let mut files: Vec<(String, u64, String)> = entries
            .flatten()
            .filter(|entry| entry.path().is_file())
            .filter(|entry| {
                !entry
                    .file_name()
                    .to_string_lossy()
                    .to_ascii_lowercase()
                    .ends_with(".tmp")
            })
            .map(|entry| {
                let name = entry.file_name().to_string_lossy().into_owned();
                let meta = entry.metadata().ok();
                let size = meta.as_ref().map(|meta| meta.len()).unwrap_or(0);
                let modified = meta
                    .and_then(|meta| meta.modified().ok())
                    .map(|value| {
                        let datetime: chrono::DateTime<chrono::Utc> = value.into();
                        datetime.to_rfc3339()
                    })
                    .unwrap_or_default();
                (name, size, modified)
            })
            .collect();
        files.sort_by(|left, right| right.2.cmp(&left.2));
        files
    }

    pub fn read(&self, name: &str) -> BridgeResult<(Vec<u8>, String)> {
        Self::validate_name(name)?;
        let path = self.path_for(name);
        if !path.is_file() {
            return Err(BridgeError::new(
                "FILE_NOT_FOUND",
                format!("文件不存在：{name}"),
            ));
        }
        let data = fs::read(&path)?;
        let content_type = crate::server::content_type_for(name);
        Ok((data, content_type))
    }

    pub fn delete(&self, name: &str) -> BridgeResult<bool> {
        Self::validate_name(name)?;
        let path = self.path_for(name);
        Ok(fs::remove_file(path).is_ok())
    }
}

pub fn encode_base64(data: &[u8]) -> String {
    base64::engine::general_purpose::STANDARD.encode(data)
}

pub fn decode_base64(data: &str) -> BridgeResult<Vec<u8>> {
    base64::engine::general_purpose::STANDARD
        .decode(data.trim())
        .map_err(|_| BridgeError::bad_request("dataBase64 不是合法的 Base64"))
}
