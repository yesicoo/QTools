use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;

use crate::error::{BridgeError, BridgeResult};

const CURRENT_VERSION: u32 = 1;
const MAX_VALUE_BYTES: usize = 256 * 1024;
const MAX_TOTAL_BYTES: usize = 5 * 1024 * 1024;
const MAX_KEY_LENGTH: usize = 128;

#[derive(Serialize, Deserialize, Default)]
struct StorageFile {
    version: u32,
    items: HashMap<String, Value>,
}

/// 按 toolId 隔离的键值存储（data/storage/{toolId}.json，临时文件 + 原子替换）。
pub struct ToolStorage {
    directory: PathBuf,
    gate: Mutex<()>,
}

impl ToolStorage {
    pub fn new(directory: PathBuf) -> Self {
        let _ = fs::create_dir_all(&directory);
        Self {
            directory,
            gate: Mutex::new(()),
        }
    }

    fn path(&self, tool_id: &str) -> PathBuf {
        self.directory.join(format!("{tool_id}.json"))
    }

    fn guard(&self) -> parking_lot::MutexGuard<'_, ()> {
        self.gate.lock()
    }

    pub fn validate_key(key: &str) -> BridgeResult<()> {
        if key.is_empty() || key.chars().count() > MAX_KEY_LENGTH {
            return Err(BridgeError::new(
                "STORAGE_KEY_INVALID",
                format!("key 必填且不超过 {MAX_KEY_LENGTH} 字符"),
            ));
        }
        if !key
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '.' | '_' | '-'))
        {
            return Err(BridgeError::new(
                "STORAGE_KEY_INVALID",
                "key 只允许字母、数字、. _ -",
            ));
        }
        Ok(())
    }

    fn load(&self, tool_id: &str) -> StorageFile {
        let path = self.path(tool_id);
        let Ok(text) = fs::read_to_string(&path) else {
            return StorageFile {
                version: CURRENT_VERSION,
                items: HashMap::new(),
            };
        };
        match serde_json::from_str::<StorageFile>(&text) {
            Ok(mut file) => {
                if file.version == 0 {
                    file.version = CURRENT_VERSION;
                }
                file
            }
            Err(error) => {
                let backup = path.with_file_name(format!(
                    "{}.corrupt-{}",
                    path.file_name().unwrap_or_default().to_string_lossy(),
                    chrono::Local::now().format("%Y%m%d%H%M%S")
                ));
                let _ = fs::rename(&path, backup);
                crate::logging::warn(&format!("存储文件损坏已备份：{error}"));
                StorageFile {
                    version: CURRENT_VERSION,
                    items: HashMap::new(),
                }
            }
        }
    }

    fn save(&self, tool_id: &str, file: &StorageFile) -> BridgeResult<()> {
        let path = self.path(tool_id);
        let temp = path.with_extension("json.tmp");
        let json = serde_json::to_string(file)?;
        fs::write(&temp, json).and_then(|_| fs::rename(&temp, &path))?;
        Ok(())
    }

    pub fn get(&self, tool_id: &str, key: &str) -> BridgeResult<Option<Value>> {
        Self::validate_key(key)?;
        let _guard = self.guard();
        Ok(self.load(tool_id).items.get(key).cloned())
    }

    pub fn set(&self, tool_id: &str, key: &str, value: Value) -> BridgeResult<()> {
        Self::validate_key(key)?;
        let raw = serde_json::to_string(&value)?;
        if raw.len() > MAX_VALUE_BYTES {
            return Err(BridgeError::new(
                "STORAGE_VALUE_TOO_LARGE",
                format!("单个值不超过 {} KB", MAX_VALUE_BYTES / 1024),
            ));
        }
        let _guard = self.guard();
        let mut file = self.load(tool_id);
        file.items.insert(key.to_string(), value);
        let total = serde_json::to_string(&file)?.len();
        if total > MAX_TOTAL_BYTES {
            return Err(BridgeError::new(
                "STORAGE_QUOTA_EXCEEDED",
                "单个工具存储总量不超过 5 MB",
            ));
        }
        self.save(tool_id, &file)
    }

    pub fn remove(&self, tool_id: &str, key: &str) -> BridgeResult<()> {
        Self::validate_key(key)?;
        let _guard = self.guard();
        let mut file = self.load(tool_id);
        if file.items.remove(key).is_some() {
            self.save(tool_id, &file)?;
        }
        Ok(())
    }

    pub fn list(&self, tool_id: &str) -> BridgeResult<Vec<String>> {
        let _guard = self.guard();
        let mut keys: Vec<String> = self.load(tool_id).items.into_keys().collect();
        keys.sort();
        Ok(keys)
    }

    pub fn size_bytes(&self, tool_id: &str) -> u64 {
        fs::metadata(self.path(tool_id))
            .map(|meta| meta.len())
            .unwrap_or(0)
    }

    pub fn delete(&self, tool_id: &str) -> bool {
        fs::remove_file(self.path(tool_id)).is_ok()
    }
}
