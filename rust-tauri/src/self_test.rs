//! `--self-test`：无 GUI 启动本地服务，打一组 HTTP 断言后退出。退出码 0 = 全过。
//! 只验证宿主静态资源、安全边界与已装工具的入口可达性，不依赖 WebView。

use std::io::{Read, Write};
use std::net::TcpStream;
use std::sync::Arc;
use std::time::Duration;

use crate::server::LocalServer;
use crate::state::AppState;

struct Check {
    passed: u32,
    failed: u32,
}

impl Check {
    fn assert(&mut self, name: &str, condition: bool, detail: &str) {
        if condition {
            self.passed += 1;
            println!("PASS {name}");
        } else {
            self.failed += 1;
            println!("FAIL {name} :: {detail}");
        }
    }
}

pub fn run(state: Arc<AppState>) -> i32 {
    let server = match LocalServer::start(state.clone(), None) {
        Ok(server) => server,
        Err(error) => {
            eprintln!("FAIL self-test-start :: {error}");
            return 1;
        }
    };
    let port = server.port;
    let mut check = Check {
        passed: 0,
        failed: 0,
    };

    let health = get(port, "/health", &format!("127.0.0.1:{port}"));
    check.assert(
        "health-status",
        health
            .as_ref()
            .is_some_and(|response| response.status == 200),
        &status_detail(&health),
    );
    check.assert(
        "health-json",
        health
            .as_ref()
            .is_some_and(|response| response.body.contains("\"status\":\"ok\"")),
        "健康检查未返回 status=ok",
    );

    let bad_host = get_raw(
        port,
        &format!(
            "GET /__host__/index.html HTTP/1.1\r\nHost: evil.example\r\nConnection: close\r\n\r\n"
        ),
    );
    check.assert(
        "host-header-rejected",
        bad_host
            .as_ref()
            .is_some_and(|response| response.status == 403),
        &status_detail(&bad_host),
    );

    let index = get(port, "/__host__/index.html", &format!("127.0.0.1:{port}"));
    check.assert(
        "host-index",
        index.as_ref().is_some_and(|response| {
            response.status == 200
                && response.body.contains("QTools")
                && response.body.contains("bridge.js")
        }),
        &status_detail(&index),
    );
    check.assert(
        "host-csp",
        index.as_ref().is_some_and(|response| {
            response
                .headers
                .contains("content-security-policy: default-src 'self'")
        }),
        "宿主页面缺少 CSP",
    );

    let settings = get(
        port,
        "/__host__/settings.html",
        &format!("127.0.0.1:{port}"),
    );
    check.assert(
        "settings-page",
        settings.as_ref().is_some_and(|response| {
            response.status == 200
                && response.body.contains("设置")
                && response.body.contains("theme.js")
        }),
        &status_detail(&settings),
    );

    for asset in [
        "/__host__/bridge.js",
        "/__host__/tauri-shim.js",
        "/__host__/theme.js",
        "/__host__/icon.png",
    ] {
        let response = get(port, asset, &format!("127.0.0.1:{port}"));
        check.assert(
            &format!("asset{}", asset.replace('/', "-")),
            response
                .as_ref()
                .is_some_and(|response| response.status == 200),
            &status_detail(&response),
        );
    }

    let traversal = get(
        port,
        "/__host__/../config.json",
        &format!("127.0.0.1:{port}"),
    );
    check.assert(
        "path-traversal",
        traversal
            .as_ref()
            .is_some_and(|response| response.status == 400 || response.status == 404),
        &status_detail(&traversal),
    );

    let post = get_raw(
        port,
        &format!(
            "POST /__host__/index.html HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        ),
    );
    check.assert(
        "method-not-allowed",
        post.as_ref().is_some_and(|response| response.status == 405),
        &status_detail(&post),
    );

    let head = get_raw(
        port,
        &format!(
            "HEAD /__host__/index.html HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n"
        ),
    );
    check.assert(
        "head-no-body",
        head.as_ref()
            .is_some_and(|response| response.status == 200 && response.body.is_empty()),
        "HEAD 不应返回正文",
    );

    let tools = state.tools.all();
    check.assert("tools-present", !tools.is_empty(), "没有可用小工具");
    for tool in tools.iter().filter(|tool| tool.is_valid) {
        let url = format!("/tools/{}/{}", tool.manifest.id, tool.entry());
        let response = get(port, &url, &format!("127.0.0.1:{port}"));
        check.assert(
            &format!("tool-{}-entry", tool.manifest.id),
            response.as_ref().is_some_and(|response| {
                response.status == 200
                    && response.body.contains("bridge.js")
                    && response.body.contains("tool-theme")
            }),
            &status_detail(&response),
        );
        let manifest = get(
            port,
            &format!("/tools/{}/manifest.json", tool.manifest.id),
            &format!("127.0.0.1:{port}"),
        );
        check.assert(
            &format!("tool-{}-manifest", tool.manifest.id),
            manifest
                .as_ref()
                .is_some_and(|response| response.status == 200),
            &status_detail(&manifest),
        );
        let dot = get(
            port,
            &format!("/tools/{}/.versions/1.0.0/index.html", tool.manifest.id),
            &format!("127.0.0.1:{port}"),
        );
        check.assert(
            &format!("tool-{}-dotdir", tool.manifest.id),
            dot.as_ref().is_some_and(|response| response.status == 404),
            &status_detail(&dot),
        );
    }

    let total = check.passed + check.failed;
    println!(
        "\n{} PASS / {} FAIL (total {})",
        check.passed, check.failed, total
    );
    if check.failed == 0 { 0 } else { 1 }
}

struct HttpResponse {
    status: u16,
    headers: String,
    body: String,
}

fn status_detail(response: &Option<HttpResponse>) -> String {
    match response {
        Some(response) => format!("status={}", response.status),
        None => "无响应".into(),
    }
}

fn get(port: u16, path: &str, host: &str) -> Option<HttpResponse> {
    get_raw(
        port,
        &format!("GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"),
    )
}

fn get_raw(port: u16, request: &str) -> Option<HttpResponse> {
    let mut stream = TcpStream::connect(("127.0.0.1", port)).ok()?;
    let _ = stream.set_read_timeout(Some(Duration::from_secs(10)));
    let _ = stream.set_write_timeout(Some(Duration::from_secs(10)));
    stream.write_all(request.as_bytes()).ok()?;
    let mut raw = Vec::new();
    stream.read_to_end(&mut raw).ok()?;
    let text = String::from_utf8_lossy(&raw).into_owned();
    let (head, body) = text.split_once("\r\n\r\n").unwrap_or((&text, ""));
    let status = head
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|value| value.parse::<u16>().ok())?;
    Some(HttpResponse {
        status,
        headers: head.to_ascii_lowercase(),
        body: body.to_string(),
    })
}
