//! 单条目导出/导入包（.qtools）：小工具只导出当前运行版本，启动项与在线工具导出各自清单。
//! 与整机备份是两套包：本包清单为 qtools-export.json，导入时按清单自动识别类型。

use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipArchive, ZipWriter};

use crate::creator::{
    SecurityFinding, detect_external_assets, detect_permissions, generate_tool_id, scan,
};
use crate::error::{BridgeError, BridgeResult};
use crate::state::AppState;
use crate::tools::{
    ToolManifest, is_safe_relative_path, is_valid_tool_id, validate_manifest, write_manifest,
};

pub const MANIFEST_ENTRY: &str = "qtools-export.json";
pub const FILE_EXTENSION: &str = ".qtools";
const SCHEMA_VERSION: u32 = 1;
const MAX_PACKAGE_BYTES: u64 = 64 * 1024 * 1024;
const MAX_ENTRY_BYTES: u64 = 16 * 1024 * 1024;
const MAX_ENTRIES: usize = 2000;
const MAX_HTML_BYTES: usize = 512 * 1024;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ItemExportManifest {
    pub schema_version: u32,
    pub kind: String,
    pub id: String,
    pub name: String,
    pub version: Option<String>,
    pub exported_at_utc: String,
    pub app_version: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ItemExportResult {
    pub path: Option<String>,
    pub file_name: Option<String>,
    pub size_bytes: u64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PickedImportFileView {
    pub path: Option<String>,
    pub file_name: Option<String>,
    pub kind: String,
    pub html: Option<String>,
    pub name: Option<String>,
    pub version: Option<String>,
    pub item_kind: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportItemResult {
    pub kind: String,
    pub item_id: String,
    pub item_name: String,
    pub saved: bool,
    pub permissions: Vec<String>,
    pub findings: Vec<SecurityFinding>,
    pub has_fatal: bool,
    pub requires_explicit_acceptance: bool,
    pub external_assets: Vec<String>,
}

struct ResolvedItem {
    name: String,
    version: Option<String>,
    directory: PathBuf,
    manifest_name: &'static str,
}

fn resolve_item(state: &AppState, kind: &str, id: &str) -> BridgeResult<ResolvedItem> {
    match kind {
        "tool" => {
            let tool = state
                .tools
                .find(id)
                .ok_or_else(|| BridgeError::new("TOOL_NOT_FOUND", format!("未找到工具 '{id}'")))?;
            Ok(ResolvedItem {
                name: tool.name().to_string(),
                version: Some(tool.manifest.version.clone()),
                directory: tool.directory.clone(),
                manifest_name: "manifest.json",
            })
        }
        "app" => {
            let app = state
                .apps
                .find(id)
                .ok_or_else(|| BridgeError::new("APP_NOT_FOUND", format!("未找到启动项 '{id}'")))?;
            Ok(ResolvedItem {
                name: app.name().to_string(),
                version: None,
                directory: app.directory.clone(),
                manifest_name: "app.json",
            })
        }
        "web" => {
            let web = state.web_tools.find(id).ok_or_else(|| {
                BridgeError::new("WEB_NOT_FOUND", format!("未找到在线工具 '{id}'"))
            })?;
            Ok(ResolvedItem {
                name: web.name().to_string(),
                version: None,
                directory: web.directory.clone(),
                manifest_name: "web.json",
            })
        }
        _ => Err(BridgeError::new(
            "EXPORT_KIND_INVALID",
            "本机程序不支持导出（请导出小工具 / 启动项 / 在线工具）",
        )),
    }
}

fn sanitize_file_name(name: &str) -> String {
    let mut stem: String = name
        .chars()
        .map(|ch| {
            if ch.is_control() || matches!(ch, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*')
            {
                '_'
            } else {
                ch
            }
        })
        .collect();
    stem = stem.trim().trim_end_matches(['.', ' ']).to_string();
    if stem.chars().count() > 80 {
        stem = stem.chars().take(80).collect::<String>();
        stem = stem.trim_end_matches(['.', ' ']).to_string();
    }
    if stem.is_empty() {
        stem = "qtools-item".into();
    }
    stem
}

pub fn export_item(state: &AppState, kind: &str, id: &str) -> BridgeResult<ItemExportResult> {
    let item = resolve_item(state, kind, id)?;
    let default_name = sanitize_file_name(&item.name) + FILE_EXTENSION;
    let Some(mut target) = rfd::FileDialog::new()
        .add_filter("QTools 导出包", &["qtools"])
        .set_file_name(&default_name)
        .save_file()
    else {
        return Ok(ItemExportResult {
            path: None,
            file_name: None,
            size_bytes: 0,
        });
    };
    if target.extension().is_none() {
        target.set_extension("qtools");
    }

    let prefix = if kind == "tool" {
        format!("tools/{id}/")
    } else if kind == "app" {
        format!("apps/{id}/")
    } else {
        format!("webtools/{id}/")
    };
    let files: Vec<(String, PathBuf)> = if kind == "tool" {
        enumerate_tool_files(&item.directory)
            .into_iter()
            .map(|relative| {
                (
                    format!("{prefix}{relative}"),
                    item.directory
                        .join(relative.replace('/', std::path::MAIN_SEPARATOR_STR)),
                )
            })
            .collect()
    } else {
        vec![(
            format!("{prefix}{}", item.manifest_name),
            item.directory.join(item.manifest_name),
        )]
    };

    let manifest = ItemExportManifest {
        schema_version: SCHEMA_VERSION,
        kind: kind.to_string(),
        id: id.to_string(),
        name: item.name.clone(),
        version: item.version,
        exported_at_utc: chrono::Utc::now().to_rfc3339(),
        app_version: env!("CARGO_PKG_VERSION").into(),
    };

    let temp = target.with_extension("qtools.tmp");
    let result = (|| -> BridgeResult<()> {
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent)?;
        }
        let file = fs::File::create(&temp)?;
        let mut archive = ZipWriter::new(file);
        let options = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
        archive
            .start_file(MANIFEST_ENTRY, options)
            .map_err(zip_error)?;
        archive
            .write_all(serde_json::to_string(&manifest)?.as_bytes())
            .map_err(std::io::Error::other)?;
        for (relative, full) in files {
            let Ok(mut source) = fs::File::open(&full) else {
                crate::logging::warn(&format!("导出跳过无法读取的文件 {}", full.display()));
                continue;
            };
            archive.start_file(&relative, options).map_err(zip_error)?;
            std::io::copy(&mut source, &mut archive).map_err(std::io::Error::other)?;
        }
        archive.finish().map_err(zip_error)?;
        fs::rename(&temp, &target)?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp);
    }
    result?;
    let size = fs::metadata(&target).map(|meta| meta.len()).unwrap_or(0);
    crate::logging::info(&format!(
        "已导出 {kind}:{id}（{}）→ {}（{size} 字节）",
        item.name,
        target.display()
    ));
    Ok(ItemExportResult {
        path: Some(target.to_string_lossy().into_owned()),
        file_name: target
            .file_name()
            .map(|value| value.to_string_lossy().into_owned()),
        size_bytes: size,
    })
}

/// 工具目录内的全部文件（相对路径，'/' 分隔）；跳过点号前缀目录，绝不带上 .versions 历史。
fn enumerate_tool_files(tool_directory: &Path) -> Vec<String> {
    let mut result = Vec::new();
    for entry in walkdir::WalkDir::new(tool_directory)
        .follow_links(false)
        .into_iter()
        .filter_map(Result::ok)
    {
        if !entry.file_type().is_file() {
            continue;
        }
        let Ok(relative) = entry.path().strip_prefix(tool_directory) else {
            continue;
        };
        let relative = relative
            .components()
            .map(|component| component.as_os_str().to_string_lossy())
            .collect::<Vec<_>>()
            .join("/");
        if relative.split('/').any(|segment| segment.starts_with('.')) {
            continue;
        }
        result.push(relative);
    }
    result
}

pub fn pick_import_file() -> BridgeResult<PickedImportFileView> {
    let Some(path) = rfd::FileDialog::new()
        .add_filter("QTools 导入包或 HTML", &["qtools", "zip", "html", "htm"])
        .pick_file()
    else {
        return Ok(PickedImportFileView {
            path: None,
            file_name: None,
            kind: String::new(),
            html: None,
            name: None,
            version: None,
            item_kind: None,
        });
    };
    inspect(&path)
}

pub fn inspect(path: &Path) -> BridgeResult<PickedImportFileView> {
    if !path.is_file() {
        return Err(BridgeError::new("IMPORT_FILE_NOT_FOUND", "文件不存在"));
    }
    let file_name = path
        .file_name()
        .map(|value| value.to_string_lossy().into_owned())
        .unwrap_or_default();
    let extension = path
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    if extension == "html" || extension == "htm" {
        let bytes = fs::read(path)?;
        if bytes.len() > MAX_HTML_BYTES {
            return Err(BridgeError::new(
                "IMPORT_HTML_TOO_LARGE",
                format!("HTML 文件过大，上限 {} KB", MAX_HTML_BYTES / 1024),
            ));
        }
        let html = String::from_utf8_lossy(&bytes).into_owned();
        return Ok(PickedImportFileView {
            path: Some(path.to_string_lossy().into_owned()),
            file_name: Some(file_name),
            kind: "html".into(),
            html: Some(html),
            name: path
                .file_stem()
                .map(|value| value.to_string_lossy().into_owned()),
            version: None,
            item_kind: None,
        });
    }

    let manifest = try_read_manifest(path)?.ok_or_else(|| {
        BridgeError::new(
            "IMPORT_FILE_INVALID",
            "请选择 HTML 文件或 QTools 导出包（.qtools）",
        )
    })?;
    validate_archive(path)?;
    Ok(PickedImportFileView {
        path: Some(path.to_string_lossy().into_owned()),
        file_name: Some(file_name),
        kind: "package".into(),
        html: None,
        name: Some(manifest.name),
        version: manifest.version,
        item_kind: Some(manifest.kind),
    })
}

fn try_read_manifest(path: &Path) -> BridgeResult<Option<ItemExportManifest>> {
    let Ok(file) = fs::File::open(path) else {
        return Ok(None);
    };
    let Ok(mut archive) = ZipArchive::new(file) else {
        return Ok(None);
    };
    let Ok(mut entry) = archive.by_name(MANIFEST_ENTRY) else {
        return Ok(None);
    };
    let mut text = String::new();
    if entry.read_to_string(&mut text).is_err() {
        return Ok(None);
    }
    Ok(serde_json::from_str::<ItemExportManifest>(&text).ok())
}

pub fn import_package(
    state: &Arc<AppState>,
    path: &str,
    accept_high_severity: bool,
) -> BridgeResult<ImportItemResult> {
    let path = Path::new(path);
    if !path.is_file() {
        return Err(BridgeError::new("IMPORT_FILE_NOT_FOUND", "导出包不存在"));
    }
    let size = fs::metadata(path).map(|meta| meta.len()).unwrap_or(0);
    if size > MAX_PACKAGE_BYTES {
        return Err(BridgeError::new(
            "IMPORT_PACKAGE_TOO_LARGE",
            format!("导出包不超过 {} MB", MAX_PACKAGE_BYTES / 1024 / 1024),
        ));
    }
    if is_whole_backup(path) {
        return Err(BridgeError::new(
            "IMPORT_PACKAGE_INVALID",
            "这是整机备份包，请到「设置 → 备份与恢复」导入",
        ));
    }
    let manifest = try_read_manifest(path)?.ok_or_else(|| {
        BridgeError::new(
            "IMPORT_PACKAGE_INVALID",
            "不是 QTools 导出包（缺少 qtools-export.json 清单）",
        )
    })?;
    validate_archive(path)?;
    match manifest.kind.as_str() {
        "tool" => import_tool(state, path, &manifest, accept_high_severity),
        "app" => import_app(state, path, &manifest),
        "web" => import_web(state, path, &manifest),
        other => Err(BridgeError::new(
            "IMPORT_PACKAGE_INVALID",
            format!("导出包类型不支持：{other}"),
        )),
    }
}

fn import_tool(
    state: &Arc<AppState>,
    path: &Path,
    manifest: &ItemExportManifest,
    accept_high_severity: bool,
) -> BridgeResult<ImportItemResult> {
    let prefix = format!("tools/{}/", manifest.id);
    let new_id = if is_valid_tool_id(&manifest.id) && !state.tools.contains(&manifest.id) {
        manifest.id.clone()
    } else {
        generate_tool_id(&state.tools, &manifest.name, "imported-")
    };

    let staging = state
        .paths
        .root
        .join("data")
        .join("import-staging")
        .join(uuid::Uuid::new_v4().simple().to_string());
    let staged_tool = staging.join(&new_id);
    let result = (|| -> BridgeResult<ImportItemResult> {
        fs::create_dir_all(&staged_tool)?;
        let file = fs::File::open(path)?;
        let mut archive = ZipArchive::new(file).map_err(zip_error)?;
        let mut extracted = 0usize;
        for index in 0..archive.len() {
            let mut entry = archive.by_index(index).map_err(zip_error)?;
            let relative = entry.name().replace('\\', "/");
            if !relative.starts_with(&prefix) || entry.is_dir() {
                continue;
            }
            let inner = &relative[prefix.len()..];
            if inner.is_empty() || !is_safe_relative_path(inner) {
                continue;
            }
            let destination = staged_tool.join(inner.replace('/', std::path::MAIN_SEPARATOR_STR));
            if let Some(parent) = destination.parent() {
                fs::create_dir_all(parent)?;
            }
            let mut buffer = Vec::new();
            entry.read_to_end(&mut buffer)?;
            fs::write(&destination, &buffer)?;
            extracted += 1;
        }
        if extracted == 0 {
            return Err(BridgeError::new(
                "IMPORT_PACKAGE_INVALID",
                "导出包里没有工具文件",
            ));
        }
        let manifest_path = staged_tool.join("manifest.json");
        if !manifest_path.is_file() {
            return Err(BridgeError::new(
                "IMPORT_PACKAGE_INVALID",
                "导出包缺少 manifest.json",
            ));
        }
        let mut tool_manifest: ToolManifest =
            serde_json::from_str(&fs::read_to_string(&manifest_path)?).map_err(|error| {
                BridgeError::new(
                    "IMPORT_PACKAGE_INVALID",
                    format!("manifest.json 解析失败：{error}"),
                )
            })?;
        let entry_relative = if tool_manifest.entry.trim().is_empty() {
            "index.html".to_string()
        } else {
            tool_manifest.entry.clone()
        };
        if !is_safe_relative_path(&entry_relative) {
            return Err(BridgeError::new(
                "IMPORT_PACKAGE_INVALID",
                "导出包的入口路径不合法",
            ));
        }
        let entry_path =
            staged_tool.join(entry_relative.replace('/', std::path::MAIN_SEPARATOR_STR));
        if !entry_path.is_file() {
            return Err(BridgeError::new(
                "IMPORT_PACKAGE_INVALID",
                format!("导出包缺少入口文件 {entry_relative}"),
            ));
        }
        let html = fs::read_to_string(&entry_path)?;

        let mut permissions: Vec<String> = tool_manifest
            .permissions
            .iter()
            .map(|value| value.trim().to_ascii_lowercase())
            .filter(|value| crate::tools::PERMISSIONS.contains(&value.as_str()))
            .collect();
        for permission in detect_permissions(&html) {
            if !permissions.contains(&permission) {
                permissions.push(permission);
            }
        }
        tool_manifest.id = new_id.clone();
        tool_manifest.permissions = permissions.clone();

        let (findings, has_fatal) = scan(&tool_manifest, &html);
        let external_assets = detect_external_assets(&html);
        if has_fatal {
            let reason = findings
                .iter()
                .filter(|finding| finding.severity == 0)
                .map(|finding| finding.message.as_str())
                .collect::<Vec<_>>()
                .join("；");
            crate::logging::warn(&format!("导出包导入被拒绝（{new_id}）：{reason}"));
            return Err(BridgeError::new(
                "IMPORT_REJECTED",
                format!("内容未通过安全审查：{reason}"),
            ));
        }
        let requires_acceptance = findings.iter().any(|finding| finding.severity == 1);
        if requires_acceptance && !accept_high_severity {
            return Ok(ImportItemResult {
                kind: "tool".into(),
                item_id: new_id.clone(),
                item_name: tool_manifest.name.clone(),
                saved: false,
                permissions,
                findings,
                has_fatal: false,
                requires_explicit_acceptance: true,
                external_assets,
            });
        }

        let errors = validate_manifest(&tool_manifest, &new_id);
        if !errors.is_empty() {
            return Err(BridgeError::new(
                "IMPORT_REJECTED",
                format!("工具未通过校验：{}", errors.join("；")),
            ));
        }
        let live_directory = state.paths.tools_dir().join(&new_id);
        if live_directory.exists() {
            return Err(BridgeError::new(
                "IMPORT_REJECTED",
                format!("工具目录已存在：{new_id}"),
            ));
        }
        fs::create_dir_all(state.paths.tools_dir())?;
        fs::rename(&staged_tool, &live_directory)?;
        write_manifest(&live_directory, &tool_manifest)?;
        let loaded = state.tools.reload(&new_id);
        if !loaded.as_ref().is_some_and(|tool| tool.is_valid) {
            let reason = loaded
                .map(|tool| tool.errors.join("；"))
                .filter(|value| !value.is_empty())
                .unwrap_or_else(|| "未知原因".into());
            let _ = fs::remove_dir_all(&live_directory);
            return Err(BridgeError::new(
                "IMPORT_REJECTED",
                format!("工具未通过运行时校验：{reason}"),
            ));
        }
        state.usage.ensure_seeded([new_id.clone()]);
        crate::logging::info(&format!(
            "已从导出包导入工具 {new_id}（{}）",
            tool_manifest.name
        ));
        Ok(ImportItemResult {
            kind: "tool".into(),
            item_id: new_id,
            item_name: tool_manifest.name,
            saved: true,
            permissions,
            findings,
            has_fatal: false,
            requires_explicit_acceptance: false,
            external_assets,
        })
    })();
    let _ = fs::remove_dir_all(&staging);
    result
}

fn import_app(
    state: &Arc<AppState>,
    path: &Path,
    manifest: &ItemExportManifest,
) -> BridgeResult<ImportItemResult> {
    let value = read_manifest_value(path, &format!("apps/{}/app.json", manifest.id))?;
    let name = value
        .get("name")
        .and_then(Value::as_str)
        .unwrap_or(&manifest.name);
    let target = value
        .get("target")
        .and_then(Value::as_str)
        .ok_or_else(|| BridgeError::new("IMPORT_PACKAGE_INVALID", "app.json 缺少 target"))?;
    let loaded = state.app_store.add(
        name,
        target,
        value.get("arguments").and_then(Value::as_str),
        value.get("workingDirectory").and_then(Value::as_str),
        value.get("category").and_then(Value::as_str),
        value.get("glyph").and_then(Value::as_str),
    )?;
    state.usage.ensure_seeded([loaded.entry.id.clone()]);
    let loaded_id = loaded.entry.id.clone();
    Ok(ImportItemResult {
        kind: "app".into(),
        item_id: loaded_id,
        item_name: loaded.name().to_string(),
        saved: true,
        permissions: Vec::new(),
        findings: Vec::new(),
        has_fatal: false,
        requires_explicit_acceptance: false,
        external_assets: Vec::new(),
    })
}

fn import_web(
    state: &Arc<AppState>,
    path: &Path,
    manifest: &ItemExportManifest,
) -> BridgeResult<ImportItemResult> {
    let value = read_manifest_value(path, &format!("webtools/{}/web.json", manifest.id))?;
    let name = value
        .get("name")
        .and_then(Value::as_str)
        .unwrap_or(&manifest.name);
    let url = value
        .get("url")
        .and_then(Value::as_str)
        .ok_or_else(|| BridgeError::new("IMPORT_PACKAGE_INVALID", "web.json 缺少 url"))?;
    let loaded = state.web_store.add(
        name,
        url,
        value.get("category").and_then(Value::as_str),
        value.get("glyph").and_then(Value::as_str),
        value.get("description").and_then(Value::as_str),
    )?;
    state.usage.ensure_seeded([loaded.entry.id.clone()]);
    let loaded_id = loaded.entry.id.clone();
    Ok(ImportItemResult {
        kind: "web".into(),
        item_id: loaded_id,
        item_name: loaded.name().to_string(),
        saved: true,
        permissions: Vec::new(),
        findings: Vec::new(),
        has_fatal: false,
        requires_explicit_acceptance: false,
        external_assets: Vec::new(),
    })
}

fn read_manifest_value(path: &Path, entry_name: &str) -> BridgeResult<Value> {
    let file = fs::File::open(path)?;
    let mut archive = ZipArchive::new(file).map_err(zip_error)?;
    let mut entry = archive.by_name(entry_name).map_err(|_| {
        BridgeError::new("IMPORT_PACKAGE_INVALID", format!("导出包缺少 {entry_name}"))
    })?;
    let mut text = String::new();
    entry.read_to_string(&mut text).map_err(|error| {
        BridgeError::new(
            "IMPORT_PACKAGE_INVALID",
            format!("{entry_name} 读取失败：{error}"),
        )
    })?;
    serde_json::from_str(&text).map_err(|error| {
        BridgeError::new(
            "IMPORT_PACKAGE_INVALID",
            format!("{entry_name} 解析失败：{error}"),
        )
    })
}

fn is_whole_backup(path: &Path) -> bool {
    let Ok(file) = fs::File::open(path) else {
        return false;
    };
    let Ok(mut archive) = ZipArchive::new(file) else {
        return false;
    };
    archive.by_name("backup.json").is_ok()
}

/// 白名单 + 防穿越 + 体积上限：只允许清单与三类条目目录，拒绝点号前缀段（.versions）。
fn validate_archive(path: &Path) -> BridgeResult<()> {
    let file = fs::File::open(path).map_err(|error| {
        BridgeError::new("IMPORT_PACKAGE_INVALID", format!("导出包无法读取：{error}"))
    })?;
    let mut archive = ZipArchive::new(file).map_err(|error| {
        BridgeError::new("IMPORT_PACKAGE_INVALID", format!("导出包无法读取：{error}"))
    })?;
    if archive.by_name(MANIFEST_ENTRY).is_err() {
        return Err(BridgeError::new(
            "IMPORT_PACKAGE_INVALID",
            "不是 QTools 导出包（缺少 qtools-export.json 清单）",
        ));
    }
    if archive.len() > MAX_ENTRIES {
        return Err(BridgeError::new(
            "IMPORT_PACKAGE_INVALID",
            format!("导出包条目过多（上限 {MAX_ENTRIES}）"),
        ));
    }
    let mut total = 0u64;
    for index in 0..archive.len() {
        let entry = archive.by_index(index).map_err(zip_error)?;
        let name = entry.name().replace('\\', "/");
        if !is_allowed_entry(&name) {
            return Err(BridgeError::new(
                "IMPORT_PACKAGE_INVALID",
                format!("导出包含非法路径条目：{name}"),
            ));
        }
        if entry.size() > MAX_ENTRY_BYTES {
            return Err(BridgeError::new(
                "IMPORT_PACKAGE_INVALID",
                format!("导出包内单个文件过大：{name}"),
            ));
        }
        total = total.saturating_add(entry.size());
        if total > MAX_PACKAGE_BYTES {
            return Err(BridgeError::new(
                "IMPORT_PACKAGE_INVALID",
                "导出包解压后体积过大",
            ));
        }
    }
    Ok(())
}

fn is_allowed_entry(full_name: &str) -> bool {
    let normalized = full_name.replace('\\', "/");
    if normalized.is_empty() || normalized.starts_with('/') || normalized.contains(':') {
        return false;
    }
    for segment in normalized.split('/') {
        if segment.is_empty() || segment == "." || segment == ".." || segment.starts_with('.') {
            return false;
        }
    }
    normalized == MANIFEST_ENTRY
        || normalized.starts_with("tools/")
        || normalized.starts_with("apps/")
        || normalized.starts_with("webtools/")
}

fn zip_error(error: zip::result::ZipError) -> BridgeError {
    BridgeError::new("IMPORT_PACKAGE_INVALID", format!("导出包处理失败：{error}"))
}

#[cfg(test)]
mod tests {
    use super::is_allowed_entry;

    #[test]
    fn package_entries_are_whitelisted_and_reject_dot_directories() {
        assert!(is_allowed_entry("qtools-export.json"));
        assert!(is_allowed_entry("tools/demo/index.html"));
        assert!(is_allowed_entry("apps/demo/app.json"));
        assert!(is_allowed_entry("webtools/demo/web.json"));
        assert!(!is_allowed_entry("tools/demo/.versions/1.0.0/index.html"));
        assert!(!is_allowed_entry("tools/../data/config.json"));
        assert!(!is_allowed_entry("backup.json"));
        assert!(!is_allowed_entry("/tools/demo/index.html"));
    }
}
