use std::fs::OpenOptions;
use std::io::Write;
use std::sync::OnceLock;

use crate::paths::AppPaths;

static LOG_FILE: OnceLock<std::path::PathBuf> = OnceLock::new();

pub fn initialize(paths: &AppPaths, dev_mode: bool) {
    let _ = dev_mode;
    let _ = std::fs::create_dir_all(paths.logs_dir());
    let _ = LOG_FILE.set(paths.log_file());
}

fn write(level: &str, message: &str) {
    let line = format!(
        "{} [{level}] {}\n",
        chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"),
        redact(message)
    );
    print!("{line}");
    if let Some(path) = LOG_FILE.get() {
        let _ = OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .and_then(|mut file| file.write_all(line.as_bytes()));
    }
}

pub fn info(message: &str) {
    write("INFO", message);
}

pub fn warn(message: &str) {
    write("WARN", message);
}

pub fn error(message: &str) {
    write("ERROR", message);
}

/// 日志脱敏：绝不记录 API Key、Bearer Token 或常见密钥字段。
pub fn redact(value: &str) -> String {
    static PATTERN: OnceLock<regex::Regex> = OnceLock::new();
    let pattern = PATTERN.get_or_init(|| {
        regex::Regex::new(
            r#"(?i)(api[_-]?key|authorization|bearer|token|secret)\s*[:=]\s*["']?[A-Za-z0-9_\-.+/=]{6,}"#,
        )
        .unwrap_or_else(|_| regex::Regex::new("$^").expect("日志脱敏正则"))
    });
    pattern.replace_all(value, "$1=***").into_owned()
}
