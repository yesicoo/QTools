use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipArchive, ZipWriter};

use crate::error::{BridgeError, BridgeResult};

const SCHEMA_VERSION: u32 = 1;
const MAX_IMPORT_BYTES: u64 = 1024 * 1024 * 1024;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupManifest {
    pub schema_version: u32,
    pub created_at_utc: String,
    pub app_version: String,
    pub note: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupEntryView {
    pub name: String,
    pub created_at_utc: String,
    pub size_bytes: u64,
    pub app_version: String,
    pub note: Option<String>,
}

pub struct BackupService {
    root: PathBuf,
}

impl BackupService {
    pub fn new(root: PathBuf) -> Self {
        Self {
            root: fs::canonicalize(&root).unwrap_or(root),
        }
    }

    pub fn backups_dir(&self) -> PathBuf {
        self.root.join("backups")
    }

    pub fn create(&self, note: Option<&str>) -> BridgeResult<(String, u64, String)> {
        self.create_from_directory(&self.root, note)
    }

    pub fn create_from_directory(
        &self,
        source_root: &Path,
        note: Option<&str>,
    ) -> BridgeResult<(String, u64, String)> {
        fs::create_dir_all(self.backups_dir())?;
        let created_at = chrono::Utc::now().to_rfc3339();
        let name = self.unique_name(&format!(
            "qtools-backup-{}.zip",
            chrono::Local::now().format("%Y%m%d-%H%M%S")
        ));
        let target = self.backups_dir().join(&name);
        let temp = target.with_extension("zip.tmp");
        let manifest = BackupManifest {
            schema_version: SCHEMA_VERSION,
            created_at_utc: created_at.clone(),
            app_version: env!("CARGO_PKG_VERSION").into(),
            note: note
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .map(str::to_string),
        };
        let result = (|| -> BridgeResult<()> {
            let file = fs::File::create(&temp)?;
            let mut archive = ZipWriter::new(file);
            let options =
                SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
            archive
                .start_file("backup.json", options)
                .map_err(zip_error)?;
            archive
                .write_all(serde_json::to_string(&manifest)?.as_bytes())
                .map_err(std::io::Error::other)?;
            for relative in enumerate_payload(&source_root.to_path_buf()) {
                let source =
                    source_root.join(relative.replace('/', &std::path::MAIN_SEPARATOR.to_string()));
                let Ok(mut source_file) = fs::File::open(&source) else {
                    crate::logging::warn(&format!("备份跳过无法读取的文件 {relative}"));
                    continue;
                };
                archive.start_file(&relative, options).map_err(zip_error)?;
                std::io::copy(&mut source_file, &mut archive).map_err(std::io::Error::other)?;
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
        Ok((name, size, created_at))
    }

    pub fn list(&self) -> Vec<BackupEntryView> {
        let Ok(entries) = fs::read_dir(self.backups_dir()) else {
            return Vec::new();
        };
        let mut result = Vec::new();
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|value| value.to_str()) != Some("zip") {
                continue;
            }
            let name = entry.file_name().to_string_lossy().into_owned();
            let meta = entry.metadata().ok();
            let size = meta.as_ref().map(|meta| meta.len()).unwrap_or(0);
            let manifest = read_manifest(&path);
            let created = manifest
                .as_ref()
                .map(|value| value.created_at_utc.clone())
                .or_else(|| {
                    meta.and_then(|meta| meta.modified().ok()).map(|value| {
                        let datetime: chrono::DateTime<chrono::Utc> = value.into();
                        datetime.to_rfc3339()
                    })
                })
                .unwrap_or_default();
            let app_version = manifest
                .as_ref()
                .map(|value| value.app_version.clone())
                .unwrap_or_else(|| "未知".into());
            let note = manifest.and_then(|value| value.note);
            result.push(BackupEntryView {
                name,
                created_at_utc: created,
                size_bytes: size,
                app_version,
                note,
            });
        }
        result.sort_by(|left, right| right.name.to_lowercase().cmp(&left.name.to_lowercase()));
        result
    }

    pub fn restore(&self, name: &str) -> BridgeResult<(bool, String)> {
        let path = self.resolve(name)?;
        validate_archive(&path)?;
        let (safety, _, _) = self.create(Some("自动快照：恢复前"))?;
        let staging = self
            .backups_dir()
            .join(format!(".restore-{}", uuid::Uuid::new_v4().simple()));
        let result = (|| -> BridgeResult<()> {
            fs::create_dir_all(&staging)?;
            extract_archive(&path, &staging)?;
            for directory in ["tools", "apps", "data/storage"] {
                let live = self
                    .root
                    .join(directory.replace('/', &std::path::MAIN_SEPARATOR.to_string()));
                let restored =
                    staging.join(directory.replace('/', &std::path::MAIN_SEPARATOR.to_string()));
                if live.exists() {
                    fs::remove_dir_all(&live)?;
                }
                if restored.exists() {
                    fs::rename(&restored, &live)?;
                } else {
                    fs::create_dir_all(&live)?;
                }
            }
            let restored_config = staging.join("data").join("config.json");
            if restored_config.is_file() {
                let live_config = self.root.join("data").join("config.json");
                if let Some(parent) = live_config.parent() {
                    fs::create_dir_all(parent)?;
                }
                let _ = fs::remove_file(&live_config);
                fs::rename(restored_config, live_config)?;
            }
            Ok(())
        })();
        let _ = fs::remove_dir_all(&staging);
        result.map_err(|error| {
            BridgeError::new(
                "BACKUP_RESTORE_FAILED",
                format!("恢复失败：{}（恢复前快照已保存为 {safety}）", error.message),
            )
        })?;
        Ok((true, safety))
    }

    pub fn delete(&self, name: &str) -> BridgeResult<bool> {
        let path = self.resolve(name)?;
        Ok(fs::remove_file(path).is_ok())
    }

    pub fn import_file(&self, source_path: &str) -> BridgeResult<String> {
        let source = Path::new(source_path);
        if !source.is_file() {
            return Err(BridgeError::new("BACKUP_FILE_NOT_FOUND", "备份文件不存在"));
        }
        let size = fs::metadata(source).map(|meta| meta.len()).unwrap_or(0);
        if size > MAX_IMPORT_BYTES {
            return Err(BridgeError::new("BACKUP_TOO_LARGE", "备份文件不超过 1 GB"));
        }
        validate_archive(source)?;
        fs::create_dir_all(self.backups_dir())?;
        let base_name = source
            .file_name()
            .map(|value| value.to_string_lossy().into_owned())
            .unwrap_or_else(|| {
                format!(
                    "imported-{}.zip",
                    chrono::Local::now().format("%Y%m%d-%H%M%S")
                )
            });
        let name = if base_name.to_ascii_lowercase().ends_with(".zip")
            && base_name.chars().all(|ch| {
                ch.is_ascii_lowercase() || ch.is_ascii_digit() || matches!(ch, '.' | '_' | '-')
            }) {
            self.unique_name(&base_name)
        } else {
            self.unique_name(&format!(
                "imported-{}.zip",
                chrono::Local::now().format("%Y%m%d-%H%M%S")
            ))
        };
        fs::copy(source, self.backups_dir().join(&name))?;
        Ok(name)
    }

    pub fn open_directory(&self) -> BridgeResult<()> {
        fs::create_dir_all(self.backups_dir())?;
        open::that(self.backups_dir()).map_err(|error| {
            BridgeError::new("BACKUP_OPEN_FAILED", format!("无法打开备份目录：{error}"))
        })
    }

    fn resolve(&self, name: &str) -> BridgeResult<PathBuf> {
        if name.is_empty()
            || name.len() > 128
            || !name.chars().all(|ch| {
                ch.is_ascii_lowercase() || ch.is_ascii_digit() || matches!(ch, '.' | '_' | '-')
            })
            || !name.to_ascii_lowercase().ends_with(".zip")
        {
            return Err(BridgeError::new("BACKUP_NAME_INVALID", "备份文件名不合法"));
        }
        Ok(self.backups_dir().join(name))
    }

    fn unique_name(&self, candidate: &str) -> String {
        if !self.backups_dir().join(candidate).exists() {
            return candidate.to_string();
        }
        let stem = candidate.trim_end_matches(".zip");
        for suffix in 2..1000 {
            let next = format!("{stem}-{suffix}.zip");
            if !self.backups_dir().join(&next).exists() {
                return next;
            }
        }
        format!("{stem}-{}.zip", chrono::Utc::now().timestamp())
    }
}

fn read_manifest(path: &Path) -> Option<BackupManifest> {
    let file = fs::File::open(path).ok()?;
    let mut archive = ZipArchive::new(file).ok()?;
    let mut entry = archive.by_name("backup.json").ok()?;
    let mut text = String::new();
    entry.read_to_string(&mut text).ok()?;
    serde_json::from_str(&text).ok()
}

fn validate_archive(path: &Path) -> BridgeResult<()> {
    let file = fs::File::open(path)
        .map_err(|error| BridgeError::new("BACKUP_INVALID", format!("备份包无法读取：{error}")))?;
    let mut archive = ZipArchive::new(file)
        .map_err(|error| BridgeError::new("BACKUP_INVALID", format!("备份包无法读取：{error}")))?;
    if archive.by_name("backup.json").is_err() {
        return Err(BridgeError::new(
            "BACKUP_INVALID",
            "不是 QTools 备份包（缺少 backup.json 清单）",
        ));
    }
    for index in 0..archive.len() {
        let entry = archive.by_index(index).map_err(|error| {
            BridgeError::new("BACKUP_INVALID", format!("备份包无法读取：{error}"))
        })?;
        if !is_allowed_entry(entry.name()) {
            return Err(BridgeError::new(
                "BACKUP_INVALID",
                format!("备份包含非法路径条目：{}", entry.name()),
            ));
        }
    }
    Ok(())
}

fn extract_archive(path: &Path, staging: &Path) -> BridgeResult<()> {
    let file = fs::File::open(path)
        .map_err(|error| BridgeError::new("BACKUP_INVALID", format!("备份包无法读取：{error}")))?;
    let mut archive = ZipArchive::new(file)
        .map_err(|error| BridgeError::new("BACKUP_INVALID", format!("备份包无法读取：{error}")))?;
    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|error| {
            BridgeError::new("BACKUP_INVALID", format!("备份包无法读取：{error}"))
        })?;
        let relative = entry.name().replace('\\', "/");
        if relative == "backup.json" || relative.ends_with('/') {
            continue;
        }
        let destination =
            staging.join(relative.replace('/', &std::path::MAIN_SEPARATOR.to_string()));
        if let Some(parent) = destination.parent() {
            fs::create_dir_all(parent)?;
        }
        let mut output = fs::File::create(&destination)?;
        std::io::copy(&mut entry, &mut output)?;
    }
    Ok(())
}

fn is_allowed_entry(name: &str) -> bool {
    let normalized = name.replace('\\', "/");
    if normalized.is_empty() || normalized.starts_with('/') || normalized.contains(':') {
        return false;
    }
    if normalized
        .split('/')
        .any(|segment| segment.is_empty() || segment == "." || segment == "..")
    {
        return false;
    }
    normalized == "backup.json"
        || normalized == "data/config.json"
        || normalized.starts_with("tools/")
        || normalized.starts_with("apps/")
        || normalized.starts_with("data/storage/")
}

fn enumerate_payload(root: &Path) -> Vec<String> {
    let mut result = Vec::new();
    for directory in ["tools", "apps", "data/storage"] {
        let base = root.join(directory.replace('/', &std::path::MAIN_SEPARATOR.to_string()));
        if !base.is_dir() {
            continue;
        }
        for entry in walkdir::WalkDir::new(&base).into_iter().flatten() {
            if entry.file_type().is_file() {
                if let Ok(relative) = entry.path().strip_prefix(root) {
                    result.push(relative.to_string_lossy().replace('\\', "/"));
                }
            }
        }
    }
    if root.join("data").join("config.json").is_file() {
        result.push("data/config.json".into());
    }
    result
}

fn zip_error(error: zip::result::ZipError) -> BridgeError {
    BridgeError::new("BACKUP_CREATE_FAILED", format!("写入备份包失败：{error}"))
}
