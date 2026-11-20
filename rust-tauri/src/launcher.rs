use parking_lot::RwLock;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};

use crate::error::{BridgeError, BridgeResult};
use crate::tools::{build_search_key, is_valid_tool_id};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AppEntry {
    pub id: String,
    pub name: String,
    pub target: String,
    pub arguments: Option<String>,
    pub working_directory: Option<String>,
    pub category: Option<String>,
    pub glyph: Option<String>,
    pub created_at: String,
}

impl Default for AppEntry {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            target: String::new(),
            arguments: None,
            working_directory: None,
            category: None,
            glyph: None,
            created_at: String::new(),
        }
    }
}

#[derive(Clone, Debug)]
pub struct LoadedApp {
    pub entry: AppEntry,
    pub directory: PathBuf,
    pub is_valid: bool,
    pub errors: Vec<String>,
}

impl LoadedApp {
    pub fn id(&self) -> &str {
        &self.entry.id
    }

    pub fn name(&self) -> &str {
        if self.entry.name.trim().is_empty() {
            &self.entry.id
        } else {
            &self.entry.name
        }
    }

    pub fn target_exists(&self) -> bool {
        Path::new(&self.entry.target).exists()
    }
}

pub fn validate_app(entry: &AppEntry, directory_name: &str) -> Vec<String> {
    let mut errors = Vec::new();
    if !is_valid_tool_id(&entry.id) {
        errors.push("id 必须匹配小写字母/数字/短横线规则".into());
    } else if entry.id != directory_name {
        errors.push(format!(
            "id({}) 必须与目录名({}) 一致",
            entry.id, directory_name
        ));
    }
    if entry.name.trim().is_empty() || entry.name.trim().chars().count() > 60 {
        errors.push("name 必填且不超过 60 字符".into());
    }
    if entry.target.trim().is_empty() {
        errors.push("target 必填（可执行文件 / .app / .lnk / .desktop 的路径）".into());
    } else if entry.target.contains('\0') {
        errors.push("target 包含非法字符".into());
    }
    if entry
        .arguments
        .as_deref()
        .is_some_and(|value| value.chars().count() > 2000)
    {
        errors.push("arguments 不超过 2000 字符".into());
    }
    if entry
        .category
        .as_deref()
        .is_some_and(|value| value.chars().count() > 30)
    {
        errors.push("category 不超过 30 字符".into());
    }
    if entry
        .glyph
        .as_deref()
        .is_some_and(|value| value.chars().count() > 4)
    {
        errors.push("glyph 不超过 4 字符".into());
    }
    errors
}

pub struct AppRegistry {
    pub apps_dir: PathBuf,
    apps: RwLock<HashMap<String, LoadedApp>>,
}

impl AppRegistry {
    pub fn new(apps_dir: PathBuf) -> Self {
        Self {
            apps_dir,
            apps: RwLock::new(HashMap::new()),
        }
    }

    pub fn rescan(&self) -> Vec<LoadedApp> {
        let mut loaded = Vec::new();
        if let Ok(entries) = fs::read_dir(&self.apps_dir) {
            for entry in entries.flatten() {
                if !entry.path().is_dir() {
                    continue;
                }
                let name = entry.file_name().to_string_lossy().into_owned();
                if name.starts_with('.') || name.is_empty() {
                    continue;
                }
                let path = entry.path().join("app.json");
                let Ok(text) = fs::read_to_string(path) else {
                    continue;
                };
                let entry_value: AppEntry = match serde_json::from_str(&text) {
                    Ok(value) => value,
                    Err(error) => {
                        crate::logging::warn(&format!("解析启动项 {name} 失败：{error}"));
                        continue;
                    }
                };
                let errors = validate_app(&entry_value, &name);
                loaded.push(LoadedApp {
                    entry: entry_value,
                    directory: entry.path(),
                    is_valid: errors.is_empty(),
                    errors,
                });
            }
        }
        loaded.sort_by(|left, right| left.name().to_lowercase().cmp(&right.name().to_lowercase()));
        let mut map = HashMap::new();
        for app in &loaded {
            map.insert(app.id().to_string(), app.clone());
        }
        *self.apps.write() = map;
        loaded
    }

    pub fn all(&self) -> Vec<LoadedApp> {
        let mut values: Vec<LoadedApp> = self.apps.read().values().cloned().collect();
        values.sort_by(|left, right| left.name().to_lowercase().cmp(&right.name().to_lowercase()));
        values
    }

    pub fn find(&self, id: &str) -> Option<LoadedApp> {
        self.apps.read().get(id).cloned()
    }

    pub fn contains(&self, id: &str) -> bool {
        self.apps.read().contains_key(id)
    }

    pub fn reload(&self, id: &str) -> Option<LoadedApp> {
        self.rescan();
        self.find(id)
    }

    pub fn remove(&self, id: &str) {
        self.apps.write().remove(id);
    }
}

pub struct AppStore {
    registry: std::sync::Arc<AppRegistry>,
}

impl AppStore {
    pub fn new(registry: std::sync::Arc<AppRegistry>) -> Self {
        Self { registry }
    }

    fn generate_id(&self, name: &str) -> String {
        let mut slug = String::new();
        let mut last_was_dash = false;
        for ch in name.to_lowercase().chars() {
            if ch.is_ascii_lowercase() || ch.is_ascii_digit() {
                slug.push(ch);
                last_was_dash = false;
            } else if !last_was_dash && !slug.is_empty() {
                slug.push('-');
                last_was_dash = true;
            }
        }
        let mut slug = slug.trim_matches('-').to_string();
        if slug.len() > 63 {
            slug.truncate(63);
            slug = slug.trim_matches('-').to_string();
        }
        if slug.is_empty() {
            let hash = Sha256::digest(name.as_bytes());
            slug = format!("app-{}", hex_prefix(&hash, 8));
        }
        self.ensure_unique(slug)
    }

    fn ensure_unique(&self, candidate: String) -> String {
        if !self.registry.contains(&candidate) {
            return candidate;
        }
        for suffix in 2..1000 {
            let next = format!("{candidate}-{suffix}");
            if !self.registry.contains(&next) {
                return next;
            }
        }
        let hash = Sha256::digest(
            format!(
                "{candidate}{}",
                chrono::Utc::now().timestamp_nanos_opt().unwrap_or(0)
            )
            .as_bytes(),
        );
        format!("{candidate}-{}", hex_prefix(&hash, 6))
    }

    pub fn add(
        &self,
        name: &str,
        target: &str,
        arguments: Option<&str>,
        working_directory: Option<&str>,
        category: Option<&str>,
        glyph: Option<&str>,
    ) -> BridgeResult<LoadedApp> {
        let name = name.trim();
        if name.is_empty() {
            return Err(BridgeError::new("APP_NAME_REQUIRED", "请填写程序名称"));
        }
        if name.chars().count() > 60 {
            return Err(BridgeError::new(
                "APP_NAME_TOO_LONG",
                "程序名称不超过 60 字符",
            ));
        }
        let target = target.trim();
        if target.is_empty() {
            return Err(BridgeError::new("APP_TARGET_REQUIRED", "请填写程序路径"));
        }
        if target.contains('\0') {
            return Err(BridgeError::new(
                "APP_TARGET_INVALID",
                "程序路径包含非法字符",
            ));
        }
        let entry = AppEntry {
            id: self.generate_id(name),
            name: name.to_string(),
            target: target.to_string(),
            arguments: normalize_opt(arguments),
            working_directory: normalize_opt(working_directory),
            category: normalize_opt(category),
            glyph: normalize_opt(glyph),
            created_at: chrono::Local::now().to_rfc3339(),
        };
        let errors = validate_app(&entry, &entry.id);
        if !errors.is_empty() {
            return Err(BridgeError::new("APP_INVALID", errors.join("；")));
        }
        let directory = self.registry.apps_dir.join(&entry.id);
        fs::create_dir_all(&directory)?;
        let json = serde_json::to_string_pretty(&entry)?;
        fs::write(directory.join("app.json"), json)?;
        self.registry
            .reload(&entry.id)
            .ok_or_else(|| BridgeError::new("APP_INVALID", "启动项未通过运行时校验"))
    }

    pub fn delete(&self, app_id: &str) -> BridgeResult<bool> {
        let Some(app) = self.registry.find(app_id) else {
            return Ok(false);
        };
        fs::remove_dir_all(&app.directory)
            .map_err(|error| BridgeError::new("APP_DELETE_FAILED", format!("删除失败：{error}")))?;
        self.registry.remove(app_id);
        Ok(true)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct WebToolEntry {
    pub id: String,
    pub name: String,
    pub url: String,
    pub category: Option<String>,
    pub glyph: Option<String>,
    pub created_at: String,
    pub description: Option<String>,
}

impl Default for WebToolEntry {
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            url: String::new(),
            category: None,
            glyph: None,
            created_at: String::new(),
            description: None,
        }
    }
}

#[derive(Clone, Debug)]
pub struct LoadedWebTool {
    pub entry: WebToolEntry,
    pub directory: PathBuf,
    pub is_valid: bool,
}

impl LoadedWebTool {
    pub fn id(&self) -> &str {
        &self.entry.id
    }
    pub fn name(&self) -> &str {
        if self.entry.name.trim().is_empty() {
            &self.entry.id
        } else {
            &self.entry.name
        }
    }
}

pub fn validate_web(entry: &WebToolEntry, directory_name: &str) -> Vec<String> {
    let mut errors = Vec::new();
    if !is_valid_tool_id(&entry.id) {
        errors.push("id 必须匹配小写字母/数字/短横线规则".into());
    } else if entry.id != directory_name {
        errors.push(format!(
            "id({}) 必须与目录名({}) 一致",
            entry.id, directory_name
        ));
    }
    if entry.name.trim().is_empty() || entry.name.trim().chars().count() > 60 {
        errors.push("name 必填且不超过 60 字符".into());
    }
    if entry
        .description
        .as_deref()
        .is_some_and(|value| value.chars().count() > 300)
    {
        errors.push("description 不超过 300 字符".into());
    }
    let url = entry.url.trim();
    if url.is_empty() {
        errors.push("url 必填（http:// 或 https:// 开头的网址）".into());
    } else if url.chars().count() > 2048 {
        errors.push("url 不超过 2048 字符".into());
    } else if url.chars().any(|ch| ch.is_control()) {
        errors.push("url 包含非法控制字符".into());
    } else if !(url.starts_with("http://") || url.starts_with("https://")) {
        errors.push("url 必须是 http:// 或 https:// 开头的合法网址".into());
    }
    if entry
        .category
        .as_deref()
        .is_some_and(|value| value.chars().count() > 30)
    {
        errors.push("category 不超过 30 字符".into());
    }
    if entry
        .glyph
        .as_deref()
        .is_some_and(|value| value.chars().count() > 4)
    {
        errors.push("glyph 不超过 4 字符".into());
    }
    errors
}

pub struct WebToolRegistry {
    pub web_tools_dir: PathBuf,
    tools: RwLock<HashMap<String, LoadedWebTool>>,
}

impl WebToolRegistry {
    pub fn new(directory: PathBuf) -> Self {
        Self {
            web_tools_dir: directory,
            tools: RwLock::new(HashMap::new()),
        }
    }

    pub fn rescan(&self) -> Vec<LoadedWebTool> {
        let mut loaded = Vec::new();
        if let Ok(entries) = fs::read_dir(&self.web_tools_dir) {
            for entry in entries.flatten() {
                if !entry.path().is_dir() {
                    continue;
                }
                let directory_name = entry.file_name().to_string_lossy().into_owned();
                if directory_name.starts_with('.') || directory_name.is_empty() {
                    continue;
                }
                let Ok(text) = fs::read_to_string(entry.path().join("web.json")) else {
                    continue;
                };
                let web_entry: WebToolEntry = match serde_json::from_str(&text) {
                    Ok(value) => value,
                    Err(error) => {
                        crate::logging::warn(&format!(
                            "解析在线工具 {directory_name} 失败：{error}"
                        ));
                        continue;
                    }
                };
                let errors = validate_web(&web_entry, &directory_name);
                loaded.push(LoadedWebTool {
                    entry: web_entry,
                    directory: entry.path(),
                    is_valid: errors.is_empty(),
                });
            }
        }
        loaded.sort_by(|left, right| left.name().to_lowercase().cmp(&right.name().to_lowercase()));
        let mut map = HashMap::new();
        for tool in &loaded {
            map.insert(tool.id().to_string(), tool.clone());
        }
        *self.tools.write() = map;
        loaded
    }

    pub fn all(&self) -> Vec<LoadedWebTool> {
        let mut values: Vec<LoadedWebTool> = self.tools.read().values().cloned().collect();
        values.sort_by(|left, right| left.name().to_lowercase().cmp(&right.name().to_lowercase()));
        values
    }

    pub fn find(&self, id: &str) -> Option<LoadedWebTool> {
        self.tools.read().get(id).cloned()
    }

    pub fn contains(&self, id: &str) -> bool {
        self.tools.read().contains_key(id)
    }

    pub fn reload(&self, id: &str) -> Option<LoadedWebTool> {
        self.rescan();
        self.find(id)
    }

    pub fn remove(&self, id: &str) {
        self.tools.write().remove(id);
    }
}

pub struct WebToolStore {
    registry: std::sync::Arc<WebToolRegistry>,
}

impl WebToolStore {
    pub fn new(registry: std::sync::Arc<WebToolRegistry>) -> Self {
        Self { registry }
    }

    fn generate_id(&self, name: &str) -> String {
        let mut slug = String::new();
        let mut last_was_dash = false;
        for ch in name.to_lowercase().chars() {
            if ch.is_ascii_lowercase() || ch.is_ascii_digit() {
                slug.push(ch);
                last_was_dash = false;
            } else if !last_was_dash && !slug.is_empty() {
                slug.push('-');
                last_was_dash = true;
            }
        }
        let mut slug = slug.trim_matches('-').to_string();
        if slug.len() > 63 {
            slug.truncate(63);
            slug = slug.trim_matches('-').to_string();
        }
        if slug.is_empty() {
            let hash = Sha256::digest(name.as_bytes());
            slug = format!("web-{}", hex_prefix(&hash, 8));
        }
        if !self.registry.contains(&slug) {
            return slug;
        }
        for suffix in 2..1000 {
            let next = format!("{slug}-{suffix}");
            if !self.registry.contains(&next) {
                return next;
            }
        }
        let hash = Sha256::digest(format!("{slug}{}", chrono::Utc::now().timestamp()).as_bytes());
        format!("{slug}-{}", hex_prefix(&hash, 6))
    }

    pub fn add(
        &self,
        name: &str,
        url: &str,
        category: Option<&str>,
        glyph: Option<&str>,
        description: Option<&str>,
    ) -> BridgeResult<LoadedWebTool> {
        let name = name.trim();
        if name.is_empty() {
            return Err(BridgeError::new("WEB_NAME_REQUIRED", "请填写工具名称"));
        }
        if name.chars().count() > 60 {
            return Err(BridgeError::new(
                "WEB_NAME_TOO_LONG",
                "工具名称不超过 60 字符",
            ));
        }
        let url = url.trim();
        if url.is_empty() {
            return Err(BridgeError::new("WEB_URL_REQUIRED", "请填写工具网址"));
        }
        if url.contains('\0') || url.chars().any(|ch| ch.is_control()) {
            return Err(BridgeError::new("WEB_URL_INVALID", "网址包含非法字符"));
        }
        let entry = WebToolEntry {
            id: self.generate_id(name),
            name: name.to_string(),
            url: url.to_string(),
            category: normalize_opt(category),
            glyph: normalize_opt(glyph),
            created_at: chrono::Local::now().to_rfc3339(),
            description: normalize_opt(description),
        };
        let errors = validate_web(&entry, &entry.id);
        if !errors.is_empty() {
            return Err(BridgeError::new("WEB_INVALID", errors.join("；")));
        }
        let directory = self.registry.web_tools_dir.join(&entry.id);
        fs::create_dir_all(&directory)?;
        fs::write(
            directory.join("web.json"),
            serde_json::to_string_pretty(&entry)?,
        )?;
        self.registry
            .reload(&entry.id)
            .ok_or_else(|| BridgeError::new("WEB_INVALID", "在线工具未通过校验"))
    }

    pub fn rename(&self, web_id: &str, new_name: &str) -> BridgeResult<LoadedWebTool> {
        let tool = self.registry.find(web_id).ok_or_else(|| {
            BridgeError::new("WEB_NOT_FOUND", format!("未找到在线工具 '{web_id}'"))
        })?;
        let mut entry = tool.entry.clone();
        entry.name = new_name.trim().to_string();
        let errors = validate_web(&entry, &tool.entry.id);
        if !errors.is_empty() {
            return Err(BridgeError::new(
                "WEB_INVALID",
                format!("重命名后校验失败：{}", errors.join("；")),
            ));
        }
        let path = tool.directory.join("web.json");
        let temp = tool.directory.join("web.json.rename-tmp");
        fs::write(&temp, serde_json::to_string_pretty(&entry)?)
            .and_then(|_| fs::rename(&temp, &path))
            .map_err(|error| {
                BridgeError::new("WEB_RENAME_FAILED", format!("重命名失败：{error}"))
            })?;
        self.registry
            .reload(web_id)
            .ok_or_else(|| BridgeError::new("WEB_RENAME_FAILED", "重命名后在线工具无法加载"))
    }

    pub fn delete(&self, web_id: &str) -> BridgeResult<bool> {
        let Some(tool) = self.registry.find(web_id) else {
            return Ok(false);
        };
        fs::remove_dir_all(&tool.directory)
            .map_err(|error| BridgeError::new("WEB_DELETE_FAILED", format!("删除失败：{error}")))?;
        self.registry.remove(web_id);
        Ok(true)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct UsageEntry {
    pub id: String,
    pub count: i32,
    pub last_used_utc: String,
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct UsageFile {
    schema_version: u32,
    entries: Vec<UsageEntry>,
}

pub struct UsageStore {
    path: PathBuf,
    entries: RwLock<HashMap<String, UsageEntry>>,
}

impl UsageStore {
    pub fn new(path: PathBuf) -> Self {
        let store = Self {
            path,
            entries: RwLock::new(HashMap::new()),
        };
        store.load();
        store
    }

    fn load(&self) {
        let Ok(text) = fs::read_to_string(&self.path) else {
            return;
        };
        let Ok(file) = serde_json::from_str::<UsageFile>(&text) else {
            crate::logging::warn("读取使用频次失败（视为空）");
            return;
        };
        let mut map = HashMap::new();
        for entry in file.entries {
            if !entry.id.is_empty() && entry.count > 0 {
                map.insert(entry.id.clone(), entry);
            }
        }
        *self.entries.write() = map;
    }

    fn persist(&self) {
        let entries: Vec<UsageEntry> = self.entries.read().values().cloned().collect();
        let file = UsageFile {
            schema_version: 1,
            entries,
        };
        let Ok(json) = serde_json::to_string(&file) else {
            return;
        };
        let temp = self.path.with_extension("json.tmp");
        if let Some(parent) = self.path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if fs::write(&temp, json)
            .and_then(|_| fs::rename(&temp, &self.path))
            .is_err()
        {
            crate::logging::warn("保存使用频次失败");
        }
    }

    pub fn get(&self, id: &str) -> Option<UsageEntry> {
        self.entries.read().get(id).cloned()
    }

    pub fn record(&self, id: &str) {
        if id.trim().is_empty() {
            return;
        }
        {
            let mut entries = self.entries.write();
            let count = entries.get(id).map(|entry| entry.count + 1).unwrap_or(1);
            entries.insert(
                id.to_string(),
                UsageEntry {
                    id: id.to_string(),
                    count,
                    last_used_utc: chrono::Utc::now().to_rfc3339(),
                },
            );
        }
        self.persist();
    }

    pub fn ensure_seeded<I>(&self, ids: I)
    where
        I: IntoIterator<Item = String>,
    {
        let mut added = false;
        {
            let mut entries = self.entries.write();
            for id in ids {
                if id.trim().is_empty() || entries.contains_key(&id) {
                    continue;
                }
                entries.insert(
                    id.clone(),
                    UsageEntry {
                        id,
                        count: 1,
                        last_used_utc: String::new(),
                    },
                );
                added = true;
            }
        }
        if added {
            self.persist();
        }
    }
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct HiddenProgramFile {
    ids: Vec<String>,
}

pub struct HiddenProgramStore {
    path: PathBuf,
    ids: RwLock<HashSet<String>>,
}

impl HiddenProgramStore {
    pub fn new(path: PathBuf) -> Self {
        let ids = fs::read_to_string(&path)
            .ok()
            .and_then(|text| serde_json::from_str::<HiddenProgramFile>(&text).ok())
            .map(|file| file.ids.into_iter().collect())
            .unwrap_or_default();
        Self {
            path,
            ids: RwLock::new(ids),
        }
    }

    pub fn is_hidden(&self, id: &str) -> bool {
        self.ids.read().contains(id)
    }

    pub fn hide(&self, id: &str) -> bool {
        let changed = self.ids.write().insert(id.to_string());
        if changed {
            self.save();
        }
        changed
    }

    pub fn unhide(&self, id: &str) -> bool {
        let changed = self.ids.write().remove(id);
        if changed {
            self.save();
        }
        changed
    }

    fn save(&self) {
        let mut ids: Vec<String> = self.ids.read().iter().cloned().collect();
        ids.sort();
        let json = serde_json::to_string(&HiddenProgramFile { ids }).unwrap_or_default();
        let _ = fs::write(&self.path, json);
    }
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct PinnedItemFile {
    keys: Vec<String>,
}

pub struct PinnedItemStore {
    path: PathBuf,
    keys: RwLock<HashSet<String>>,
}

impl PinnedItemStore {
    pub fn new(path: PathBuf) -> Self {
        let keys = fs::read_to_string(&path)
            .ok()
            .and_then(|text| serde_json::from_str::<PinnedItemFile>(&text).ok())
            .map(|file| file.keys.into_iter().collect())
            .unwrap_or_default();
        Self {
            path,
            keys: RwLock::new(keys),
        }
    }

    pub fn build_key(kind: &str, id: &str) -> String {
        format!("{kind}:{id}")
    }

    pub fn is_pinned(&self, kind: &str, id: &str) -> bool {
        self.keys.read().contains(&Self::build_key(kind, id))
    }

    pub fn set_pinned(&self, kind: &str, id: &str, pinned: bool) -> bool {
        let key = Self::build_key(kind, id);
        let changed = if pinned {
            self.keys.write().insert(key)
        } else {
            self.keys.write().remove(&key)
        };
        if changed {
            self.save();
        }
        changed
    }

    pub fn forget(&self, kind: &str, id: &str) {
        if self.keys.write().remove(&Self::build_key(kind, id)) {
            self.save();
        }
    }

    fn save(&self) {
        let mut keys: Vec<String> = self.keys.read().iter().cloned().collect();
        keys.sort();
        let json = serde_json::to_string(&PinnedItemFile { keys }).unwrap_or_default();
        let _ = fs::write(&self.path, json);
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct IndexEntry {
    pub id: String,
    pub name: String,
    pub target: String,
    pub source: String,
    pub category: Option<String>,
}

#[derive(Clone, Debug, Default)]
pub struct ProgramIndex {
    pub entries: Vec<IndexEntry>,
    pub last_indexed_utc: Option<String>,
    pub indexing: bool,
}

impl ProgramIndex {
    pub fn load(path: &Path) -> Self {
        let Ok(text) = fs::read_to_string(path) else {
            return Self::default();
        };
        #[derive(Deserialize, Default)]
        #[serde(rename_all = "camelCase", default)]
        struct ProgramIndexFile {
            indexed_at_utc: String,
            entries: Vec<IndexEntry>,
        }
        match serde_json::from_str::<ProgramIndexFile>(&text) {
            Ok(file) => Self {
                entries: file.entries,
                last_indexed_utc: (!file.indexed_at_utc.is_empty()).then_some(file.indexed_at_utc),
                indexing: false,
            },
            Err(_) => Self::default(),
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherItemView {
    pub id: String,
    pub kind: String,
    pub name: String,
    pub version: Option<String>,
    pub icon_url: Option<String>,
    pub search_key: String,
    pub is_valid: bool,
    pub target_exists: bool,
    pub use_count: i32,
    pub category: Option<String>,
    pub glyph: Option<String>,
    pub target: Option<String>,
    pub description: Option<String>,
    pub pinned: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherIndexStatus {
    pub indexing: bool,
    pub last_indexed_utc: Option<String>,
    pub program_count: i32,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherListView {
    pub items: Vec<LauncherItemView>,
    pub index: LauncherIndexStatus,
}

pub struct LauncherSources<'a> {
    pub tools: &'a crate::tools::ToolRegistry,
    pub apps: &'a AppRegistry,
    pub web_tools: &'a WebToolRegistry,
    pub program_index: &'a ProgramIndex,
    pub usage: &'a UsageStore,
    pub hidden: &'a HiddenProgramStore,
    pub pinned: &'a PinnedItemStore,
    pub icon_cache_dir: &'a std::path::Path,
}

pub fn build_launcher_list(sources: LauncherSources<'_>) -> LauncherListView {
    let mut rows: Vec<(LauncherItemView, String)> = Vec::new();

    for tool in sources.tools.all() {
        let used = sources.usage.get(&tool.manifest.id);
        rows.push((
            LauncherItemView {
                id: tool.manifest.id.clone(),
                kind: "tool".into(),
                name: tool.name().to_string(),
                version: Some(tool.manifest.version.clone()),
                icon_url: None,
                search_key: build_search_key(
                    tool.name(),
                    &tool.manifest.id,
                    tool.manifest.description.as_deref(),
                ),
                is_valid: tool.is_valid,
                target_exists: true,
                use_count: used.as_ref().map(|entry| entry.count).unwrap_or(0),
                category: tool.manifest.category.clone(),
                glyph: tool.manifest.glyph.clone(),
                target: None,
                description: tool.manifest.description.clone(),
                pinned: sources.pinned.is_pinned("tool", &tool.manifest.id),
            },
            used.map(|entry| entry.last_used_utc).unwrap_or_default(),
        ));
    }

    let mut manual_targets = HashSet::new();
    for app in sources.apps.all() {
        if !app.entry.target.trim().is_empty() {
            manual_targets.insert(app.entry.target.trim().to_string());
        }
        let icon_path = app.directory.join("icon.png");
        crate::app_icons::ensure_icon_at(&app.entry.target, &icon_path);
        let icon_url = icon_path
            .is_file()
            .then(|| format!("/__host__/app-icon/{}", urlencoding::encode(app.id())));
        let used = sources.usage.get(app.id());
        rows.push((
            LauncherItemView {
                id: app.entry.id.clone(),
                kind: "app".into(),
                name: app.name().to_string(),
                version: None,
                icon_url,
                search_key: build_search_key(app.name(), app.id(), Some(app.entry.target.as_str())),
                is_valid: app.is_valid,
                target_exists: app.target_exists(),
                use_count: used.as_ref().map(|entry| entry.count).unwrap_or(0),
                category: app.entry.category.clone(),
                glyph: app.entry.glyph.clone(),
                target: Some(app.entry.target.clone()),
                description: None,
                pinned: sources.pinned.is_pinned("app", app.id()),
            },
            used.map(|entry| entry.last_used_utc).unwrap_or_default(),
        ));
    }

    for entry in &sources.program_index.entries {
        if manual_targets.contains(&entry.target) || sources.hidden.is_hidden(&entry.id) {
            continue;
        }
        let icon_path = sources.icon_cache_dir.join(format!("{}.png", entry.id));
        let icon_url = icon_path
            .is_file()
            .then(|| format!("/__host__/app-icon/{}", urlencoding::encode(&entry.id)));
        let used = sources.usage.get(&entry.id);
        rows.push((
            LauncherItemView {
                id: entry.id.clone(),
                kind: "program".into(),
                name: entry.name.clone(),
                version: None,
                icon_url,
                search_key: build_search_key(&entry.name, &entry.id, Some(entry.target.as_str())),
                is_valid: true,
                target_exists: Path::new(&entry.target).exists(),
                use_count: used.as_ref().map(|entry| entry.count).unwrap_or(0),
                category: entry.category.clone(),
                glyph: None,
                target: Some(entry.target.clone()),
                description: None,
                pinned: sources.pinned.is_pinned("program", &entry.id),
            },
            used.map(|entry| entry.last_used_utc).unwrap_or_default(),
        ));
    }

    for web in sources.web_tools.all() {
        let used = sources.usage.get(web.id());
        rows.push((
            LauncherItemView {
                id: web.entry.id.clone(),
                kind: "web".into(),
                name: web.name().to_string(),
                version: None,
                icon_url: None,
                search_key: build_search_key(
                    web.name(),
                    web.id(),
                    web.entry
                        .description
                        .as_deref()
                        .or(Some(web.entry.url.as_str())),
                ),
                is_valid: web.is_valid,
                target_exists: true,
                use_count: used.as_ref().map(|entry| entry.count).unwrap_or(0),
                category: web.entry.category.clone(),
                glyph: web.entry.glyph.clone(),
                target: Some(web.entry.url.clone()),
                description: web.entry.description.clone(),
                pinned: sources.pinned.is_pinned("web", web.id()),
            },
            used.map(|entry| entry.last_used_utc).unwrap_or_default(),
        ));
    }

    rows.sort_by(|left, right| {
        right
            .0
            .pinned
            .cmp(&left.0.pinned)
            .then(right.0.use_count.cmp(&left.0.use_count))
            .then(right.1.cmp(&left.1))
            .then(left.0.name.to_lowercase().cmp(&right.0.name.to_lowercase()))
    });

    LauncherListView {
        items: rows.into_iter().map(|row| row.0).collect(),
        index: LauncherIndexStatus {
            indexing: sources.program_index.indexing,
            last_indexed_utc: sources.program_index.last_indexed_utc.clone(),
            program_count: sources.program_index.entries.len() as i32,
        },
    }
}

pub fn launch_app(app: &LoadedApp) -> BridgeResult<Option<u32>> {
    if !app.is_valid {
        return Err(BridgeError::new(
            "APP_INVALID",
            format!("启动项配置无效：{}", app.errors.join("；")),
        ));
    }
    let target = app.entry.target.trim();
    if !app.target_exists() {
        return Err(BridgeError::new(
            "APP_TARGET_NOT_FOUND",
            format!("找不到目标程序：{target}"),
        ));
    }
    let arguments = app.entry.arguments.as_deref().unwrap_or("").trim();
    let working_directory = app.entry.working_directory.as_deref().unwrap_or("").trim();

    #[cfg(windows)]
    {
        if !arguments.is_empty() {
            let mut command = std::process::Command::new(target);
            for argument in split_arguments(arguments) {
                command.arg(argument);
            }
            if !working_directory.is_empty() && Path::new(working_directory).is_dir() {
                command.current_dir(working_directory);
            }
            let child = command.spawn().map_err(|error| {
                BridgeError::new("APP_LAUNCH_FAILED", format!("启动失败：{error}"))
            })?;
            return Ok(Some(child.id()));
        }
        // 无参数时交给 ShellExecute，支持 .lnk / 文档关联
        open::that(target)
            .map_err(|error| BridgeError::new("APP_LAUNCH_FAILED", format!("启动失败：{error}")))?;
        return Ok(None);
    }

    #[cfg(not(windows))]
    {
        let _ = (target, arguments, working_directory);
        open::that(target)
            .map_err(|error| BridgeError::new("APP_LAUNCH_FAILED", format!("启动失败：{error}")))?;
        Ok(None)
    }
}

pub fn reveal(target: &str) -> BridgeResult<()> {
    let path = Path::new(target);
    #[cfg(windows)]
    {
        let result = if path.is_dir() {
            std::process::Command::new("explorer.exe").arg(path).spawn()
        } else {
            std::process::Command::new("explorer.exe")
                .arg(format!("/select,{}", path.display()))
                .spawn()
        };
        result.map_err(|error| {
            BridgeError::new("REVEAL_FAILED", format!("打开所在位置失败：{error}"))
        })?;
        return Ok(());
    }
    #[cfg(target_os = "macos")]
    {
        std::process::Command::new("open")
            .args(["-R", target])
            .spawn()
            .map_err(|error| {
                BridgeError::new("REVEAL_FAILED", format!("打开所在位置失败：{error}"))
            })?;
        return Ok(());
    }
    #[cfg(all(not(windows), not(target_os = "macos")))]
    {
        let directory = if path.is_dir() {
            path.to_path_buf()
        } else {
            path.parent().map(Path::to_path_buf).unwrap_or_default()
        };
        open::that(directory).map_err(|error| {
            BridgeError::new("REVEAL_FAILED", format!("打开所在位置失败：{error}"))
        })?;
        Ok(())
    }
}

fn normalize_opt(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn hex_prefix(bytes: &[u8], length: usize) -> String {
    bytes
        .iter()
        .flat_map(|byte| format!("{byte:02x}").chars().collect::<Vec<_>>())
        .take(length)
        .collect()
}

/// 简单命令行拆分：支持双引号包裹，够 .lnk 快捷方式参数使用。
fn split_arguments(arguments: &str) -> Vec<String> {
    let mut result = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    for ch in arguments.chars() {
        match ch {
            '"' => quoted = !quoted,
            ' ' | '\t' if !quoted => {
                if !current.is_empty() {
                    result.push(std::mem::take(&mut current));
                }
            }
            _ => current.push(ch),
        }
    }
    if !current.is_empty() {
        result.push(current);
    }
    result
}
