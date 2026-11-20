use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output, Stdio};
use std::time::{Duration, Instant};

use crate::backup::BackupService;
use crate::error::{BridgeError, BridgeResult};

const GIT_TIMEOUT: Duration = Duration::from_secs(120);
const TRANSFER_TIMEOUT: Duration = Duration::from_secs(300);

pub fn validate_remote_url(url: &str) -> BridgeResult<()> {
    let trimmed = url.trim();
    if trimmed.is_empty() {
        return Err(BridgeError::new(
            "GIT_URL_REQUIRED",
            "请先填写 Git 仓库地址",
        ));
    }
    if trimmed.chars().count() > 500
        || trimmed.starts_with('-')
        || trimmed.chars().any(char::is_control)
    {
        return Err(BridgeError::new(
            "GIT_URL_INVALID",
            "Git 仓库地址不合法（不超过 500 字符，不能以 - 开头或含控制字符）",
        ));
    }
    Ok(())
}

pub struct GitResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}

pub fn git(args: &[&str], working_directory: &Path, timeout: Duration) -> BridgeResult<GitResult> {
    let mut command = Command::new("git");
    command
        .args(args)
        .current_dir(working_directory)
        .env("GIT_TERMINAL_PROMPT", "0")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .stdin(Stdio::null());
    let mut child = command.spawn().map_err(|_| {
        BridgeError::new(
            "GIT_NOT_FOUND",
            "未检测到 git 命令：请先安装 Git 并确保其在 PATH 中",
        )
    })?;
    let started = Instant::now();
    loop {
        if child.try_wait().ok().flatten().is_some() {
            break;
        }
        if started.elapsed() > timeout {
            let _ = child.kill();
            let _ = child.wait();
            return Err(BridgeError::new(
                "GIT_TIMEOUT",
                "Git 操作超时，请检查网络或仓库地址",
            ));
        }
        std::thread::sleep(Duration::from_millis(80));
    }
    let output: Output = child
        .wait_with_output()
        .map_err(|error| BridgeError::new("GIT_COMMAND_FAILED", error.to_string()))?;
    Ok(GitResult {
        exit_code: output.status.code().unwrap_or(-1),
        stdout: String::from_utf8_lossy(&output.stdout).into_owned(),
        stderr: String::from_utf8_lossy(&output.stderr).into_owned(),
    })
}

pub fn push(
    root: &Path,
    data_dir: &Path,
    remote_url: &str,
    progress: &mut dyn FnMut(&str),
) -> BridgeResult<(bool, bool, String)> {
    validate_remote_url(remote_url)?;
    let remote_url = remote_url.trim();
    fs::create_dir_all(data_dir)?;
    progress("检查 git 环境…\n");
    let version = git(&["--version"], data_dir, GIT_TIMEOUT)?;
    if version.exit_code != 0 {
        return Err(BridgeError::new(
            "GIT_NOT_FOUND",
            "git 命令执行失败，请确认已安装 Git",
        ));
    }
    let repo = data_dir.join("backup-repo");
    fs::create_dir_all(&repo)?;
    if !repo.join(".git").is_dir() {
        progress("初始化本地备份仓库…\n");
        let init = git(&["init", "-b", "main"], &repo, GIT_TIMEOUT)?;
        if init.exit_code != 0 {
            let fallback = git(&["init"], &repo, GIT_TIMEOUT)?;
            if fallback.exit_code != 0 {
                return Err(BridgeError::new(
                    "GIT_INIT_FAILED",
                    format!("初始化备份仓库失败：{}", sanitize(&fallback.stderr)),
                ));
            }
        }
    }
    ensure_identity(&repo)?;
    ensure_remote(&repo, remote_url)?;

    progress("同步用户数据到备份仓库…\n");
    sync_mirror(root, &repo)?;
    checked(&repo, &["add", "-A"], "暂存备份内容")?;
    let status = git(&["status", "--porcelain"], &repo, GIT_TIMEOUT)?;
    if status.exit_code == 0 && status.stdout.trim().is_empty() {
        return Ok((false, false, "数据没有变化，无需推送".into()));
    }
    progress("提交变更…\n");
    let message = format!(
        "QTools 备份 {}",
        chrono::Local::now().format("%Y-%m-%d %H:%M:%S")
    );
    checked(&repo, &["commit", "-m", &message], "提交备份")?;
    let _ = git(&["branch", "-M", "main"], &repo, GIT_TIMEOUT);
    progress("推送到远程仓库（凭证由 git 自身管理）…\n");
    let push = git(&["push", "-u", "origin", "HEAD"], &repo, TRANSFER_TIMEOUT)?;
    if push.exit_code != 0 {
        return Err(BridgeError::new(
            "GIT_PUSH_FAILED",
            format!(
                "推送到远程仓库失败（退出码 {}）：{}",
                push.exit_code,
                sanitize(&push.stderr)
            ),
        ));
    }
    Ok((true, true, "已提交并推送到远程仓库".into()))
}

pub fn restore(
    backup: &BackupService,
    data_dir: &Path,
    remote_url: &str,
    progress: &mut dyn FnMut(&str),
) -> BridgeResult<(bool, String)> {
    validate_remote_url(remote_url)?;
    fs::create_dir_all(data_dir)?;
    progress("检查 git 环境…\n");
    let version = git(&["--version"], data_dir, GIT_TIMEOUT)?;
    if version.exit_code != 0 {
        return Err(BridgeError::new(
            "GIT_NOT_FOUND",
            "git 命令执行失败，请确认已安装 Git",
        ));
    }
    let staging = data_dir.join(format!(".git-restore-{}", uuid::Uuid::new_v4().simple()));
    let result = (|| -> BridgeResult<(bool, String)> {
        progress("读取远程仓库分支…\n");
        let heads = git(
            &["ls-remote", "--heads", "--", remote_url.trim()],
            data_dir,
            TRANSFER_TIMEOUT,
        )?;
        if heads.exit_code != 0 {
            return Err(BridgeError::new(
                "GIT_CLONE_FAILED",
                format!(
                    "读取远程仓库分支失败（退出码 {}）：{}",
                    heads.exit_code,
                    sanitize(&heads.stderr)
                ),
            ));
        }
        let branches: Vec<String> = heads
            .stdout
            .lines()
            .filter_map(|line| line.split('\t').nth(1))
            .filter_map(|reference| reference.strip_prefix("refs/heads/"))
            .map(str::to_string)
            .collect();
        let Some(branch) = branches
            .iter()
            .find(|branch| branch.as_str() == "main")
            .or_else(|| branches.iter().find(|branch| branch.as_str() == "master"))
            .or_else(|| branches.first())
        else {
            return Err(BridgeError::new(
                "GIT_RESTORE_INVALID",
                "远程仓库是空的（需先用「推送到 Git」建立备份）",
            ));
        };
        progress("克隆远程仓库（浅克隆）…\n");
        let staging_text = staging.to_string_lossy().into_owned();
        let clone = git(
            &[
                "clone",
                "--depth",
                "1",
                "--branch",
                branch,
                "--",
                remote_url.trim(),
                &staging_text,
            ],
            data_dir,
            TRANSFER_TIMEOUT,
        )?;
        if clone.exit_code != 0 {
            return Err(BridgeError::new(
                "GIT_CLONE_FAILED",
                format!(
                    "克隆远程仓库失败（退出码 {}）：{}",
                    clone.exit_code,
                    sanitize(&clone.stderr)
                ),
            ));
        }
        let has_payload = staging.join("tools").is_dir()
            || staging.join("apps").is_dir()
            || staging.join("data").join("storage").is_dir()
            || staging.join("data").join("config.json").is_file();
        if !has_payload {
            return Err(BridgeError::new(
                "GIT_RESTORE_INVALID",
                "远程仓库中没有 QTools 备份数据（需先用「推送到 Git」建立备份）",
            ));
        }
        progress("固化快照并恢复（恢复前自动备份当前状态）…\n");
        let (snapshot, _, _) = backup.create_from_directory(&staging, Some("Git 恢复快照"))?;
        backup.restore(&snapshot)
    })();
    let _ = fs::remove_dir_all(&staging);
    result
}

fn ensure_identity(repo: &Path) -> BridgeResult<()> {
    let name = git(&["config", "user.name"], repo, GIT_TIMEOUT)?;
    if name.exit_code != 0 || name.stdout.trim().is_empty() {
        checked(
            repo,
            &["config", "user.name", "QTools Backup"],
            "设置仓库提交身份",
        )?;
    }
    let email = git(&["config", "user.email"], repo, GIT_TIMEOUT)?;
    if email.exit_code != 0 || email.stdout.trim().is_empty() {
        checked(
            repo,
            &["config", "user.email", "qtools@localhost"],
            "设置仓库提交邮箱",
        )?;
    }
    Ok(())
}

fn ensure_remote(repo: &Path, remote_url: &str) -> BridgeResult<()> {
    let current = git(&["remote", "get-url", "origin"], repo, GIT_TIMEOUT)?;
    if current.exit_code != 0 {
        checked(
            repo,
            &["remote", "add", "origin", remote_url],
            "登记远程仓库",
        )?;
    } else if current.stdout.trim() != remote_url {
        checked(
            repo,
            &["remote", "set-url", "origin", remote_url],
            "更新远程仓库地址",
        )?;
    }
    Ok(())
}

fn checked(repo: &Path, args: &[&str], action: &str) -> BridgeResult<()> {
    let result = git(args, repo, GIT_TIMEOUT)?;
    if result.exit_code != 0 {
        return Err(BridgeError::new(
            "GIT_COMMAND_FAILED",
            format!(
                "{action}失败（退出码 {}）：{}",
                result.exit_code,
                sanitize(&result.stderr)
            ),
        ));
    }
    Ok(())
}

fn sync_mirror(root: &Path, repo: &Path) -> BridgeResult<()> {
    if let Ok(entries) = fs::read_dir(repo) {
        for entry in entries.flatten() {
            let name = entry.file_name();
            if name == ".git" {
                continue;
            }
            let path = entry.path();
            if path.is_dir() {
                let _ = fs::remove_dir_all(&path);
            } else {
                let _ = fs::remove_file(&path);
            }
        }
    }
    for relative in enumerate_payload(root) {
        let source = root.join(relative.replace('/', &std::path::MAIN_SEPARATOR.to_string()));
        let destination = repo.join(relative.replace('/', &std::path::MAIN_SEPARATOR.to_string()));
        if let Some(parent) = destination.parent() {
            let _ = fs::create_dir_all(parent);
        }
        let _ = fs::copy(&source, &destination);
    }
    Ok(())
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

fn sanitize(text: &str) -> String {
    static PATTERN: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let pattern =
        PATTERN.get_or_init(|| regex::Regex::new(r"(https?://)[^/@\s]+@").expect("凭证脱敏正则"));
    let cleaned = pattern.replace_all(text, "$1***@").trim().to_string();
    if cleaned.chars().count() > 500 {
        format!("{}…", cleaned.chars().take(500).collect::<String>())
    } else {
        cleaned
    }
}

#[allow(dead_code)]
fn unused(_: PathBuf) {}
