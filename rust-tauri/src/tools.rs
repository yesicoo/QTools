use parking_lot::RwLock;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use crate::error::BridgeError;

pub const PERMISSIONS: [&str; 4] = ["storage", "ai", "media", "files"];

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct ToolWindowSettings {
    pub width: i32,
    pub height: i32,
    pub resizable: bool,
}

impl ToolWindowSettings {
    pub fn sane(&self, fallback_width: i32, fallback_height: i32) -> (i32, i32) {
        let width = if (320..=3840).contains(&self.width) {
            self.width
        } else {
            fallback_width
        };
        let height = if (320..=3840).contains(&self.height) {
            self.height
        } else {
            fallback_height
        };
        (width, height)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ToolManifest {
    pub id: String,
    pub name: String,
    pub version: String,
    pub description: Option<String>,
    pub entry: String,
    pub permissions: Vec<String>,
    pub window: ToolWindowSettings,
    pub created_at: String,
    pub allow_eval: bool,
    pub csp: Option<String>,
    pub category: Option<String>,
    pub glyph: Option<String>,
}

impl Default for ToolManifest {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            version: "1.0.0".into(),
            description: None,
            entry: "index.html".into(),
            permissions: Vec::new(),
            window: ToolWindowSettings {
                width: 720,
                height: 720,
                resizable: true,
            },
            created_at: String::new(),
            allow_eval: false,
            csp: None,
            category: None,
            glyph: None,
        }
    }
}

#[derive(Clone, Debug)]
pub struct LoadedTool {
    pub manifest: ToolManifest,
    pub directory: PathBuf,
    pub last_modified_utc: String,
    pub is_valid: bool,
    pub errors: Vec<String>,
    pub permissions: Vec<String>,
    pub versions: Vec<ToolVersion>,
}

impl LoadedTool {
    pub fn name(&self) -> &str {
        if self.manifest.name.trim().is_empty() {
            &self.manifest.id
        } else {
            &self.manifest.name
        }
    }

    pub fn entry(&self) -> &str {
        if self.manifest.entry.trim().is_empty() {
            "index.html"
        } else {
            &self.manifest.entry
        }
    }

    pub fn has_permission(&self, permission: &str) -> bool {
        self.permissions.iter().any(|value| value == permission)
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolVersion {
    pub version: String,
    pub created_at: String,
    pub size_bytes: u64,
    pub is_active: bool,
    pub note: Option<String>,
}

pub fn is_valid_tool_id(value: &str) -> bool {
    let bytes = value.as_bytes();
    if bytes.is_empty() || bytes.len() > 63 {
        return false;
    }
    if bytes[0] == b'-' || bytes[bytes.len() - 1] == b'-' {
        return false;
    }
    bytes
        .iter()
        .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'-')
}

pub fn is_valid_version(value: &str) -> bool {
    let core = value.split('-').next().unwrap_or_default();
    let parts: Vec<&str> = core.split('.').collect();
    parts.len() == 3
        && parts
            .iter()
            .all(|part| !part.is_empty() && part.bytes().all(|byte| byte.is_ascii_digit()))
        && (value.len() == core.len()
            || value[core.len()..]
                .chars()
                .all(|ch| ch == '-' || ch == '.' || ch.is_ascii_alphanumeric()))
}

pub fn is_safe_relative_path(relative: &str) -> bool {
    if relative.trim().is_empty()
        || relative.contains('\\')
        || relative.contains('\0')
        || Path::new(relative).is_absolute()
        || relative.as_bytes().get(1) == Some(&b':')
    {
        return false;
    }
    let segments: Vec<&str> = relative
        .split('/')
        .filter(|segment| !segment.trim().is_empty())
        .collect();
    if segments.is_empty() {
        return false;
    }
    segments
        .iter()
        .all(|segment| !segment.starts_with('.') && !segment.contains('\0'))
}

pub fn validate_manifest(manifest: &ToolManifest, directory_name: &str) -> Vec<String> {
    let mut errors = Vec::new();
    if !is_valid_tool_id(&manifest.id) {
        errors.push("id 必须匹配小写字母/数字/短横线规则（1~63 字符）".into());
    } else if manifest.id != directory_name {
        errors.push(format!(
            "id({}) 必须与工具目录名({}) 一致",
            manifest.id, directory_name
        ));
    }
    if manifest.name.trim().is_empty() || manifest.name.trim().chars().count() > 60 {
        errors.push("name 必填且不超过 60 字符".into());
    }
    if !is_valid_version(&manifest.version) {
        errors.push("version 必填且为语义化版本（如 1.0.0）".into());
    }
    if manifest
        .description
        .as_deref()
        .is_some_and(|value| value.chars().count() > 300)
    {
        errors.push("description 不超过 300 字符".into());
    }
    if manifest
        .category
        .as_deref()
        .is_some_and(|value| value.chars().count() > 30)
    {
        errors.push("category 不超过 30 字符".into());
    }
    if manifest
        .glyph
        .as_deref()
        .is_some_and(|value| value.chars().count() > 4)
    {
        errors.push("glyph 不超过 4 字符".into());
    }
    if !is_safe_relative_path(&manifest.entry) {
        errors.push("entry 必须是工具目录内的相对路径（禁止 ..、绝对路径、盘符、反斜杠）".into());
    }
    for permission in &manifest.permissions {
        if !PERMISSIONS.contains(&permission.trim().to_lowercase().as_str()) {
            errors.push(format!(
                "未知权限 '{permission}'（仅支持 storage / ai / media / files）"
            ));
        }
    }
    if !(320..=3840).contains(&manifest.window.width)
        || !(320..=3840).contains(&manifest.window.height)
    {
        errors.push("window.width/height 必须在 320~3840 之间".into());
    }
    if manifest.created_at.trim().is_empty()
        || chrono::DateTime::parse_from_rfc3339(&manifest.created_at).is_err()
    {
        errors.push("createdAt 必填且为 ISO-8601 日期时间".into());
    }
    errors
}

pub fn parse_permissions(values: &[String]) -> Vec<String> {
    let mut result: Vec<String> = Vec::new();
    for value in values {
        let normalized = value.trim().to_ascii_lowercase();
        if PERMISSIONS.contains(&normalized.as_str()) && !result.contains(&normalized) {
            result.push(normalized);
        }
    }
    result
}

/// 版本存储：`.versions/{version}/`（点号前缀目录，静态服务明确拒绝）。
pub struct ToolVersionStore;

impl ToolVersionStore {
    pub const VERSIONS_DIR: &'static str = ".versions";
    const VERSIONED_ENTRIES: [&'static str; 2] = ["manifest.json", "index.html"];

    pub fn versions_root(tool_dir: &Path) -> PathBuf {
        tool_dir.join(Self::VERSIONS_DIR)
    }

    pub fn version_dir(tool_dir: &Path, version: &str) -> PathBuf {
        Self::versions_root(tool_dir).join(version)
    }

    pub fn list(tool_dir: &Path, active_version: &str) -> Vec<ToolVersion> {
        let root = Self::versions_root(tool_dir);
        let Ok(entries) = fs::read_dir(&root) else {
            return Vec::new();
        };
        let mut versions: Vec<ToolVersion> = entries
            .flatten()
            .filter(|entry| entry.path().is_dir())
            .filter_map(|entry| {
                let version = entry.file_name().to_string_lossy().into_owned();
                if !is_valid_version(&version) {
                    return None;
                }
                let directory = entry.path();
                let created = fs::metadata(directory.join("manifest.json"))
                    .and_then(|meta| meta.created().or_else(|_| meta.modified()))
                    .or_else(|_| fs::metadata(&directory).and_then(|meta| meta.created()))
                    .unwrap_or(SystemTime::UNIX_EPOCH);
                let created_at: chrono::DateTime<chrono::Utc> = created.into();
                let size_bytes = walkdir::WalkDir::new(&directory)
                    .into_iter()
                    .flatten()
                    .filter_map(|item| item.metadata().ok())
                    .filter(|meta| meta.is_file())
                    .map(|meta| meta.len())
                    .sum();
                let note = fs::read_to_string(directory.join("note.txt")).ok();
                Some(ToolVersion {
                    version: version.clone(),
                    created_at: created_at.to_rfc3339(),
                    size_bytes,
                    is_active: version == active_version,
                    note,
                })
            })
            .collect();
        versions
            .sort_by(|left, right| version_key(&right.version).cmp(&version_key(&left.version)));
        versions
    }

    pub fn snapshot(
        tool_dir: &Path,
        version: &str,
        note: Option<&str>,
    ) -> Result<bool, BridgeError> {
        if !is_valid_version(version) {
            return Err(BridgeError::new(
                "VERSION_INVALID",
                format!("非法版本号 '{version}'"),
            ));
        }
        let target = Self::version_dir(tool_dir, version);
        if target.exists() {
            return Ok(false);
        }
        fs::create_dir_all(&target)
            .map_err(|error| BridgeError::new("VERSION_SNAPSHOT_FAILED", error.to_string()))?;
        for entry in Self::VERSIONED_ENTRIES {
            let source = tool_dir.join(entry);
            if source.is_file() {
                fs::copy(&source, target.join(entry)).map_err(|error| {
                    BridgeError::new("VERSION_SNAPSHOT_FAILED", error.to_string())
                })?;
            }
        }
        if let Some(note) = note.filter(|value| !value.trim().is_empty()) {
            let _ = fs::write(target.join("note.txt"), note);
        }
        Ok(true)
    }

    pub fn activate(
        tool_dir: &Path,
        current_version: &str,
        target_version: &str,
    ) -> Result<(), BridgeError> {
        if !is_valid_version(target_version) {
            return Err(BridgeError::new(
                "VERSION_INVALID",
                format!("非法版本号 '{target_version}'"),
            ));
        }
        let source = Self::version_dir(tool_dir, target_version);
        if !source.is_dir() {
            return Err(BridgeError::new(
                "VERSION_NOT_FOUND",
                format!("版本 {target_version} 不存在"),
            ));
        }
        if current_version != target_version {
            let _ = Self::snapshot(tool_dir, current_version, None);
        }
        for entry in Self::VERSIONED_ENTRIES {
            let from = source.join(entry);
            let to = tool_dir.join(entry);
            if from.is_file() {
                let temp = tool_dir.join(format!("{entry}.switch-tmp"));
                fs::copy(&from, &temp)
                    .and_then(|_| fs::rename(&temp, &to))
                    .map_err(|error| {
                        BridgeError::new("VERSION_SWITCH_FAILED", error.to_string())
                    })?;
            }
        }
        Ok(())
    }

    pub fn delete(
        tool_dir: &Path,
        version: &str,
        active_version: &str,
    ) -> Result<bool, BridgeError> {
        if version == active_version {
            return Err(BridgeError::new(
                "VERSION_ACTIVE",
                "不能删除正在运行的版本，请先切换到其他版本",
            ));
        }
        if !is_valid_version(version) {
            return Err(BridgeError::new(
                "VERSION_INVALID",
                format!("非法版本号 '{version}'"),
            ));
        }
        let target = Self::version_dir(tool_dir, version);
        if !target.is_dir() {
            return Ok(false);
        }
        fs::remove_dir_all(target)
            .map_err(|error| BridgeError::new("VERSION_DELETE_FAILED", error.to_string()))?;
        Ok(true)
    }
}

fn version_key(version: &str) -> (u64, u64, u64, &str) {
    let core = version.split('-').next().unwrap_or_default();
    let mut parts = core.split('.').map(|part| part.parse::<u64>().unwrap_or(0));
    (
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
        version
            .split_once('-')
            .map(|(_, suffix)| suffix)
            .unwrap_or(""),
    )
}

pub struct ToolRegistry {
    pub tools_dir: PathBuf,
    tools: RwLock<HashMap<String, LoadedTool>>,
}

impl ToolRegistry {
    pub fn new(tools_dir: PathBuf) -> Self {
        Self {
            tools_dir,
            tools: RwLock::new(HashMap::new()),
        }
    }

    pub fn rescan(&self) -> Vec<LoadedTool> {
        let mut loaded = Vec::new();
        let Ok(entries) = fs::read_dir(&self.tools_dir) else {
            self.tools.write().clear();
            return loaded;
        };
        for entry in entries.flatten() {
            if !entry.path().is_dir() {
                continue;
            }
            let directory_name = entry.file_name().to_string_lossy().into_owned();
            if directory_name.starts_with('.') || directory_name.is_empty() {
                continue;
            }
            let directory = entry.path();
            let manifest_path = directory.join("manifest.json");
            if !manifest_path.is_file() {
                crate::logging::warn(&format!("跳过 {directory_name}：缺少 manifest.json"));
                continue;
            }
            let Ok(text) = fs::read_to_string(&manifest_path) else {
                continue;
            };
            let manifest: ToolManifest = match serde_json::from_str(&text) {
                Ok(value) => value,
                Err(error) => {
                    crate::logging::warn(&format!("解析工具 {directory_name} 失败：{error}"));
                    continue;
                }
            };
            let errors = validate_manifest(&manifest, &directory_name);
            let last_modified_utc = fs::metadata(&manifest_path)
                .and_then(|meta| meta.modified())
                .map(|value| {
                    let datetime: chrono::DateTime<chrono::Utc> = value.into();
                    datetime.to_rfc3339()
                })
                .unwrap_or_default();
            let mut versions = ToolVersionStore::list(&directory, &manifest.version);
            if versions.is_empty() && is_valid_version(&manifest.version) {
                let _ = ToolVersionStore::snapshot(
                    &directory,
                    &manifest.version,
                    Some("初始版本（自动迁移）"),
                );
                versions = ToolVersionStore::list(&directory, &manifest.version);
            }
            loaded.push(LoadedTool {
                permissions: parse_permissions(&manifest.permissions),
                manifest,
                directory: directory.clone(),
                last_modified_utc,
                is_valid: errors.is_empty(),
                errors,
                versions,
            });
        }
        loaded.sort_by(|left, right| left.manifest.id.cmp(&right.manifest.id));
        let mut map = HashMap::new();
        for tool in &loaded {
            map.insert(tool.manifest.id.clone(), tool.clone());
        }
        *self.tools.write() = map;
        loaded
    }

    pub fn reload(&self, tool_id: &str) -> Option<LoadedTool> {
        self.rescan();
        self.find(tool_id)
    }

    pub fn all(&self) -> Vec<LoadedTool> {
        let mut values: Vec<LoadedTool> = self.tools.read().values().cloned().collect();
        values.sort_by(|left, right| left.manifest.id.cmp(&right.manifest.id));
        values
    }

    pub fn find(&self, tool_id: &str) -> Option<LoadedTool> {
        self.tools.read().get(tool_id).cloned()
    }

    pub fn contains(&self, tool_id: &str) -> bool {
        self.tools.read().contains_key(tool_id)
    }

    pub fn rename(&self, tool_id: &str, name: &str) -> Result<LoadedTool, BridgeError> {
        let tool = self
            .find(tool_id)
            .ok_or_else(|| BridgeError::new("TOOL_NOT_FOUND", format!("未找到工具 '{tool_id}'")))?;
        let name = name.trim();
        let mut manifest = tool.manifest.clone();
        manifest.name = name.to_string();
        let errors = validate_manifest(&manifest, &tool.manifest.id);
        if !errors.is_empty() {
            return Err(BridgeError::new(
                "MANIFEST_INVALID",
                format!("重命名后 manifest 校验失败：{}", errors.join("；")),
            ));
        }
        write_manifest(&tool.directory, &manifest)?;
        self.reload(tool_id).ok_or_else(|| {
            BridgeError::new(
                "TOOL_NOT_FOUND",
                format!("重命名后工具 '{tool_id}' 无法加载"),
            )
        })
    }

    pub fn switch_version(&self, tool_id: &str, version: &str) -> Result<LoadedTool, BridgeError> {
        let tool = self
            .find(tool_id)
            .ok_or_else(|| BridgeError::new("TOOL_NOT_FOUND", format!("未找到工具 '{tool_id}'")))?;
        ToolVersionStore::activate(&tool.directory, &tool.manifest.version, version)?;
        let snapshot_manifest =
            ToolVersionStore::version_dir(&tool.directory, version).join("manifest.json");
        if snapshot_manifest.is_file() {
            let temp = tool.directory.join("manifest.json.switch-tmp");
            fs::copy(&snapshot_manifest, &temp)
                .and_then(|_| fs::rename(&temp, tool.directory.join("manifest.json")))
                .map_err(|error| BridgeError::new("VERSION_SWITCH_FAILED", error.to_string()))?;
        }
        self.reload(tool_id).ok_or_else(|| {
            BridgeError::new("VERSION_SWITCH_FAILED", "切换后工具无法加载，已保留原文件")
        })
    }

    pub fn delete_version(&self, tool_id: &str, version: &str) -> Result<LoadedTool, BridgeError> {
        let tool = self
            .find(tool_id)
            .ok_or_else(|| BridgeError::new("TOOL_NOT_FOUND", format!("未找到工具 '{tool_id}'")))?;
        ToolVersionStore::delete(&tool.directory, version, &tool.manifest.version)?;
        Ok(self.reload(tool_id).unwrap_or(tool))
    }

    pub fn remove(&self, tool_id: &str) {
        self.tools.write().remove(tool_id);
    }

    /// 只在没有真实工具时释放内嵌示例（.versions 等内部目录不算）。
    pub fn ensure_samples(&self) -> Vec<String> {
        if !self.tools.read().is_empty() {
            return Vec::new();
        }
        if let Ok(entries) = fs::read_dir(&self.tools_dir) {
            let has_real_tool = entries.flatten().any(|entry| {
                entry.path().is_dir()
                    && !entry.file_name().to_string_lossy().starts_with('.')
                    && entry.path().join("manifest.json").is_file()
            });
            if has_real_tool {
                return Vec::new();
            }
        }
        let samples = ["json-formatter", "qrcode-generator"];
        let mut released = Vec::new();
        for sample in samples {
            let target = self.tools_dir.join(sample);
            if target.join("manifest.json").is_file() {
                continue;
            }
            let prefix = format!("samples/{sample}/");
            let mut written = 0;
            for file in crate::assets::Assets::iter() {
                let relative = file.as_ref();
                if !relative.starts_with(&prefix) {
                    continue;
                }
                let file_name = &relative[prefix.len()..];
                if file_name.is_empty() || file_name.contains("..") {
                    continue;
                }
                let Some(content) = crate::assets::Assets::get(relative) else {
                    continue;
                };
                let destination = target.join(file_name);
                if let Some(parent) = destination.parent() {
                    let _ = fs::create_dir_all(parent);
                }
                if fs::write(&destination, content.data.as_ref()).is_ok() {
                    written += 1;
                }
            }
            if written > 0 {
                released.push(sample.to_string());
            }
        }
        released
    }
}

pub fn write_manifest(directory: &Path, manifest: &ToolManifest) -> Result<(), BridgeError> {
    let json = serde_json::to_string_pretty(manifest)
        .map_err(|error| BridgeError::new("MANIFEST_WRITE_FAILED", error.to_string()))?;
    let temp = directory.join("manifest.json.rename-tmp");
    fs::write(&temp, json)
        .and_then(|_| fs::rename(&temp, directory.join("manifest.json")))
        .map_err(|error| BridgeError::new("MANIFEST_WRITE_FAILED", error.to_string()))
}

/// 与 .NET 版 BuildSearchKey 对齐的核心：名称/ID/描述 + 拼音首字母与全拼。
pub fn build_search_key(name: &str, id: &str, description: Option<&str>) -> String {
    use pinyin::ToPinyin;
    let lower_name = name.to_lowercase();
    let lower_id = id.to_lowercase();
    let lower_desc = description.unwrap_or_default().to_lowercase();
    let mut initials = String::new();
    let mut compact = String::new();
    let mut syllables = String::new();
    for ch in name.chars() {
        if let Some(pinyin) = ch.to_pinyin() {
            let plain = pinyin.plain();
            if let Some(first) = plain.chars().next() {
                initials.push(first);
            }
            compact.push_str(plain);
            syllables.push_str(plain);
            syllables.push(' ');
        } else {
            initials.push(ch);
            compact.push(ch);
            syllables.push(ch);
        }
    }
    format!(
        "{} {} {} {} {} {}",
        lower_name,
        lower_id,
        lower_desc,
        initials,
        compact,
        syllables.trim()
    )
}

#[allow(dead_code)]
pub fn manifest_from_value(value: Value) -> Result<ToolManifest, serde_json::Error> {
    serde_json::from_value(value)
}

#[cfg(test)]
mod tests {
    use super::build_search_key;

    #[test]
    fn search_key_contains_pinyin_initials_and_full_pinyin() {
        let key = build_search_key("图片压缩", "image-compressor", Some("本地压缩图片"));
        assert!(key.contains("tpys"), "缺少首字母：{key}");
        assert!(key.contains("tupian"), "缺少全拼：{key}");
    }
}
