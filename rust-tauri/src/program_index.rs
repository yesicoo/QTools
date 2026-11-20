use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use tauri::Emitter;

use crate::launcher::IndexEntry;
use crate::state::AppState;

const MAX_RESULTS: usize = 400;

#[derive(Clone, Debug)]
pub struct DiscoveredProgram {
    pub name: String,
    pub target: String,
    pub source: String,
    pub category: Option<String>,
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct ProgramIndexFile {
    schema_version: u32,
    indexed_at_utc: String,
    entries: Vec<IndexEntry>,
}

/// 本机程序索引：启动即加载旧索引，后台扫描完成后临时文件 + 原子 rename 覆盖。
pub fn discover() -> Vec<DiscoveredProgram> {
    let mut programs = Vec::new();
    #[cfg(windows)]
    {
        if let Some(program_data) = std::env::var_os("ProgramData") {
            let root = PathBuf::from(program_data)
                .join("Microsoft")
                .join("Windows")
                .join("Start Menu")
                .join("Programs");
            collect_lnk(&root, &mut programs, "开始菜单", 3);
        }
        if let Some(app_data) = std::env::var_os("APPDATA") {
            let root = PathBuf::from(app_data)
                .join("Microsoft")
                .join("Windows")
                .join("Start Menu")
                .join("Programs");
            collect_lnk(&root, &mut programs, "开始菜单", 3);
        }
        if let Some(desktop) = std::env::var_os("USERPROFILE") {
            let root = PathBuf::from(desktop).join("Desktop");
            collect_lnk(&root, &mut programs, "桌面", 1);
        }
    }
    let mut programs = dedupe(programs);
    programs.retain(|program| !program.name.trim().is_empty() && !is_junk_name(&program.name));
    programs.sort_by(|left, right| left.name.to_lowercase().cmp(&right.name.to_lowercase()));
    programs.truncate(MAX_RESULTS);
    programs
}

pub fn start_background_refresh(app: tauri::AppHandle, state: Arc<AppState>) {
    std::thread::Builder::new()
        .name("qtools-program-index".into())
        .spawn(move || {
            {
                let mut index = state.program_index.write();
                if index.indexing {
                    return;
                }
                index.indexing = true;
            }
            let entries = std::panic::catch_unwind(|| scan()).unwrap_or_else(|_| {
                crate::logging::warn("程序索引扫描异常，保留旧索引");
                Vec::new()
            });
            #[cfg(windows)]
            for entry in &entries {
                let path = state
                    .paths
                    .icon_cache_dir()
                    .join(format!("{}.png", entry.id));
                crate::app_icons::ensure_icon_at(&entry.target, &path);
            }
            if !entries.is_empty() {
                let timestamp = chrono::Utc::now().to_rfc3339();
                let file = ProgramIndexFile {
                    schema_version: 1,
                    indexed_at_utc: timestamp.clone(),
                    entries: entries.clone(),
                };
                if let Ok(json) = serde_json::to_string(&file) {
                    let path = state.paths.program_index_file();
                    if let Some(parent) = path.parent() {
                        let _ = fs::create_dir_all(parent);
                    }
                    let temp = path.with_extension("json.tmp");
                    if fs::write(&temp, json)
                        .and_then(|_| fs::rename(&temp, &path))
                        .is_ok()
                    {
                        let mut index = state.program_index.write();
                        index.entries = entries;
                        index.last_indexed_utc = Some(timestamp);
                    }
                }
            }
            {
                let mut index = state.program_index.write();
                index.indexing = false;
            }
            let _ = app.emit("launcher-changed", ());
            crate::logging::info("程序索引完成，列表已刷新");
        })
        .ok();
}

pub fn scan() -> Vec<IndexEntry> {
    let mut programs = Vec::new();
    #[cfg(windows)]
    {
        if let Some(program_data) = std::env::var_os("ProgramData") {
            let root = PathBuf::from(program_data)
                .join("Microsoft")
                .join("Windows")
                .join("Start Menu")
                .join("Programs");
            collect_lnk(&root, &mut programs, "开始菜单", 3);
        }
        if let Some(app_data) = std::env::var_os("APPDATA") {
            let root = PathBuf::from(app_data)
                .join("Microsoft")
                .join("Windows")
                .join("Start Menu")
                .join("Programs");
            collect_lnk(&root, &mut programs, "开始菜单", 3);
        }
        if let Some(desktop) = std::env::var_os("USERPROFILE") {
            let root = PathBuf::from(desktop).join("Desktop");
            collect_lnk(&root, &mut programs, "桌面", 1);
        }
    }
    #[cfg(target_os = "macos")]
    {
        collect_mac("/Applications", &mut programs);
        if let Some(home) = dirs::home_dir() {
            collect_mac(&home.join("Applications").to_string_lossy(), &mut programs);
        }
    }
    #[cfg(all(not(windows), not(target_os = "macos")))]
    {
        collect_linux("/usr/share/applications", &mut programs);
        collect_linux("/var/lib/snapd/desktop/applications", &mut programs);
        if let Some(home) = dirs::home_dir() {
            collect_linux(
                &home.join(".local/share/applications").to_string_lossy(),
                &mut programs,
            );
        }
    }

    let deduped = dedupe(programs);
    let mut entries = Vec::new();
    let mut seen = HashSet::new();
    for program in deduped {
        if program.name.trim().is_empty() || is_junk_name(&program.name) {
            continue;
        }
        let target = program.target.trim();
        if target.is_empty() || !seen.insert(target.to_ascii_lowercase()) {
            continue;
        }
        entries.push(IndexEntry {
            id: build_id(target),
            name: program.name.trim().to_string(),
            target: target.to_string(),
            source: program.source,
            category: program.category,
        });
    }
    entries.sort_by(|left, right| left.name.to_lowercase().cmp(&right.name.to_lowercase()));
    entries.truncate(MAX_RESULTS);
    entries
}

#[cfg(windows)]
fn collect_lnk(root: &Path, result: &mut Vec<DiscoveredProgram>, source: &str, max_depth: usize) {
    if !root.is_dir() {
        return;
    }
    let mut stack = vec![(root.to_path_buf(), 0usize)];
    while let Some((directory, depth)) = stack.pop() {
        let Ok(entries) = fs::read_dir(&directory) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                if depth < max_depth {
                    stack.push((path, depth + 1));
                }
                continue;
            }
            let extension = path
                .extension()
                .map(|value| value.to_string_lossy().to_ascii_lowercase())
                .unwrap_or_default();
            if extension != "lnk" && extension != "exe" {
                continue;
            }
            let name = path
                .file_stem()
                .map(|value| value.to_string_lossy().into_owned())
                .unwrap_or_default();
            result.push(DiscoveredProgram {
                name,
                target: path.to_string_lossy().into_owned(),
                source: source.to_string(),
                category: None,
            });
        }
    }
}

#[cfg(target_os = "macos")]
fn collect_mac(root: &str, result: &mut Vec<DiscoveredProgram>) {
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_dir()
            || !path
                .to_string_lossy()
                .to_ascii_lowercase()
                .ends_with(".app")
        {
            continue;
        }
        let name = path
            .file_stem()
            .map(|value| value.to_string_lossy().into_owned())
            .unwrap_or_default();
        result.push(DiscoveredProgram {
            name,
            target: path.to_string_lossy().into_owned(),
            source: "Applications".into(),
            category: None,
        });
    }
}

#[cfg(all(not(windows), not(target_os = "macos")))]
fn collect_linux(root: &str, result: &mut Vec<DiscoveredProgram>) {
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|value| value.to_str()) != Some("desktop") {
            continue;
        }
        let name = path
            .file_stem()
            .map(|value| value.to_string_lossy().into_owned())
            .unwrap_or_default();
        result.push(DiscoveredProgram {
            name,
            target: path.to_string_lossy().into_owned(),
            source: "应用程序".into(),
            category: None,
        });
    }
}

pub fn dedupe(programs: Vec<DiscoveredProgram>) -> Vec<DiscoveredProgram> {
    let mut results = Vec::new();
    let mut seen = HashSet::new();
    for program in programs {
        let target = program.target.trim();
        if target.is_empty() {
            continue;
        }
        let key = if target.to_ascii_lowercase().ends_with(".lnk") {
            match resolve_lnk_target(Path::new(target)) {
                Some(resolved) => {
                    if !resolved.to_ascii_lowercase().ends_with(".exe") {
                        continue;
                    }
                    format!("t:{}", resolved.to_ascii_lowercase())
                }
                None => format!(
                    "n:{}",
                    Path::new(target)
                        .file_stem()
                        .map(|value| value.to_string_lossy().to_ascii_lowercase())
                        .unwrap_or_default()
                ),
            }
        } else {
            format!("t:{}", target.to_ascii_lowercase())
        };
        if seen.insert(key) {
            results.push(program);
        }
    }
    results
}

/// MS-SHLLINK 字节解析：取 LinkInfo 的 LocalBasePath（无 COM）。
pub fn resolve_lnk_target(path: &Path) -> Option<String> {
    let bytes = fs::read(path).ok()?;
    if bytes.len() < 76 || &bytes[0..4] != b"L\0\0\0" {
        return None;
    }
    let flags = u32::from_le_bytes(bytes[20..24].try_into().ok()?);
    let mut pos = 76usize;
    if flags & 0x1 != 0 {
        let id_list_size = u16::from_le_bytes(bytes.get(pos..pos + 2)?.try_into().ok()?) as usize;
        if id_list_size == 0 || pos + 2 + id_list_size > bytes.len() {
            return None;
        }
        pos += 2 + id_list_size;
    }
    if flags & 0x2 == 0 || pos + 28 > bytes.len() {
        return None;
    }
    let link_info_size = i32::from_le_bytes(bytes[pos..pos + 4].try_into().ok()?);
    let link_info_flags = u32::from_le_bytes(bytes[pos + 8..pos + 12].try_into().ok()?);
    let local_path_offset = i32::from_le_bytes(bytes[pos + 16..pos + 20].try_into().ok()?);
    if link_info_flags & 0x1 == 0
        || link_info_size <= 0
        || pos + link_info_size as usize > bytes.len()
        || local_path_offset < 28
        || pos + local_path_offset as usize >= bytes.len()
    {
        return None;
    }
    let start = pos + local_path_offset as usize;
    if start + 3 < bytes.len() && bytes[start + 1] == 0 && bytes[start + 3] == 0 {
        let mut end = start;
        while end + 1 < bytes.len() && (bytes[end] != 0 || bytes[end + 1] != 0) {
            end += 2;
        }
        let units: Vec<u16> = bytes[start..end]
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .collect();
        return clean_path(&String::from_utf16_lossy(&units));
    }
    let mut stop = start;
    while stop < bytes.len() && bytes[stop] != 0 {
        stop += 1;
    }
    let decoded: String = bytes[start..stop]
        .iter()
        .map(|byte| *byte as char)
        .collect();
    clean_path(&decoded)
}

fn clean_path(value: &str) -> Option<String> {
    if value.trim().is_empty() || value.chars().any(|ch| (ch as u32) < 0x20) {
        None
    } else {
        Some(value.to_string())
    }
}

pub fn build_id(target: &str) -> String {
    let hash = Sha256::digest(target.to_ascii_lowercase().as_bytes());
    let hex: String = hash.iter().map(|byte| format!("{byte:02x}")).collect();
    format!("idx-{}", &hex[..12])
}

fn is_junk_name(name: &str) -> bool {
    const CHINESE: &[&str] = &[
        "卸载",
        "安装",
        "帮助",
        "手册",
        "说明",
        "官网",
        "网站",
        "更新日志",
        "许可",
        "协议",
        "隐私",
        "反馈",
        "指南",
        "入门",
    ];
    if CHINESE.iter().any(|word| name.contains(word)) {
        return true;
    }
    static JUNK: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let pattern = JUNK.get_or_init(|| {
        regex::Regex::new(
            r"(?i)\b(uninstalls?|unins\w*|setups?|installs?|installers?|readme|helps?|manuals?|changelogs?|licen[cs]es?|websites?|homepages?|documentation|quickstart|tutorial|eula|faq)\b",
        )
        .expect("垃圾名称正则")
    });
    pattern.is_match(name)
}
