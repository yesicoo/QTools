use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::Path;
use std::sync::OnceLock;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::error::{BridgeError, BridgeResult};
use crate::tools::{
    LoadedTool, ToolManifest, ToolRegistry, ToolVersionStore, ToolWindowSettings,
    validate_manifest, write_manifest,
};

pub const SYSTEM_PROMPT: &str = r#"你是 QTools 桌面小工具宿主的工具生成器。用户会用自然语言描述需求，你必须只输出一个 JSON 对象，不要有任何解释文字或 Markdown 代码围栏。

输出格式（严格遵守）：
{
  "manifest": {
    "id": "小写字母/数字/短横线，1-63 字符，首尾不能是短横线",
    "name": "工具显示名",
    "version": "1.0.0",
    "description": "一句话描述",
    "entry": "index.html",
    "permissions": ["storage"/"ai"/"media"/"files" 的子集，按需最小化],
    "window": { "width": 520, "height": 720, "resizable": true },
    "createdAt": "ISO-8601 时间",
    "allowEval": false,
    "csp": null
  },
  "indexHtml": "完整的单文件 HTML（UTF-8，内联 CSS/JS）"
}

===== 可用能力（window.host，由宿主注入，页面可直接使用）=====

1) storage —— 需要 "storage" 权限。键值存储，按工具隔离。
   window.host.storage.get(key)        -> Promise<{ found, value } | null>
   window.host.storage.set(key, value) -> Promise<{ ok: true }>   value 为任意 JSON
   window.host.storage.remove(key)     -> Promise<{ ok: true }>
   window.host.storage.list()          -> Promise<{ keys: string[] }>
   key 只允许字母数字 . _ - ，最长 128；单个 value 不超过 256KB。

2) ai —— 需要 "ai" 权限。宿主代理的 AI 对话，Key 由宿主保管，页面拿不到。
   window.host.ai.chat({ messages: [{role:"user"|"system"|"assistant", content:"..."}], temperature })
     -> Promise<{ content }>

3) files —— 需要 "files" 权限。二进制文件存取，存放在 tools/{工具id}/files/ 下。
   window.host.files.save({ name, dataBase64, contentType? }) -> Promise<{ name, sizeBytes, url }>
   window.host.files.list()    -> Promise<{ files: [{ name, sizeBytes, modifiedUtc }] }>
   window.host.files.read({ name })    -> Promise<{ name, contentType, dataBase64 }>
   window.host.files.delete({ name })  -> Promise<{ deleted }>
   返回的 url 形如 /tools/{工具id}/files/xxx，可直接用于 <audio src> <img src> <a href>。
   允许的扩展名：webm wav mp3 ogg m4a aac flac opus mp4 mov avi mkv png jpg jpeg gif webp bmp svg pdf txt csv json zip。
   单个文件 50MB，单工具总计 200MB。文件名只能是字母数字 . _ - ，不能带路径。

4) media —— 需要 "media" 权限。声明后宿主会为该工具开启麦克风/摄像头/屏幕捕获，
   页面可直接使用标准 Web API（无需再请求权限）：
   navigator.mediaDevices.getUserMedia({ audio: true })      // 麦克风
   navigator.mediaDevices.getUserMedia({ video: true })      // 摄像头
   navigator.mediaDevices.getDisplayMedia({ video: true })   // 屏幕
   配合 new MediaRecorder(stream) 录制，停止后得到 Blob。

===== 典型配方：录音工具 =====
- manifest.permissions 必须包含 "media" 和 "files"。
- getUserMedia({audio:true}) 拿流 → new MediaRecorder(stream, {mimeType:'audio/webm'}) 录制；
  收集 chunk，停止时组成 Blob；
- 用 FileReader.readAsDataURL 得到 base64（去掉 "data:...;base64," 前缀），调 window.host.files.save；
- 用返回的 url 作为 <audio controls src> 播放；files.list() 展示历史录音。
- DataURL 体积比原文件大约 33%，录音较长要提示用户；可先用 URL.createObjectURL 本地试听，
  仅在用户点击「保存」时才转 base64 上传。

===== 硬性规则（违反任何一条都算失败）=====
1. permissions 只能从 ["storage","ai","media","files"] 中选择，只声明真正用到的。
   用到 getUserMedia/getDisplayMedia 就必须有 media；用到 window.host.files 就必须有 files。
2. indexHtml 中禁止出现 eval(、new Function、动态 import(。
3. indexHtml 中禁止硬编码任何 API Key 或令牌。
4. 本地键值存储用 window.host.storage；二进制文件用 window.host.files；
   需要 AI 能力时优先用 window.host.ai.chat（Key 由宿主保管，页面拿不到）；硬件捕获用 media 权限 + 标准 Web API。
5. window.host 由宿主注入，页面可直接使用；不要自己定义或引用其他桥接对象。
6. 单文件 HTML（全部 CSS/JS 内联，目的：支持宿主的导入/导出与备份）、无构建步骤；
   允许引用 CDN 或外部资源，允许 fetch/XHR/WebSocket 访问外部接口，请求失败时要有降级提示。
7. 必须同时适配宿主浅色 / 深色主题（宿主会在 <html> 上注入 data-theme="light|dark"，跟随系统时不注入）。
   用 CSS 变量定义两套颜色，按以下模式写样式，body / 输入框 / 按钮 / 卡片等所有背景与文字颜色都走变量，
   禁止写死单一浅色配色（否则深色下不可用）：
   :root { color-scheme: light; --bg:#f7f8fa; --text:#181b20; }
   :root[data-theme="dark"] { color-scheme: dark; --bg:#101216; --text:#e8eaed; }
   @media (prefers-color-scheme: dark) {
     :root:not([data-theme]) { color-scheme: dark; --bg:#101216; --text:#e8eaed; }
   }
"#;

pub const GENERATE_REQUIREMENTS: &str = r#"【生成要求（QTools 宿主规范，必须全部满足）】
1. 成品必须是单个 index.html 文件，全部 CSS / JS 内联——目的在于支持宿主的导入 / 导出与备份，不限制联网。
2. 允许引用 CDN 或外部资源（脚本、样式、字体、图片），允许 fetch / XMLHttpRequest / WebSocket 访问外部接口；请求失败时要有降级提示，不要让页面卡死或白屏。
3. 本地数据持久化用 window.host.storage，二进制文件用 window.host.files，需要 AI 能力时优先用 window.host.ai（Key 由宿主保管，页面拿不到）；禁止在代码里硬编码任何 API Key 或令牌。
4. 界面文案用简体中文，图标用内联 SVG 或字符；禁止 eval / new Function / 动态 import。
5. 布局自适应 manifest.window 声明的尺寸并随窗口缩放；window.width/height 必须是 320~3840 的整数（常用 520×720、resizable=true）。
6. permissions 按实际用量最小化声明；allowEval 固定 false、csp 固定 null。
7. 交互完整：输入校验、空态、成功/失败反馈都要有，错误不静默。
8. 界面必须适配宿主深浅主题：CSS 变量 + :root[data-theme="dark"] + prefers-color-scheme 兜底（见系统提示第 7 条），
   禁止只写一套浅色配色。

【我的需求】
"#;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SecurityFinding {
    pub severity: i32,
    pub code: String,
    pub message: String,
}

pub struct GeneratedResult {
    pub manifest: ToolManifest,
    pub index_html: String,
    pub findings: Vec<SecurityFinding>,
    pub has_fatal: bool,
    pub requires_explicit_acceptance: bool,
}

impl GeneratedResult {
    pub fn to_view(&self) -> Value {
        serde_json::json!({
            "manifest": self.manifest,
            "indexHtml": self.index_html,
            "findings": self.findings,
            "hasFatal": self.has_fatal,
            "requiresExplicitAcceptance": self.requires_explicit_acceptance
        })
    }
}

#[derive(Debug)]
pub struct AiOutput {
    pub manifest: Value,
    pub index_html: String,
}

/// AI 输出容错解析：剥围栏整体解析优先，括号配对兜底。
pub fn parse_ai_output(text: &str) -> BridgeResult<AiOutput> {
    if text.trim().is_empty() {
        return Err(BridgeError::new("AI_OUTPUT_UNPARSEABLE", "AI 返回了空内容"));
    }
    let unfenced = strip_code_fence(text);
    let candidate = if try_parse(&unfenced) {
        unfenced
    } else if let Some(balanced) = extract_balanced_object(text) {
        if try_parse(&balanced) {
            balanced
        } else {
            return Err(BridgeError::new(
                "AI_OUTPUT_UNPARSEABLE",
                "AI 输出中找不到 JSON 对象",
            ));
        }
    } else if unfenced != text {
        if let Some(inner) = extract_balanced_object(&unfenced) {
            if try_parse(&inner) {
                inner
            } else {
                return Err(BridgeError::new(
                    "AI_OUTPUT_UNPARSEABLE",
                    "AI 输出中找不到 JSON 对象",
                ));
            }
        } else {
            return Err(BridgeError::new(
                "AI_OUTPUT_UNPARSEABLE",
                "AI 输出中找不到 JSON 对象",
            ));
        }
    } else {
        return Err(BridgeError::new(
            "AI_OUTPUT_UNPARSEABLE",
            "AI 输出中找不到 JSON 对象",
        ));
    };

    let value: Value = serde_json::from_str(&candidate).map_err(|error| {
        BridgeError::new(
            "AI_OUTPUT_UNPARSEABLE",
            format!("AI 输出的 JSON 无法解析：{error}"),
        )
    })?;
    let manifest = value
        .get("manifest")
        .filter(|value| value.is_object())
        .cloned()
        .ok_or_else(|| BridgeError::new("AI_OUTPUT_UNPARSEABLE", "AI 输出缺少 manifest 字段"))?;
    let index_html = value
        .get("indexHtml")
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| BridgeError::new("AI_OUTPUT_UNPARSEABLE", "AI 输出缺少 indexHtml 字段"))?
        .to_string();
    Ok(AiOutput {
        manifest,
        index_html,
    })
}

fn try_parse(text: &str) -> bool {
    let Ok(value) = serde_json::from_str::<Value>(text) else {
        return false;
    };
    value.get("manifest").is_some_and(Value::is_object)
        && value
            .get("indexHtml")
            .and_then(Value::as_str)
            .is_some_and(|html| !html.trim().is_empty())
}

fn strip_code_fence(text: &str) -> String {
    let trimmed = text.trim();
    if !trimmed.starts_with("```") {
        return text.to_string();
    }
    let Some(first_line_end) = trimmed.find('\n') else {
        return text.to_string();
    };
    let Some(last_fence) = trimmed.rfind("```") else {
        return text.to_string();
    };
    if last_fence <= first_line_end {
        return text.to_string();
    }
    trimmed[first_line_end + 1..last_fence].trim().to_string()
}

fn extract_balanced_object(text: &str) -> Option<String> {
    let start = text.find('{')?;
    let bytes = text.as_bytes();
    let mut depth = 0i32;
    let mut in_string = false;
    let mut escaped = false;
    for (index, byte) in bytes.iter().enumerate().skip(start) {
        if in_string {
            if escaped {
                escaped = false;
            } else if *byte == b'\\' {
                escaped = true;
            } else if *byte == b'"' {
                in_string = false;
            }
            continue;
        }
        match byte {
            b'"' => in_string = true,
            b'{' => depth += 1,
            b'}' => {
                depth -= 1;
                if depth == 0 {
                    return Some(text[start..=index].to_string());
                }
            }
            _ => {}
        }
    }
    None
}

pub fn manifest_from_value(value: &Value) -> BridgeResult<ToolManifest> {
    serde_json::from_value(value.clone()).map_err(|error| {
        BridgeError::new(
            "MANIFEST_INVALID",
            format!("生成的 manifest 不是合法 JSON：{error}"),
        )
    })
}

pub fn scan(manifest: &ToolManifest, html: &str) -> (Vec<SecurityFinding>, bool) {
    let mut findings = Vec::new();
    for error in validate_manifest(manifest, &manifest.id) {
        findings.push(SecurityFinding {
            severity: 0,
            code: "MANIFEST_INVALID".into(),
            message: error,
        });
    }
    let rules: Vec<(i32, &str, &str, &str)> = vec![
        (
            1,
            "DYNAMIC_CODE",
            "检测到 eval(——动态执行代码",
            r"\beval\s*\(",
        ),
        (
            1,
            "DYNAMIC_CODE",
            "检测到 new Function(——动态执行代码",
            r"\bnew\s+Function\s*\(",
        ),
        (
            1,
            "DYNAMIC_CODE",
            "检测到动态 import(——运行时加载模块",
            r"\bimport\s*\(",
        ),
        (
            2,
            "REMOTE_SCRIPT",
            "检测到远程 script 标签——引用了外部脚本，请确认来源可信",
            r#"(?i)<script[^>]+src\s*=\s*["']?\s*(?:https?:)?//"#,
        ),
        (
            2,
            "EXTERNAL_NETWORK",
            "检测到 fetch 外部地址——该工具会访问网络",
            r#"(?i)\bfetch\s*\(\s*["']\s*(?:https?:)?//"#,
        ),
        (
            2,
            "EXTERNAL_NETWORK",
            "检测到 XMLHttpRequest——该工具会访问网络",
            r"(?i)\bXMLHttpRequest\b",
        ),
        (
            2,
            "EXTERNAL_NETWORK",
            "检测到 WebSocket(——该工具会建立外部长连接",
            r"(?i)\bWebSocket\s*\(",
        ),
        (
            2,
            "EXTERNAL_NETWORK",
            "检测到 navigator.sendBeacon(——该工具会向外部上报数据",
            r"(?i)\bnavigator\s*\.\s*sendBeacon\s*\(",
        ),
        (
            2,
            "REMOTE_IFRAME",
            "检测到远程 iframe——内容来自外部站点",
            r#"(?i)<\s*iframe[^>]+src\s*=\s*["']?\s*(?:https?:)?//"#,
        ),
        (
            2,
            "DOCUMENT_WRITE",
            "检测到 document.write(——建议改用 DOM API",
            r"(?i)\bdocument\s*\.\s*write\s*\(",
        ),
        (2, "SRCDOC", "检测到 srcdoc 属性", r"(?i)\bsrcdoc\s*="),
        (
            1,
            "HARDCODED_KEY",
            "检测到疑似 OpenAI 风格 API Key（sk-...）",
            r"sk-[A-Za-z0-9_\-]{16,}",
        ),
        (
            1,
            "HARDCODED_KEY",
            "检测到疑似硬编码 apiKey/accessToken",
            r#"(?i)\b(?:api[_-]?key|apikey|access[_-]?token)\b\s*[:=]\s*["'][^"']{12,}["']"#,
        ),
        (
            1,
            "HARDCODED_KEY",
            "检测到疑似 AWS Access Key（AKIA...）",
            r"\bAKIA[0-9A-Z]{16}\b",
        ),
        (
            1,
            "HARDCODED_KEY",
            "检测到疑似 Bearer 令牌",
            r"(?i)\bbearer\s+[A-Za-z0-9._\-]{20,}",
        ),
    ];
    for (severity, code, message, pattern) in rules {
        if regex_is_match(pattern, html) {
            findings.push(SecurityFinding {
                severity,
                code: code.into(),
                message: message.into(),
            });
        }
    }
    for permission in &manifest.permissions {
        let (code, message) = match permission.as_str() {
            "ai" => (
                "PERMISSION_AI",
                "manifest 声明了 ai 权限：该工具可调用宿主 AI 接口（消耗你的配额）",
            ),
            "media" => (
                "PERMISSION_MEDIA",
                "manifest 声明了 media 权限：该工具可使用麦克风 / 摄像头 / 屏幕捕获",
            ),
            "files" => (
                "PERMISSION_FILES",
                "manifest 声明了 files 权限：该工具可在自己的目录内读写二进制文件",
            ),
            _ => continue,
        };
        findings.push(SecurityFinding {
            severity: 2,
            code: code.into(),
            message: message.into(),
        });
    }
    if manifest.allow_eval {
        findings.push(SecurityFinding {
            severity: 1,
            code: "ALLOW_EVAL".into(),
            message: "manifest 启用了 allowEval：CSP 将放开 'unsafe-eval'，允许 eval/new Function"
                .into(),
        });
    }
    if manifest
        .csp
        .as_deref()
        .is_some_and(|value| !value.trim().is_empty())
    {
        findings.push(SecurityFinding {
            severity: 1,
            code: "CSP_OVERRIDE".into(),
            message: "manifest 自定义了 CSP：可能削弱 script-src / connect-src 限制".into(),
        });
    }

    let declared: HashSet<String> = manifest
        .permissions
        .iter()
        .map(|p| p.to_ascii_lowercase())
        .collect();
    let checks = [
        (
            "media",
            r"\bget(?:User|Display)Media\s*\(",
            "页面使用了 getUserMedia/getDisplayMedia，但 manifest 未声明 media 权限",
        ),
        (
            "files",
            r"\bwindow\s*\.\s*host\s*\.\s*files\s*\.",
            "页面使用了 window.host.files，但 manifest 未声明 files 权限",
        ),
        (
            "storage",
            r"\bwindow\s*\.\s*host\s*\.\s*storage\s*\.",
            "页面使用了 window.host.storage，但 manifest 未声明 storage 权限",
        ),
        (
            "ai",
            r"\bwindow\s*\.\s*host\s*\.\s*ai\s*\.",
            "页面使用了 window.host.ai，但 manifest 未声明 ai 权限",
        ),
    ];
    for (permission, pattern, message) in checks {
        if regex_is_match(pattern, html) && !declared.contains(permission) {
            findings.push(SecurityFinding {
                severity: 0,
                code: "PERMISSION_MISSING".into(),
                message: message.into(),
            });
        }
    }

    findings.sort_by(|left, right| {
        left.severity
            .cmp(&right.severity)
            .then(left.code.cmp(&right.code))
    });
    let has_fatal = findings.iter().any(|finding| finding.severity == 0);
    (findings, has_fatal)
}

fn regex_is_match(pattern: &str, text: &str) -> bool {
    static CACHE: OnceLock<parking_lot::Mutex<HashMap<String, regex::Regex>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| parking_lot::Mutex::new(HashMap::new()));
    let mut cache = cache.lock();
    let regex = cache
        .entry(pattern.to_string())
        .or_insert_with(|| regex::Regex::new(pattern).expect("安全审查正则"));
    regex.is_match(text)
}

pub fn detect_permissions(html: &str) -> Vec<String> {
    let mut permissions = Vec::new();
    if regex_is_match(r"\bwindow\s*\.\s*host\s*\.\s*storage\s*\.", html) {
        permissions.push("storage".into());
    }
    if regex_is_match(r"\bwindow\s*\.\s*host\s*\.\s*ai\s*\.", html) {
        permissions.push("ai".into());
    }
    if regex_is_match(r"\bwindow\s*\.\s*host\s*\.\s*files\s*\.", html) {
        permissions.push("files".into());
    }
    if regex_is_match(r"\bgetUserMedia\s*\(", html)
        || regex_is_match(r"\bgetDisplayMedia\s*\(", html)
    {
        permissions.push("media".into());
    }
    permissions
}

pub fn detect_external_assets(html: &str) -> Vec<String> {
    static PATTERN: OnceLock<regex::Regex> = OnceLock::new();
    let pattern = PATTERN.get_or_init(|| {
        regex::Regex::new(r#"(?i)(?:src|href)\s*=\s*["'](?P<path>[^"']+)["']"#)
            .expect("资源引用正则")
    });
    let mut result = Vec::new();
    for capture in pattern.captures_iter(html) {
        let Some(value) = capture.name("path").map(|value| value.as_str()) else {
            continue;
        };
        if value.is_empty()
            || value.starts_with('#')
            || value.to_ascii_lowercase().starts_with("data:")
            || value.starts_with("http://")
            || value.starts_with("https://")
            || value.starts_with("//")
        {
            continue;
        }
        if !result.contains(&value.to_string()) {
            result.push(value.to_string());
        }
    }
    result
}

pub fn generate_tool_id(registry: &ToolRegistry, name: &str, prefix: &str) -> String {
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
        use sha2::{Digest, Sha256};
        let hash = Sha256::digest(name.as_bytes());
        slug = format!("{prefix}{}", hex_prefix(&hash, 8));
    }
    if !registry.contains(&slug) {
        return slug;
    }
    for suffix in 2..1000 {
        let next = format!("{slug}-{suffix}");
        if !registry.contains(&next) {
            return next;
        }
    }
    use sha2::{Digest, Sha256};
    let hash = Sha256::digest(
        format!(
            "{slug}{}",
            chrono::Utc::now().timestamp_nanos_opt().unwrap_or(0)
        )
        .as_bytes(),
    );
    format!("{slug}-{}", hex_prefix(&hash, 6))
}

fn hex_prefix(bytes: &[u8], length: usize) -> String {
    bytes
        .iter()
        .flat_map(|byte| format!("{byte:02x}").chars().collect::<Vec<_>>())
        .take(length)
        .collect()
}

pub fn next_version(versions: &[crate::tools::ToolVersion], bump: &str) -> String {
    let latest = versions
        .iter()
        .map(|version| parse_version(&version.version))
        .max()
        .unwrap_or((1, 0, 0));
    match bump {
        "major" => format!("{}.0.0", latest.0 + 1),
        "minor" => format!("{}.{}.0", latest.0, latest.1 + 1),
        _ => format!("{}.{}.{}", latest.0, latest.1, latest.2 + 1),
    }
}

fn parse_version(version: &str) -> (u64, u64, u64) {
    let mut parts = version
        .split('-')
        .next()
        .unwrap_or_default()
        .split('.')
        .map(|part| part.parse::<u64>().unwrap_or(0));
    (
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
    )
}

pub fn ensure_save_allowed(
    findings: &[SecurityFinding],
    accept_high_severity: bool,
) -> BridgeResult<()> {
    if let Some(fatal) = findings.iter().find(|finding| finding.severity == 0) {
        return Err(BridgeError::new(
            "MANIFEST_INVALID",
            format!("生成内容存在致命问题，已拒绝保存：{}", fatal.message),
        ));
    }
    if let Some(high) = findings.iter().find(|finding| finding.severity == 1) {
        if !accept_high_severity {
            return Err(BridgeError::new(
                "CONTENT_SECURITY_VIOLATION",
                format!(
                    "生成内容包含高危项（{}: {}），需显式确认后才能保存",
                    high.code, high.message
                ),
            ));
        }
    }
    Ok(())
}

pub struct CreateDeps<'a> {
    pub registry: &'a ToolRegistry,
    pub tools_root: &'a Path,
}

pub fn create_tool(
    deps: CreateDeps<'_>,
    manifest: &ToolManifest,
    index_html: &str,
    findings: &[SecurityFinding],
    accept_high_severity: bool,
) -> BridgeResult<String> {
    ensure_save_allowed(findings, accept_high_severity)?;
    let tool_id = manifest.id.clone();
    if deps.registry.find(&tool_id).is_some() {
        return Err(BridgeError::new(
            "TOOL_ALREADY_EXISTS",
            format!("工具 ID 已存在：{tool_id}"),
        ));
    }
    let directory = deps.tools_root.join(&tool_id);
    if directory.join("manifest.json").is_file() {
        return Err(BridgeError::new(
            "TOOL_ALREADY_EXISTS",
            format!("工具目录已存在：{tool_id}"),
        ));
    }
    fs::create_dir_all(&directory)?;
    let mut saved_manifest = manifest.clone();
    saved_manifest.created_at = chrono::Local::now().to_rfc3339();
    write_manifest(&directory, &saved_manifest)?;
    let entry = if saved_manifest.entry.trim().is_empty() {
        "index.html"
    } else {
        saved_manifest.entry.as_str()
    };
    let entry_path = directory.join(entry);
    if let Some(parent) = entry_path.parent() {
        fs::create_dir_all(parent)?;
    }
    write_atomic(&entry_path, index_html)?;
    deps.registry.reload(&tool_id).ok_or_else(|| {
        BridgeError::new(
            "MANIFEST_INVALID",
            "工具已写入磁盘但重新扫描失败，请手动检查",
        )
    })?;
    Ok(tool_id)
}

pub fn apply_update(
    registry: &ToolRegistry,
    tool: &LoadedTool,
    manifest: &ToolManifest,
    index_html: &str,
    findings: &[SecurityFinding],
    accept_high_severity: bool,
) -> BridgeResult<LoadedTool> {
    ensure_save_allowed(findings, accept_high_severity)?;
    let version = manifest.version.clone();
    let snapshot = ToolVersionStore::version_dir(&tool.directory, &version);
    if snapshot.exists() {
        return Err(BridgeError::new(
            "VERSION_EXISTS",
            format!("版本 {version} 已存在，请改换递增粒度"),
        ));
    }
    ToolVersionStore::snapshot(
        &tool.directory,
        &tool.manifest.version,
        Some("更新前自动备份"),
    )?;
    fs::create_dir_all(&snapshot)?;
    write_atomic(&snapshot.join("index.html"), index_html)?;
    write_manifest(&snapshot, manifest)?;
    registry.switch_version(&tool.manifest.id, &version)
}

pub struct ImportResultView {
    pub tool_id: String,
    pub tool_name: String,
    pub url: String,
    pub saved: bool,
    pub permissions: Vec<String>,
    pub findings: Vec<SecurityFinding>,
    pub has_fatal: bool,
    pub requires_explicit_acceptance: bool,
    pub external_assets: Vec<String>,
}

pub fn import_html(
    registry: &ToolRegistry,
    tools_root: &Path,
    name: &str,
    html: &str,
    description: Option<&str>,
    category: Option<&str>,
    glyph: Option<&str>,
    accept_high_severity: bool,
) -> BridgeResult<ImportResultView> {
    let name = name.trim();
    if name.is_empty() {
        return Err(BridgeError::new("IMPORT_NAME_REQUIRED", "请填写工具名称"));
    }
    if name.chars().count() > 60 {
        return Err(BridgeError::new(
            "IMPORT_NAME_TOO_LONG",
            "工具名称不超过 60 字符",
        ));
    }
    if html.trim().is_empty() {
        return Err(BridgeError::new("IMPORT_HTML_REQUIRED", "HTML 内容为空"));
    }
    if html.len() > 512 * 1024 {
        return Err(BridgeError::new(
            "IMPORT_HTML_TOO_LARGE",
            "HTML 文件过大，上限 512 KB",
        ));
    }
    let description = description.map(str::trim).filter(|value| !value.is_empty());
    let category = category.map(str::trim).filter(|value| !value.is_empty());
    let glyph = glyph.map(str::trim).filter(|value| !value.is_empty());
    let permissions = detect_permissions(html);
    let external_assets = detect_external_assets(html);
    let manifest = ToolManifest {
        id: generate_tool_id(registry, name, "imported-"),
        name: name.to_string(),
        version: "1.0.0".into(),
        description: description.map(str::to_string),
        entry: "index.html".into(),
        permissions,
        window: ToolWindowSettings {
            width: 960,
            height: 720,
            resizable: true,
        },
        created_at: chrono::Local::now().to_rfc3339(),
        allow_eval: false,
        csp: None,
        category: category.map(str::to_string),
        glyph: glyph.map(str::to_string),
    };
    let (findings, has_fatal) = scan(&manifest, html);
    if has_fatal {
        let reason = findings
            .iter()
            .filter(|finding| finding.severity == 0)
            .map(|finding| finding.message.as_str())
            .collect::<Vec<_>>()
            .join("；");
        return Err(BridgeError::new(
            "IMPORT_REJECTED",
            format!("内容未通过安全审查：{reason}"),
        ));
    }
    let requires_acceptance = findings.iter().any(|finding| finding.severity == 1);
    if requires_acceptance && !accept_high_severity {
        return Ok(ImportResultView {
            tool_id: manifest.id.clone(),
            tool_name: name.to_string(),
            url: String::new(),
            saved: false,
            permissions: manifest.permissions.clone(),
            findings,
            has_fatal: false,
            requires_explicit_acceptance: true,
            external_assets,
        });
    }
    let directory = tools_root.join(&manifest.id);
    fs::create_dir_all(&directory)?;
    write_atomic(&directory.join("index.html"), html)?;
    write_manifest(&directory, &manifest)?;
    let loaded = registry
        .reload(&manifest.id)
        .ok_or_else(|| BridgeError::new("IMPORT_REJECTED", "工具未通过运行时校验"))?;
    if !loaded.is_valid {
        return Err(BridgeError::new(
            "IMPORT_REJECTED",
            format!("工具未通过运行时校验：{}", loaded.errors.join("；")),
        ));
    }
    Ok(ImportResultView {
        tool_id: manifest.id.clone(),
        tool_name: name.to_string(),
        url: format!("/tools/{}/index.html", manifest.id),
        saved: true,
        permissions: manifest.permissions.clone(),
        findings,
        has_fatal: false,
        requires_explicit_acceptance: false,
        external_assets,
    })
}

fn write_atomic(path: &Path, content: &str) -> BridgeResult<()> {
    let temp = path.with_extension(format!(
        "{}.tmp",
        path.extension()
            .map(|value| value.to_string_lossy())
            .unwrap_or_default()
    ));
    fs::write(&temp, content).and_then(|_| fs::rename(&temp, path))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::parse_ai_output;

    #[test]
    fn parses_fenced_ai_output_with_braces_in_html() {
        let text = "```json\n{\"manifest\":{\"id\":\"demo\",\"name\":\"Demo\"},\"indexHtml\":\"<html><style>a{color:red}</style><script>const x = { a: 1 };</script></html>\"}\n```";
        let parsed = parse_ai_output(text).expect("应能解析");
        assert_eq!(
            parsed.index_html,
            "<html><style>a{color:red}</style><script>const x = { a: 1 };</script></html>"
        );
    }

    #[test]
    fn rejects_output_without_html() {
        let error = parse_ai_output("{\"manifest\":{}}").unwrap_err();
        assert_eq!(error.code, "AI_OUTPUT_UNPARSEABLE");
    }
}
