#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod ai;
mod app_icons;
mod assets;
mod backup;
mod bridge;
mod config;
mod creator;
mod error;
mod files;
mod git_backup;
mod hotkey;
mod item_package;
mod launcher;
mod logging;
mod paths;
mod program_index;
mod secret_store;
mod self_test;
mod server;
mod shell;
mod shortcut;
mod state;
mod storage;
mod tools;
mod tray;
mod window_pin;
mod window_theme;

use std::sync::Arc;

use tauri::{AppHandle, Manager, WebviewUrl, WebviewWindowBuilder, WindowEvent};

use paths::AppPaths;
use server::{LocalServer, host_url};
use state::AppState;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|arg| arg == "--help" || arg == "-h") {
        println!("{}", usage());
        return;
    }
    if args.iter().any(|arg| arg == "--version") {
        println!("QTools (Rust + Tauri 2) {}", env!("CARGO_PKG_VERSION"));
        return;
    }
    if let Err(message) = validate_args(&args) {
        eprintln!("QTools: {message}");
        eprintln!("{}", usage());
        std::process::exit(2);
    }
    let fixed_port = match parse_port(&args) {
        Ok(port) => port,
        Err(message) => {
            eprintln!("QTools: {message}");
            return;
        }
    };
    let root = value_of(&args, "--root");
    let paths = AppPaths::initialize(root.as_deref());
    logging::initialize(&paths, args.iter().any(|arg| arg == "--dev"));
    logging::info(&format!(
        "QTools Rust/Tauri 启动 | 数据目录={} | PID={}",
        paths.root.display(),
        std::process::id()
    ));

    let state = AppState::new(paths);
    if let Ok(key) = std::env::var("QTOOLS_AI_KEY") {
        if !key.trim().is_empty() && state.secrets.set(key.trim()).is_ok() {
            logging::info("已从环境变量注入 AI Key 到系统保护存储");
        }
    }

    if args.iter().any(|arg| arg == "--self-test") {
        let code = self_test::run(state);
        std::process::exit(code);
    }

    let mut open_tool = value_of(&args, "--open-tool");
    let open_settings = args.iter().any(|arg| arg == "--open-settings");
    let notify = notify_args(&args);

    if let Some(tool_id) = value_of(&args, "--delete-tool") {
        let code = run_headless_delete(
            &state,
            &tool_id,
            args.iter().any(|arg| arg == "--delete-storage"),
        );
        std::process::exit(code);
    }

    if let Some(prompt) = value_of(&args, "--generate-tool") {
        match run_headless_generate(&state, &prompt, args.iter().any(|arg| arg == "--yes")) {
            Ok(Some(tool_id)) => open_tool = Some(tool_id),
            Ok(None) => std::process::exit(0),
            Err(code) => std::process::exit(code),
        }
    }

    if let Some(tool_id) = open_tool.as_deref() {
        if state.tools.find(tool_id).is_none_or(|tool| !tool.is_valid) {
            eprintln!("QTools: 工具不存在或 manifest 无效：{tool_id}");
            std::process::exit(3);
        }
    }

    let builder = tauri::Builder::default()
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| {
            handle_second_instance(app, &argv);
        }))
        .invoke_handler(tauri::generate_handler![bridge::bridge_message])
        .setup(move |app| {
            let handle = app.handle().clone();
            if let Some((title, body)) = notify {
                if let Err(error) = shell::notify(&handle, &title, &body) {
                    logging::error(&format!("系统通知发送失败：{error}"));
                }
                let handle = handle.clone();
                std::thread::spawn(move || {
                    std::thread::sleep(std::time::Duration::from_secs(6));
                    handle.exit(0);
                });
                return Ok(());
            }

            app.manage(state.clone());
            let server = LocalServer::start(state.clone(), fixed_port)?;
            logging::info(&format!("本地服务端口 {}", server.port));

            if let Err(error) = tray::init(&handle) {
                logging::warn(&format!("初始化托盘失败：{error}"));
            }
            let config = state.config_snapshot();
            if config.shell.hotkey_enabled && cfg!(windows) {
                if !hotkey::start(handle.clone()) {
                    logging::warn("全局热键未启用（注册失败）");
                }
            }

            let width = config
                .shell
                .main_window_width
                .unwrap_or(340)
                .clamp(280, 8000);
            let height = config
                .shell
                .main_window_height
                .unwrap_or(720)
                .clamp(480, 6000);
            let mut window_builder = WebviewWindowBuilder::new(
                app,
                shell::MAIN_LABEL,
                WebviewUrl::External(
                    host_url(state.port(), "index.html")
                        .parse()
                        .expect("宿主页面 URL"),
                ),
            )
            .title("QTools")
            .inner_size(width as f64, height as f64)
            .min_inner_size(280.0, 480.0)
            .visible(false);
            if let (Some(left), Some(top)) =
                (config.shell.main_window_left, config.shell.main_window_top)
            {
                window_builder = window_builder.position(left as f64, top as f64);
            }
            let main = window_builder.build()?;
            if let (Some(left), Some(top)) =
                (config.shell.main_window_left, config.shell.main_window_top)
            {
                shell::restore_saved_position(&main, left, top);
            }
            let _ = main.show();
            let _ = main.set_focus();
            window_theme::apply(&main, &state, true);
            program_index::start_background_refresh(app.handle().clone(), state.clone());

            if open_settings {
                let _ = shell::open_settings_window(&handle, &state);
            }
            if let Some(tool_id) = open_tool.as_deref() {
                open_tool_for(&handle, tool_id);
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            let label = window.label().to_string();
            if label == shell::TRAY_MENU_LABEL {
                if let WindowEvent::Focused(false) = event {
                    let Some(state) = window.app_handle().try_state::<Arc<AppState>>() else {
                        return;
                    };
                    let state = state.inner();
                    let shown = *state.tray_menu_shown_at.lock();
                    if shown.is_some_and(|instant| {
                        instant.elapsed() >= std::time::Duration::from_millis(800)
                    }) {
                        shell::hide_tray_menu(window.app_handle(), state);
                    }
                }
                return;
            }
            if label != shell::MAIN_LABEL {
                return;
            }
            if let WindowEvent::CloseRequested { api, .. } = event {
                let Some(state) = window.app_handle().try_state::<Arc<AppState>>() else {
                    return;
                };
                let state = state.inner();
                if let Some(webview) = window.app_handle().get_webview_window(shell::MAIN_LABEL) {
                    shell::persist_geometry(state, &webview, shell::MAIN_LABEL, None);
                }
                if cfg!(windows)
                    && !state
                        .exit_requested
                        .load(std::sync::atomic::Ordering::SeqCst)
                {
                    api.prevent_close();
                    let _ = window.hide();
                    logging::info("主窗口已收起到托盘");
                }
            }
        });

    if let Err(error) = builder.run(tauri::generate_context!()) {
        logging::error(&format!("应用运行失败：{error}"));
        eprintln!("QTools 启动失败：{error}");
        std::process::exit(1);
    }
}

fn validate_args(args: &[String]) -> Result<(), String> {
    const VALUE_OPTIONS: [&str; 5] = [
        "--port",
        "--root",
        "--open-tool",
        "--generate-tool",
        "--delete-tool",
    ];
    const FLAGS: [&str; 10] = [
        "--self-test",
        "--dev",
        "--yes",
        "--delete-storage",
        "--open-settings",
        "--help",
        "-h",
        "/?",
        "--version",
        "--notify",
    ];
    let mut index = 0;
    while index < args.len() {
        let arg = args[index].as_str();
        if VALUE_OPTIONS.contains(&arg) {
            let Some(value) = args.get(index + 1) else {
                return Err(format!("{arg} 缺少参数"));
            };
            if value.starts_with("--") {
                return Err(format!("{arg} 缺少参数"));
            }
            index += 2;
            continue;
        }
        if arg == "--notify" {
            let Some(title) = args.get(index + 1) else {
                return Err("--notify 需要非空通知标题".into());
            };
            if title.starts_with("--") || title.trim().is_empty() {
                return Err("--notify 需要非空通知标题".into());
            }
            if title.chars().count() > 64 {
                return Err("--notify 标题不超过 64 字符".into());
            }
            index += 2;
            if let Some(body) = args.get(index) {
                if !body.starts_with("--") {
                    if body.chars().count() > 256 {
                        return Err("--notify 正文不超过 256 字符".into());
                    }
                    index += 1;
                }
            }
            continue;
        }
        if FLAGS.contains(&arg) {
            index += 1;
            continue;
        }
        return Err(format!("未知参数：{arg}"));
    }
    Ok(())
}

fn parse_port(args: &[String]) -> Result<Option<u16>, String> {
    let Some(index) = args.iter().position(|arg| arg == "--port") else {
        return Ok(None);
    };
    let value = args
        .get(index + 1)
        .ok_or_else(|| "--port 需要 1-65535 之间的整数".to_string())?;
    let port = value
        .parse::<u16>()
        .map_err(|_| "--port 需要 1-65535 之间的整数".to_string())?;
    if port == 0 {
        return Err("--port 需要 1-65535 之间的整数".into());
    }
    Ok(Some(port))
}

/// 无头删除：删除工具目录与按需存储，退出码与 .NET 版对齐（0=成功，6=失败，7=不存在）。
fn run_headless_delete(state: &Arc<AppState>, tool_id: &str, delete_storage: bool) -> i32 {
    let Some(tool) = state.tools.find(tool_id) else {
        eprintln!("工具不存在：{tool_id}");
        return 7;
    };
    let storage_path = state.paths.storage_dir().join(format!("{tool_id}.json"));
    let had_storage = storage_path.is_file();
    let storage_bytes = std::fs::metadata(&storage_path)
        .map(|meta| meta.len())
        .unwrap_or(0);
    println!("删除工具 {tool_id}（目录 {}）", tool.directory.display());
    println!(
        "  删除存储：{}；删除前存在存储文件：{}",
        if delete_storage { "是" } else { "否" },
        if had_storage {
            format!("是（{storage_bytes} 字节）")
        } else {
            "否".into()
        }
    );
    if let Err(error) = std::fs::remove_dir_all(&tool.directory) {
        if error.kind() != std::io::ErrorKind::NotFound {
            logging::error(&format!("无头删除工具目录失败：{error}"));
            eprintln!("删除失败：{error}");
            return 6;
        }
    }
    if delete_storage {
        state.storage.delete(tool_id);
    }
    state.tools.remove(tool_id);
    state.revoke_embed_tokens(tool_id);
    state.pinned.forget("tool", tool_id);
    let directory_gone = !tool.directory.exists();
    let storage_gone = !storage_path.exists();
    let still_listed = state.tools.find(tool_id).is_some();
    println!("  工具目录已删除：{directory_gone}");
    println!(
        "  存储文件已删除：{}",
        if delete_storage {
            storage_gone.to_string()
        } else {
            "未要求删除".into()
        }
    );
    println!("  列表中已移除：{}", !still_listed);
    println!(
        "  剩余工具：{}",
        state
            .tools
            .all()
            .iter()
            .map(|tool| tool.manifest.id.clone())
            .collect::<Vec<_>>()
            .join(", ")
    );
    if directory_gone && !still_listed && (!delete_storage || storage_gone) {
        println!("删除完成");
        0
    } else {
        println!("删除结果不符合预期");
        6
    }
}

/// 无头生成：调用 AI → 安全审查 → 打印报告；--yes 时保存并返回工具 ID 供 GUI 打开。
fn run_headless_generate(
    state: &Arc<AppState>,
    prompt: &str,
    accept_high_severity: bool,
) -> Result<Option<String>, i32> {
    let config = state.config_snapshot();
    let Some(api_key) = state.secrets.get() else {
        eprintln!("生成失败：AI_NOT_CONFIGURED 宿主尚未配置 AI API Key");
        return Err(4);
    };
    if config.ai.base_url.trim().is_empty() || config.ai.model.trim().is_empty() {
        eprintln!("生成失败：AI_NOT_CONFIGURED 宿主尚未配置 AI 接口");
        return Err(4);
    }
    let messages = vec![
        crate::ai::ChatMessage {
            role: "system".into(),
            content: crate::creator::SYSTEM_PROMPT.into(),
        },
        crate::ai::ChatMessage {
            role: "user".into(),
            content: format!("{}{}", crate::creator::GENERATE_REQUIREMENTS, prompt.trim()),
        },
    ];
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("生成失败：无法初始化运行时：{error}");
            return Err(4);
        }
    };
    let (_cancel, receiver) = tokio::sync::watch::channel(false);
    let raw = match runtime.block_on(crate::ai::chat(
        &config.ai, &api_key, &messages, None, receiver,
    )) {
        Ok(raw) => raw,
        Err(error) => {
            eprintln!("生成失败：{} {}", error.code, error.message);
            return Err(4);
        }
    };
    let result = match crate::bridge::build_generated(&raw, None) {
        Ok(result) => result,
        Err(error) => {
            eprintln!("生成失败：{} {}", error.code, error.message);
            return Err(4);
        }
    };
    println!("=== 安全审查结果 ===");
    if result.findings.is_empty() {
        println!("  （未发现安全问题）");
    }
    for finding in &result.findings {
        let level = match finding.severity {
            0 => "致命",
            1 => "高危",
            _ => "提示",
        };
        println!("  [{level}] {}: {}", finding.code, finding.message);
    }
    println!("=== manifest.json ===");
    println!(
        "{} / {} v{}",
        result.manifest.id, result.manifest.name, result.manifest.version
    );
    println!("  permissions: {}", result.manifest.permissions.join(","));
    println!("  indexHtml: {} 字符", result.index_html.len());
    if result.has_fatal {
        eprintln!("存在致命问题，拒绝保存");
        return Err(5);
    }
    if !accept_high_severity {
        println!("未指定 --yes，仅打印审查报告，不保存");
        return Ok(None);
    }
    let tools_root = state.paths.tools_dir();
    match crate::creator::create_tool(
        crate::creator::CreateDeps {
            registry: &state.tools,
            tools_root: &tools_root,
        },
        &result.manifest,
        &result.index_html,
        &result.findings,
        true,
    ) {
        Ok(tool_id) => {
            state.usage.ensure_seeded([tool_id.clone()]);
            println!("已保存工具 {tool_id}");
            Ok(Some(tool_id))
        }
        Err(error) => {
            eprintln!("保存失败：{} {}", error.code, error.message);
            Err(if error.code == "CONTENT_SECURITY_VIOLATION" {
                5
            } else {
                6
            })
        }
    }
}

fn handle_second_instance(app: &AppHandle, argv: &[String]) {
    if let Some((title, body)) = notify_args(argv) {
        if let Err(error) = shell::notify(app, &title, &body) {
            logging::error(&format!("单实例通知转发失败：{error}"));
        }
        return;
    }
    if argv.iter().any(|arg| arg == "--open-settings") {
        if let Some(state) = app.try_state::<Arc<AppState>>() {
            if let Err(error) = shell::open_settings_window(app, state.inner()) {
                logging::error(&format!("打开设置失败：{error}"));
            }
        }
        return;
    }
    if let Some(tool_id) = value_of(argv, "--open-tool") {
        open_tool_for(app, &tool_id);
        return;
    }
    tray::show_main(app);
}

fn open_tool_for(app: &tauri::AppHandle, tool_id: &str) {
    let Some(state) = app.try_state::<Arc<AppState>>() else {
        return;
    };
    let Some(tool) = state
        .inner()
        .tools
        .find(tool_id)
        .filter(|tool| tool.is_valid)
    else {
        logging::warn(&format!("工具不存在或无效：{tool_id}"));
        return;
    };
    if let Err(error) = shell::open_tool_window(app, state.inner(), &tool) {
        logging::error(&format!("打开工具窗口失败：{error}"));
    }
    state.inner().usage.record(tool_id);
}

fn value_of(args: &[String], name: &str) -> Option<String> {
    args.iter()
        .position(|arg| arg == name)
        .and_then(|index| args.get(index + 1))
        .cloned()
        .filter(|value| !value.starts_with("--"))
}

fn notify_args(args: &[String]) -> Option<(String, String)> {
    let index = args.iter().position(|arg| arg == "--notify")?;
    let title = args
        .get(index + 1)
        .filter(|value| !value.starts_with("--"))
        .cloned()?;
    let body = args
        .get(index + 2)
        .filter(|value| !value.starts_with("--"))
        .cloned()
        .unwrap_or_default();
    Some((title, body))
}

fn usage() -> &'static str {
    "QTools（Rust + Tauri 2 移植版）\n\n\
     用法: QTools.exe [选项]\n\n\
     选项:\n\
       --root <目录>         指定数据根目录\n\
       --open-tool <id>      启动后打开指定小工具\n\
       --open-settings       启动后打开设置窗口\n\
       --notify <标题> [正文] 发送系统通知（已运行实例会转发）\n\
       --self-test           无 GUI 启动本地服务并执行 HTTP 自检\n\
       --dev                 开发模式（输出调试日志）\n\
       --version             显示版本\n\
       --help                显示本帮助\n"
}
