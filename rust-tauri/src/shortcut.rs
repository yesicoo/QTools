//! 桌面快捷方式：在桌面写 `{工具名}.lnk`，目标为当前可执行文件 + `--open-tool <id>`。
//! 采用系统自带的 WScript.Shell 创建（COM 调用放在 PowerShell 进程内），
//! 不改写同名他人快捷方式，冲突时顺延「名称 (2).lnk」。

use std::process::Command;
use std::sync::Arc;

use base64::Engine;
use serde_json::Value;
use serde_json::json;

use crate::error::{BridgeError, BridgeResult};
use crate::state::AppState;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

pub fn create_tool_shortcut(state: &Arc<AppState>, tool_id: &str) -> BridgeResult<Value> {
    if !cfg!(windows) {
        return Err(BridgeError::new(
            "PLATFORM_NOT_SUPPORTED",
            "桌面快捷方式仅支持 Windows",
        ));
    }
    let tool = state
        .tools
        .find(tool_id)
        .ok_or_else(|| BridgeError::new("TOOL_NOT_FOUND", format!("未找到工具 '{tool_id}'")))?;
    let exe = std::env::current_exe().map_err(|error| {
        BridgeError::new("SHORTCUT_FAILED", format!("获取程序路径失败：{error}"))
    })?;
    let exe = exe.to_string_lossy().into_owned();
    let workdir = std::path::Path::new(&exe)
        .parent()
        .map(|path| path.to_string_lossy().into_owned())
        .unwrap_or_default();
    let base_name = sanitize_shortcut_name(tool.name());
    let arguments = format!("--open-tool {tool_id}");

    let script = format!(
        r#"$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$desktop = [Environment]::GetFolderPath('Desktop')
$target = {target}
$arguments = {arguments}
$base = {base}
$path = $null
for ($i = 1; $i -lt 100; $i++) {{
  if ($i -eq 1) {{ $leaf = $base + '.lnk' }} else {{ $leaf = $base + ' (' + $i + ').lnk' }}
  $candidate = Join-Path $desktop $leaf
  if (Test-Path -LiteralPath $candidate) {{
    try {{
      $existing = (New-Object -ComObject WScript.Shell).CreateShortcut($candidate)
      if ($existing.TargetPath -eq $target -and $existing.Arguments -eq $arguments) {{
        $path = $candidate
        break
      }}
    }} catch {{ }}
    continue
  }}
  $path = $candidate
  break
}}
if (-not $path) {{ throw '快捷方式名称冲突过多' }}
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
$shortcut.TargetPath = $target
$shortcut.Arguments = $arguments
$shortcut.WorkingDirectory = {workdir}
$shortcut.IconLocation = $target + ',0'
$shortcut.Save()
Write-Output $path
"#,
        target = ps_quote(&exe),
        arguments = ps_quote(&arguments),
        base = ps_quote(&base_name),
        workdir = ps_quote(&workdir),
    );

    let encoded = encode_power_shell(&script);
    let output = Command::new("powershell")
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-EncodedCommand",
            &encoded,
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|error| {
            BridgeError::new("SHORTCUT_FAILED", format!("调用 PowerShell 失败：{error}"))
        })?;
    if !output.status.success() {
        let message = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(BridgeError::new(
            "SHORTCUT_FAILED",
            format!(
                "创建桌面快捷方式失败：{}",
                if message.is_empty() {
                    "未知错误"
                } else {
                    &message
                }
            ),
        ));
    }
    let path = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if path.is_empty() {
        return Err(BridgeError::new(
            "SHORTCUT_FAILED",
            "创建桌面快捷方式失败：未返回路径",
        ));
    }
    crate::logging::info(&format!("已创建桌面快捷方式：{path}"));
    Ok(json!({
        "path": path,
        "fileName": std::path::Path::new(&path)
            .file_name()
            .map(|value| value.to_string_lossy().into_owned())
            .unwrap_or_default()
    }))
}

fn ps_quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn encode_power_shell(script: &str) -> String {
    let mut bytes = Vec::with_capacity(script.len() * 2);
    for unit in script.encode_utf16() {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn sanitize_shortcut_name(name: &str) -> String {
    let mut result: String = name
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
    result = result.trim().trim_end_matches(['.', ' ']).to_string();
    if result.chars().count() > 80 {
        result = result.chars().take(80).collect::<String>();
        result = result.trim_end_matches(['.', ' ']).to_string();
    }
    if result.is_empty() {
        result = "QTools 工具".into();
    }
    let upper = result.to_ascii_uppercase();
    let reserved = [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
        "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
    if reserved.contains(&upper.as_str()) {
        result.push('_');
    }
    result
}

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(test)]
mod tests {
    use super::sanitize_shortcut_name;

    #[test]
    fn shortcut_name_is_sanitized_and_reserved_names_are_escaped() {
        assert_eq!(sanitize_shortcut_name("a/b:c*?"), "a_b_c__");
        assert_eq!(sanitize_shortcut_name("con"), "con_");
        assert_eq!(sanitize_shortcut_name("  "), "QTools 工具");
    }
}
