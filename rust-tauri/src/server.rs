use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use crate::assets::Assets;
use crate::config::{explicit_theme, is_dark_now};
use crate::logging;
use crate::state::AppState;

const HOST_UI_PREFIX: &str = "/__host__";
const TOOLS_PREFIX: &str = "/tools/";
const MAX_HEADER_BYTES: usize = 16 * 1024;
const MAX_REQUEST_TARGET: usize = 4096;
const MAX_FILE_BYTES: u64 = 8 * 1024 * 1024;

const HOST_UI_CSP: &str = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'self'; base-uri 'none'; object-src 'none'; form-action 'none'";

const DEFAULT_TOOL_CSP: &str = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-src 'self' https:; script-src 'self' 'unsafe-inline' https:; style-src 'self' 'unsafe-inline' https:; img-src 'self' data: blob: https:; font-src 'self' data: https:; media-src 'self' data: blob: https:; connect-src 'self' https: wss:; form-action 'none'; frame-ancestors 'self'";

const TOOL_CSP_ALLOW_EVAL: &str = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-src 'self' https:; script-src 'self' 'unsafe-inline' 'unsafe-eval' https:; style-src 'self' 'unsafe-inline' https:; img-src 'self' data: blob: https:; font-src 'self' data: https:; media-src 'self' data: blob: https:; connect-src 'self' https: wss:; form-action 'none'; frame-ancestors 'self'";

const TAURI_SHIM: &str = r#"(function () {
  "use strict";
  try {
    if (!window.__TAURI_INTERNALS__ || typeof window.__TAURI_INTERNALS__.invoke !== "function") { return; }
    window.external = {
      sendMessage: function (message) {
        try {
          var result = window.__TAURI_INTERNALS__.invoke("bridge_message", { message: String(message) });
          if (result && typeof result.catch === "function") { result.catch(function () { }); }
        } catch (error) { }
      },
      receiveMessage: function (callback) { window.__qtoolsReceive = callback; }
    };
  } catch (error) { }
})();"#;

pub struct LocalServer {
    pub port: u16,
}

impl LocalServer {
    pub fn start(state: Arc<AppState>, port: Option<u16>) -> std::io::Result<Self> {
        let listener = TcpListener::bind(("127.0.0.1", port.unwrap_or(0)))?;
        let port = listener.local_addr()?.port();
        state.set_port(port);
        std::thread::Builder::new()
            .name("qtools-http".into())
            .spawn(move || {
                for stream in listener.incoming() {
                    let Ok(stream) = stream else { continue };
                    let state = state.clone();
                    let _ = std::thread::Builder::new()
                        .name("qtools-http-conn".into())
                        .spawn(move || {
                            let _ = handle_connection(stream, &state);
                        });
                }
            })?;
        logging::info(&format!("本地服务已启动：http://127.0.0.1:{port}"));
        Ok(Self { port })
    }
}

struct Request {
    method: String,
    path: String,
    host: String,
    head_only: bool,
}

struct Response {
    status: u16,
    content_type: String,
    body: Vec<u8>,
    extra_headers: Vec<(String, String)>,
    suppress_body: bool,
}

impl Response {
    fn text(status: u16, text: &str) -> Self {
        Self {
            status,
            content_type: "text/plain; charset=utf-8".into(),
            body: text.as_bytes().to_vec(),
            extra_headers: Vec::new(),
            suppress_body: false,
        }
    }
}

fn handle_connection(mut stream: TcpStream, state: &AppState) -> std::io::Result<()> {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(15)));
    let _ = stream.set_write_timeout(Some(Duration::from_secs(15)));
    let Some(request) = read_request(&mut stream)? else {
        return Ok(());
    };
    let watch = std::time::Instant::now();
    let mut response = route(&request, state);
    response
        .extra_headers
        .push(("X-Content-Type-Options".into(), "nosniff".into()));
    response
        .extra_headers
        .push(("Referrer-Policy".into(), "no-referrer".into()));
    response
        .extra_headers
        .push(("Cache-Control".into(), "no-store".into()));
    response.extra_headers.push((
        "Content-Security-Policy".into(),
        csp_for(state, &request.path).into(),
    ));
    if request.method != "GET" && request.method != "HEAD" {
        response.status = 405;
        response.body = b"Method Not Allowed".to_vec();
        response.content_type = "text/plain; charset=utf-8".into();
        response
            .extra_headers
            .push(("Allow".into(), "GET, HEAD".into()));
    }
    response.suppress_body = request.head_only;
    write_response(&mut stream, &response)?;
    logging::info(&format!(
        "{} {} -> {} ({}ms)",
        request.method,
        request.path,
        response.status,
        watch.elapsed().as_millis()
    ));
    Ok(())
}

fn read_request(stream: &mut TcpStream) -> std::io::Result<Option<Request>> {
    let mut reader = BufReader::new(stream.try_clone()?);
    let mut first = String::new();
    if reader.read_line(&mut first)? == 0 {
        return Ok(None);
    }
    let mut total = first.len();
    if total > MAX_HEADER_BYTES {
        write_response(
            stream,
            &Response::text(431, "Request Header Fields Too Large"),
        )?;
        return Ok(None);
    }
    let parts: Vec<&str> = first.trim_end().split_whitespace().collect();
    if parts.len() != 3 || !parts[2].starts_with("HTTP/") {
        write_response(stream, &Response::text(400, "Bad Request"))?;
        return Ok(None);
    }
    let method = parts[0].to_ascii_uppercase();
    let target = parts[1];
    if target.len() > MAX_REQUEST_TARGET {
        write_response(stream, &Response::text(414, "URI Too Long"))?;
        return Ok(None);
    }

    let mut host: Option<String> = None;
    let mut header_count = 0;
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line)? == 0 {
            break;
        }
        total += line.len();
        if total > MAX_HEADER_BYTES {
            write_response(
                stream,
                &Response::text(431, "Request Header Fields Too Large"),
            )?;
            return Ok(None);
        }
        if line == "\r\n" || line == "\n" {
            break;
        }
        header_count += 1;
        if header_count > 100 {
            write_response(stream, &Response::text(431, "Too Many Headers"))?;
            return Ok(None);
        }
        if line.starts_with(' ') || line.starts_with('\t') {
            write_response(stream, &Response::text(400, "Bad Request"))?;
            return Ok(None);
        }
        let Some((name, value)) = line.split_once(':') else {
            write_response(stream, &Response::text(400, "Bad Request"))?;
            return Ok(None);
        };
        if name.eq_ignore_ascii_case("host") {
            if host.is_some() {
                write_response(stream, &Response::text(400, "Bad Request"))?;
                return Ok(None);
            }
            host = Some(value.trim().to_string());
        }
    }

    let path_part = target.split('?').next().unwrap_or(target);
    let decoded = urlencoding::decode(path_part)
        .map(|value| value.into_owned())
        .unwrap_or_else(|_| path_part.to_string());
    if decoded.chars().any(|ch| ch == '\0' || ch == '\\') || !decoded.starts_with('/') {
        write_response(stream, &Response::text(400, "Bad Request"))?;
        return Ok(None);
    }

    Ok(Some(Request {
        head_only: method == "HEAD",
        method,
        path: decoded,
        host: host.unwrap_or_default(),
    }))
}

fn route(request: &Request, state: &AppState) -> Response {
    if request.host.is_empty() || !is_allowed_host(&request.host, state.port()) {
        logging::warn(&format!(
            "Host 头校验失败，已拒绝：Host='{}' Path={}",
            logging::redact(&request.host),
            request.path
        ));
        return Response::text(403, "Forbidden: invalid Host header");
    }

    if request.path.eq_ignore_ascii_case("/health") {
        let json = serde_json::json!({
            "status": "ok",
            "version": env!("CARGO_PKG_VERSION"),
            "port": state.port()
        });
        return Response {
            status: 200,
            content_type: "application/json; charset=utf-8".into(),
            body: json.to_string().into_bytes(),
            extra_headers: Vec::new(),
            suppress_body: false,
        };
    }

    if request.path == "/__host__/tauri-shim.js" {
        return Response {
            status: 200,
            content_type: "text/javascript; charset=utf-8".into(),
            body: TAURI_SHIM.as_bytes().to_vec(),
            extra_headers: Vec::new(),
            suppress_body: false,
        };
    }

    if request.path == HOST_UI_PREFIX || request.path.starts_with(&format!("{HOST_UI_PREFIX}/")) {
        return serve_host_asset(&request.path, state);
    }

    if request.path.starts_with(TOOLS_PREFIX) {
        return serve_tool_asset(&request.path, state);
    }

    Response::text(404, "Not Found")
}

fn is_allowed_host(host: &str, port: u16) -> bool {
    let normalized = host.trim().to_ascii_lowercase();
    normalized == format!("127.0.0.1:{port}") || normalized == format!("localhost:{port}")
}

fn serve_host_asset(path: &str, state: &AppState) -> Response {
    let relative = path
        .strip_prefix(&format!("{HOST_UI_PREFIX}/"))
        .unwrap_or("index.html");
    if relative.is_empty() || relative.split('/').any(|segment| segment.starts_with('.')) {
        return Response::text(404, "Not Found");
    }
    if let Some(app_id) = relative.strip_prefix("app-icon/") {
        return serve_app_icon(app_id, state);
    }
    let (asset_path, served_name) = if relative.eq_ignore_ascii_case("icon.png") {
        ("brand/icon.png".to_string(), "icon.png")
    } else if relative.eq_ignore_ascii_case("icon.ico") {
        ("brand/icon.ico".to_string(), "icon.ico")
    } else {
        (format!("host-ui/{relative}"), relative)
    };
    let Some(content) = Assets::get(&asset_path) else {
        return Response::text(404, "Not Found");
    };
    let content_type = content_type_for(served_name);
    if is_html(served_name) {
        let html = String::from_utf8_lossy(content.data.as_ref()).into_owned();
        let transformed = transform_html(&html, "__host__", state);
        Response {
            status: 200,
            content_type,
            body: transformed.into_bytes(),
            extra_headers: Vec::new(),
            suppress_body: false,
        }
    } else {
        Response {
            status: 200,
            content_type,
            body: content.data.to_vec(),
            extra_headers: Vec::new(),
            suppress_body: false,
        }
    }
}

fn serve_app_icon(app_id: &str, state: &AppState) -> Response {
    if app_id.is_empty() || app_id.contains('/') || app_id.contains('\\') || app_id.contains("..") {
        return Response::text(404, "Not Found");
    }
    let app_icon = state.paths.apps_dir().join(app_id).join("icon.png");
    let cached_icon = state.paths.icon_cache_dir().join(format!("{app_id}.png"));
    let path = if app_icon.is_file() {
        app_icon
    } else if cached_icon.is_file() {
        cached_icon
    } else {
        return Response::text(404, "Not Found");
    };
    match fs::read(path) {
        Ok(body) => Response {
            status: 200,
            content_type: "image/png".into(),
            body,
            extra_headers: Vec::new(),
            suppress_body: false,
        },
        Err(_) => Response::text(404, "Not Found"),
    }
}

fn serve_tool_asset(path: &str, state: &AppState) -> Response {
    let relative = &path[TOOLS_PREFIX.len()..];
    let (tool_id, rest) = relative.split_once('/').unwrap_or((relative, ""));
    if !crate::tools::is_valid_tool_id(tool_id) {
        logging::warn(&format!("静态文件请求被拒绝：非法 toolId '{tool_id}'"));
        return Response::text(404, "Not Found");
    }
    if rest.contains('\0') {
        return Response::text(404, "Not Found");
    }
    let segments: Vec<&str> = rest
        .split('/')
        .filter(|segment| !segment.trim().is_empty())
        .collect();
    if segments.is_empty() {
        return Response::text(404, "Not Found");
    }
    for segment in &segments {
        if *segment == "." || *segment == ".." || segment.starts_with('.') || segment.contains('\0')
        {
            logging::warn("静态文件请求被拒绝：路径包含穿越段或宿主内部目录");
            return Response::text(404, "Not Found");
        }
    }

    let tool_dir = state.paths.tools_dir().join(tool_id);
    let full_path = segments
        .iter()
        .fold(tool_dir.clone(), |path, segment| path.join(segment));
    if !full_path.is_file() {
        return Response::text(404, "Not Found");
    }
    let Ok(canonical_tool) = fs::canonicalize(&tool_dir) else {
        return Response::text(404, "Not Found");
    };
    let Ok(canonical_file) = fs::canonicalize(&full_path) else {
        return Response::text(404, "Not Found");
    };
    if !path_within(&canonical_file, &canonical_tool) {
        logging::warn("静态文件请求被拒绝：符号链接逃逸");
        return Response::text(404, "Not Found");
    }
    let Ok(metadata) = fs::metadata(&full_path) else {
        return Response::text(404, "Not Found");
    };
    if metadata.len() > MAX_FILE_BYTES {
        return Response::text(404, "Not Found");
    }

    let file_name = segments.last().copied().unwrap_or_default();
    let content_type = content_type_for(file_name);
    if is_html(file_name) {
        let Ok(html) = fs::read_to_string(&full_path) else {
            return Response::text(500, "Internal Server Error");
        };
        let transformed = transform_html(&html, tool_id, state);
        return Response {
            status: 200,
            content_type,
            body: transformed.into_bytes(),
            extra_headers: Vec::new(),
            suppress_body: false,
        };
    }
    match fs::read(&full_path) {
        Ok(body) => Response {
            status: 200,
            content_type,
            body,
            extra_headers: Vec::new(),
            suppress_body: false,
        },
        Err(_) => Response::text(404, "Not Found"),
    }
}

fn path_within(file: &Path, directory: &Path) -> bool {
    let file = file.to_string_lossy().replace('\\', "/");
    let directory = directory.to_string_lossy().replace('\\', "/");
    #[cfg(windows)]
    {
        file.to_lowercase()
            .starts_with(&(directory.to_lowercase() + "/"))
    }
    #[cfg(not(windows))]
    {
        file.starts_with(&(directory + "/"))
    }
}

fn is_html(path: &str) -> bool {
    let extension = Path::new(path)
        .extension()
        .map(|value| value.to_string_lossy().to_ascii_lowercase())
        .unwrap_or_default();
    extension == "html" || extension == "htm"
}

pub(crate) fn content_type_for(path: &str) -> String {
    let extension = Path::new(path)
        .extension()
        .map(|value| value.to_string_lossy().to_ascii_lowercase())
        .unwrap_or_default();
    match extension.as_str() {
        "html" | "htm" => "text/html; charset=utf-8".into(),
        "js" | "mjs" => "text/javascript; charset=utf-8".into(),
        "css" => "text/css; charset=utf-8".into(),
        "json" => "application/json; charset=utf-8".into(),
        "svg" => "image/svg+xml".into(),
        "png" => "image/png".into(),
        "jpg" | "jpeg" => "image/jpeg".into(),
        "gif" => "image/gif".into(),
        "webp" => "image/webp".into(),
        "ico" => "image/x-icon".into(),
        "woff" => "font/woff".into(),
        "woff2" => "font/woff2".into(),
        "ttf" => "font/ttf".into(),
        "otf" => "font/otf".into(),
        "txt" | "md" => "text/plain; charset=utf-8".into(),
        "map" => "application/json; charset=utf-8".into(),
        _ => mime_guess::from_path(path)
            .first_raw()
            .unwrap_or("application/octet-stream")
            .to_string(),
    }
}

fn transform_html(html: &str, tool_id: &str, state: &AppState) -> String {
    let config = state.config_snapshot();
    let theme = config.shell.theme.as_str();
    let bridge = format!(
        "<script src=\"/__host__/tauri-shim.js\"></script>\
         <script src=\"/__host__/bridge.js?tool={}&v={}&port={}\"></script>",
        urlencoding::encode(tool_id),
        env!("CARGO_PKG_VERSION"),
        state.port()
    );
    let mut transformed = insert_after_head_open(html, &bridge);
    if tool_id == "__host__" {
        if let Some(explicit) = explicit_theme(theme) {
            transformed = set_html_attribute(&transformed, "data-theme", Some(explicit));
        }
    } else {
        let assets = "<link rel=\"stylesheet\" href=\"/__host__/tool-theme.css\">\
                      <script src=\"/__host__/tool-theme.js\" defer></script>";
        transformed = insert_after_head_open(&transformed, assets);
        let themed = contains_ci(&transformed, "data-theme");
        let adaptive = themed
            || contains_ci(&transformed, "prefers-color-scheme")
            || contains_ci(&transformed, "color-scheme");
        transformed = set_html_attribute(&transformed, "data-theme", explicit_theme(theme));
        transformed = set_html_attribute(&transformed, "data-qtools-theme", Some(theme));
        transformed = set_html_attribute(
            &transformed,
            "data-qtools-themed",
            Some(if adaptive { "1" } else { "0" }),
        );
        transformed = set_html_attribute(
            &transformed,
            "data-qtools-auto-dark",
            Some(if !adaptive && is_dark_now(theme) {
                "1"
            } else {
                "0"
            }),
        );
        let controls = "<script src=\"/__host__/window-controls.js\"></script>";
        transformed = if let Some(index) = transformed.to_lowercase().rfind("</body>") {
            let mut with_controls = transformed;
            with_controls.insert_str(index, controls);
            with_controls
        } else {
            format!("{controls}{transformed}")
        };
    }
    transformed
}

fn contains_ci(haystack: &str, needle: &str) -> bool {
    haystack.to_lowercase().contains(&needle.to_lowercase())
}

fn insert_after_head_open(html: &str, snippet: &str) -> String {
    let lower = html.to_lowercase();
    if let Some(open) = lower.find("<head") {
        if let Some(close_offset) = lower[open..].find('>') {
            let index = open + close_offset + 1;
            let mut result = String::with_capacity(html.len() + snippet.len());
            result.push_str(&html[..index]);
            result.push_str(snippet);
            result.push_str(&html[index..]);
            return result;
        }
    }
    format!("{snippet}{html}")
}

fn set_html_attribute(html: &str, name: &str, value: Option<&str>) -> String {
    let lower = html.to_lowercase();
    let Some(open) = lower.find("<html") else {
        return html.to_string();
    };
    let Some(close_offset) = lower[open..].find('>') else {
        return html.to_string();
    };
    let close = open + close_offset;
    let name_lower = name.to_lowercase();
    let mut index = None;
    let mut scan = open + 5;
    while scan < close {
        let Some(hit_offset) = lower[scan..close].find(&name_lower) else {
            break;
        };
        let hit = scan + hit_offset;
        let before_ok = hit > open && lower.as_bytes()[hit - 1].is_ascii_whitespace();
        let after = hit + name_lower.len();
        let after_ok = after < close
            && (lower.as_bytes()[after].is_ascii_whitespace() || lower.as_bytes()[after] == b'=');
        if before_ok && after_ok {
            index = Some(hit);
            break;
        }
        scan = hit + name_lower.len();
    }

    let Some(index) = index else {
        let Some(value) = value else {
            return html.to_string();
        };
        let mut result = String::with_capacity(html.len() + name.len() + value.len() + 4);
        result.push_str(&html[..close]);
        result.push(' ');
        result.push_str(name);
        result.push_str("=\"");
        result.push_str(&html_escape(value));
        result.push('"');
        result.push_str(&html[close..]);
        return result;
    };

    if value.is_none() {
        let mut start = index;
        while start > open && lower.as_bytes()[start - 1].is_ascii_whitespace() {
            start -= 1;
        }
        let mut end = index + name_lower.len();
        while end < close && lower.as_bytes()[end].is_ascii_whitespace() {
            end += 1;
        }
        if end < close && lower.as_bytes()[end] == b'=' {
            end += 1;
            while end < close && lower.as_bytes()[end].is_ascii_whitespace() {
                end += 1;
            }
            if end < close && (lower.as_bytes()[end] == b'"' || lower.as_bytes()[end] == b'\'') {
                let quote = lower.as_bytes()[end];
                if let Some(value_end_offset) = lower[end + 1..close].find(quote as char) {
                    end = end + 1 + value_end_offset + 1;
                }
            } else {
                while end < close && !lower.as_bytes()[end].is_ascii_whitespace() {
                    end += 1;
                }
            }
        }
        let mut result = String::with_capacity(html.len());
        result.push_str(&html[..start]);
        result.push_str(&html[end..]);
        return result;
    }

    let Some(value) = value else {
        return html.to_string();
    };
    let Some(equals_offset) = lower[index + name_lower.len()..close].find('=') else {
        return html.to_string();
    };
    let equals = index + name_lower.len() + equals_offset;
    let mut quote_index = equals + 1;
    while quote_index < close && lower.as_bytes()[quote_index].is_ascii_whitespace() {
        quote_index += 1;
    }
    if quote_index >= close
        || (lower.as_bytes()[quote_index] != b'"' && lower.as_bytes()[quote_index] != b'\'')
    {
        return html.to_string();
    }
    let quote = lower.as_bytes()[quote_index];
    let Some(closing_offset) = lower[quote_index + 1..close].find(quote as char) else {
        return html.to_string();
    };
    let closing = quote_index + 1 + closing_offset;
    let mut result = String::with_capacity(html.len() + value.len());
    result.push_str(&html[..quote_index + 1]);
    result.push_str(&html_escape(value));
    result.push_str(&html[closing..]);
    result
}

fn html_escape(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('"', "&quot;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

fn csp_for(state: &AppState, path: &str) -> &'static str {
    if path == HOST_UI_PREFIX || path.starts_with(&format!("{HOST_UI_PREFIX}/")) {
        return HOST_UI_CSP;
    }
    if let Some(rest) = path.strip_prefix(TOOLS_PREFIX) {
        let tool_id = rest.split('/').next().unwrap_or_default();
        if let Some(tool) = state.tools.find(tool_id) {
            if let Some(csp) = tool
                .manifest
                .csp
                .as_deref()
                .filter(|value| !value.trim().is_empty())
            {
                // manifest 自定义 CSP 是运行期字符串；这里使用泄漏换取静态生命周期的简单性。
                return Box::leak(csp.to_string().into_boxed_str());
            }
            return if tool.manifest.allow_eval {
                TOOL_CSP_ALLOW_EVAL
            } else {
                DEFAULT_TOOL_CSP
            };
        }
    }
    DEFAULT_TOOL_CSP
}

fn write_response(stream: &mut TcpStream, response: &Response) -> std::io::Result<()> {
    let reason = match response.status {
        200 => "OK",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        414 => "URI Too Long",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        _ => "Error",
    };
    let mut head = format!(
        "HTTP/1.1 {} {}\r\nContent-Type: {}\r\nContent-Length: {}\r\nConnection: close\r\n",
        response.status,
        reason,
        response.content_type,
        response.body.len()
    );
    for (name, value) in &response.extra_headers {
        head.push_str(name);
        head.push_str(": ");
        head.push_str(value);
        head.push_str("\r\n");
    }
    head.push_str("\r\n");
    stream.write_all(head.as_bytes())?;
    if !response.suppress_body {
        stream.write_all(&response.body)?;
    }
    stream.flush()
}

/// 供桥接层拼接工具 URL。
pub fn tool_url(port: u16, tool_id: &str, entry: &str) -> String {
    format!(
        "http://127.0.0.1:{port}/tools/{}/{}",
        urlencoding::encode(tool_id),
        entry
    )
}

pub fn host_url(port: u16, page: &str) -> String {
    format!("http://127.0.0.1:{port}/__host__/{}", page)
}

#[allow(dead_code)]
fn read_body(reader: &mut impl Read) -> std::io::Result<Vec<u8>> {
    let mut buffer = Vec::new();
    reader.read_to_end(&mut buffer)?;
    Ok(buffer)
}

#[allow(dead_code)]
fn unused(_: PathBuf) {}
