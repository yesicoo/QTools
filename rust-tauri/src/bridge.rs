use std::sync::Arc;

use serde::Deserialize;
use serde_json::{Value, json};
use tauri::{AppHandle, Emitter, Manager, WebviewWindow};

use crate::config::{is_dark_now, normalize_theme};
use crate::error::{BridgeError, BridgeResult};
use crate::launcher::{LauncherSources, build_launcher_list};
use crate::server::tool_url;
use crate::shell;
use crate::state::AppState;
use crate::tools::{build_search_key, is_valid_tool_id};

const HOST_TOOL_ID: &str = "__host__";
const MAX_MESSAGE_BYTES: usize = 1024 * 1024;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct BridgeRequest {
    id: String,
    #[serde(default)]
    tool_id: String,
    method: String,
    #[serde(default)]
    token: Option<String>,
    #[serde(default)]
    payload: Value,
}

/// Tauri 前端统一桥接入口：与 .NET 版 bridge.js 共用同一套信封，
/// 这里只负责把消息交给 Rust 分发器，并把响应 eval 回发起窗口。
#[tauri::command]
pub fn bridge_message(app: AppHandle, window: WebviewWindow, message: String) {
    let state = app.state::<Arc<AppState>>().inner().clone();
    let label = window.label().to_string();
    let app_handle = app.clone();
    tauri::async_runtime::spawn(async move {
        let envelope = dispatch(&app_handle, &state, &window, &label, &message).await;
        send_envelope(&window, &envelope);
    });
}

fn send_envelope(window: &WebviewWindow, envelope: &Value) {
    let Ok(json) = serde_json::to_string(envelope) else {
        return;
    };
    let Ok(literal) = serde_json::to_string(&json) else {
        return;
    };
    let script = format!("if (window.__qtoolsReceive) {{ window.__qtoolsReceive({literal}); }}");
    let _ = window.eval(&script);
}

pub(crate) fn send_stream(window: &WebviewWindow, request_id: &str, delta: &str, reasoning: bool) {
    let envelope = json!({
        "id": request_id,
        "stream": { "delta": delta, "reasoning": reasoning }
    });
    send_envelope(window, &envelope);
}

async fn dispatch(
    app: &AppHandle,
    state: &Arc<AppState>,
    window: &WebviewWindow,
    label: &str,
    raw: &str,
) -> Value {
    let mut request_id = "unknown".to_string();
    let method;
    if raw.len() > MAX_MESSAGE_BYTES {
        return error_envelope(
            &request_id,
            "BRIDGE_MESSAGE_TOO_LARGE",
            "单条桥接消息不超过 1 MB",
        );
    }
    let request: BridgeRequest = match serde_json::from_str(raw) {
        Ok(value) => value,
        Err(error) => {
            return error_envelope(
                &request_id,
                "BRIDGE_BAD_REQUEST",
                &format!("消息不是合法 JSON：{error}"),
            );
        }
    };
    if request.id.is_empty() || request.method.is_empty() {
        return error_envelope(&request_id, "BRIDGE_BAD_REQUEST", "缺少 id 或 method 字段");
    }
    request_id = request.id;
    method = request.method.clone();
    // 在线工具窗口加载的是外部网页（即使是回环地址），与 .NET 版一样不注入 / 不放行宿主桥接。
    if label.starts_with("web-") {
        return error_envelope(
            &request_id,
            "PERMISSION_DENIED",
            "在线工具窗口不提供宿主桥接",
        );
    }
    let host_window = matches!(label, "main" | "content" | "settings" | "tray-menu");
    if !host_window && !label.starts_with("tool-") {
        return error_envelope(&request_id, "PERMISSION_DENIED", "未知窗口不提供宿主桥接");
    }
    let mut tool_id = authoritative_tool_id(label);

    // 主窗口内嵌 iframe 的凭证：只在窗口绑定为宿主时参与身份解析。
    if tool_id == HOST_TOOL_ID {
        if let Some(token) = request.token.as_deref().filter(|value| !value.is_empty()) {
            let Some(embedded) = state.resolve_embed_token(token) else {
                return error_envelope(&request_id, "PERMISSION_DENIED", "内嵌凭证无效或已过期");
            };
            tool_id = embedded;
        }
    }
    let _ = request.tool_id;

    let spec = match method_spec(&method) {
        Some(spec) => spec,
        None => {
            return error_envelope(
                &request_id,
                "UNKNOWN_METHOD",
                &format!("未知方法 '{method}'"),
            );
        }
    };
    let tool = state.tools.find(&tool_id);
    if tool_id == HOST_TOOL_ID {
        if !spec.host_only {
            return error_envelope(
                &request_id,
                "PERMISSION_DENIED",
                "该方法不对宿主管理界面开放",
            );
        }
    } else {
        if spec.host_only {
            return error_envelope(
                &request_id,
                "PERMISSION_DENIED",
                "该方法仅限宿主管理界面调用",
            );
        }
        let Some(tool) = tool.as_ref() else {
            return error_envelope(
                &request_id,
                "TOOL_NOT_FOUND",
                &format!("未找到工具 '{tool_id}'"),
            );
        };
        if let Some(permission) = spec.permission {
            if !tool.has_permission(permission) {
                return error_envelope(
                    &request_id,
                    "PERMISSION_DENIED",
                    &format!("权限不足：manifest 未声明 {permission}"),
                );
            }
        }
    }

    match execute_method(
        app,
        state,
        window,
        label,
        &tool_id,
        &method,
        &request.payload,
        &request_id,
    )
    .await
    {
        Ok(result) => json!({ "id": request_id, "result": result }),
        Err(error) => {
            crate::logging::info(&format!(
                "桥接 {method} -> {}: {}",
                error.code, error.message
            ));
            error_envelope(&request_id, &error.code, &error.message)
        }
    }
}

struct MethodSpec {
    host_only: bool,
    permission: Option<&'static str>,
}

fn method_spec(method: &str) -> Option<MethodSpec> {
    let spec = |host_only, permission| {
        Some(MethodSpec {
            host_only,
            permission,
        })
    };
    match method {
        "storage.get" | "storage.set" | "storage.remove" | "storage.list" => {
            spec(false, Some("storage"))
        }
        "ai.chat" => spec(false, Some("ai")),
        "files.save" | "files.list" | "files.read" | "files.delete" => spec(false, Some("files")),
        "tool.getManifest"
        | "theme.get"
        | "window.getState"
        | "window.setTopmost"
        | "window.setPinned"
        | "window.openVersionManager"
        | "window.saveChipPosition" => spec(false, None),
        "manager.cancelRequest"
        | "manager.getServerInfo"
        | "manager.listTools"
        | "manager.openTool"
        | "manager.deleteTool"
        | "manager.renameTool"
        | "manager.createToolShortcut"
        | "manager.getAiConfig"
        | "manager.setAiConfig"
        | "manager.openSettings"
        | "manager.importTool"
        | "manager.listVersions"
        | "manager.switchVersion"
        | "manager.deleteVersion"
        | "manager.generateUpdate"
        | "manager.applyUpdate"
        | "manager.listApps"
        | "manager.launchApp"
        | "manager.addApp"
        | "manager.deleteApp"
        | "manager.discoverPrograms"
        | "manager.pickProgram"
        | "manager.launcherList"
        | "manager.reindexPrograms"
        | "manager.addWebTool"
        | "manager.deleteWebTool"
        | "manager.renameWebTool"
        | "manager.openWebTool"
        | "manager.hideProgram"
        | "manager.unhideProgram"
        | "manager.revealItem"
        | "manager.setItemPinned"
        | "manager.exportItem"
        | "manager.pickImportFile"
        | "manager.importItemPackage"
        | "manager.getContentWindowState"
        | "manager.setContentWindowTopmost"
        | "manager.setContentWindowPinned"
        | "manager.generateTool"
        | "manager.createTool"
        | "manager.fixTool"
        | "manager.embedTool"
        | "manager.listBackups"
        | "manager.createBackup"
        | "manager.restoreBackup"
        | "manager.deleteBackup"
        | "manager.importBackupFile"
        | "manager.openBackupDirectory"
        | "manager.getBackupConfig"
        | "manager.setBackupConfig"
        | "manager.gitPush"
        | "manager.gitRestore"
        | "manager.setHotkeyEnabled"
        | "manager.setTrayMenuItems"
        | "manager.getTheme"
        | "manager.setTheme"
        | "manager.notify"
        | "manager.openContent"
        | "manager.closeContent"
        | "manager.trayMenuList"
        | "manager.trayMenuReady"
        | "manager.hideTrayMenu"
        | "manager.exitApp" => spec(true, None),
        _ => None,
    }
}

fn authoritative_tool_id(label: &str) -> String {
    if let Some(tool_id) = label.strip_prefix("tool-") {
        return tool_id.to_string();
    }
    HOST_TOOL_ID.to_string()
}

async fn execute_method(
    app: &AppHandle,
    state: &Arc<AppState>,
    window: &WebviewWindow,
    label: &str,
    tool_id: &str,
    method: &str,
    payload: &Value,
    request_id: &str,
) -> BridgeResult<Value> {
    match method {
        "storage.get" => {
            let key = read_string(payload, "key")?;
            let value = state.storage.get(tool_id, &key)?;
            Ok(match value {
                Some(value) => json!({ "found": true, "value": value }),
                None => json!({ "found": false, "value": null }),
            })
        }
        "storage.set" => {
            let key = read_string(payload, "key")?;
            let value = payload
                .get("value")
                .cloned()
                .ok_or_else(|| BridgeError::bad_request("payload.value 必填"))?;
            state.storage.set(tool_id, &key, value)?;
            Ok(json!({ "ok": true }))
        }
        "storage.remove" => {
            let key = read_string(payload, "key")?;
            state.storage.remove(tool_id, &key)?;
            Ok(json!({ "ok": true }))
        }
        "storage.list" => Ok(json!({ "keys": state.storage.list(tool_id)? })),
        "tool.getManifest" => {
            let tool = state.tools.find(tool_id).ok_or_else(|| {
                BridgeError::new("TOOL_NOT_FOUND", format!("未找到工具 '{tool_id}'"))
            })?;
            Ok(serde_json::to_value(&tool.manifest)?)
        }
        "theme.get" => Ok(theme_view(state)),
        "window.getState" => {
            require_tool_window(label, tool_id)?;
            let (topmost, pinned, chip_left, chip_top) = shell::window_state_view(state, tool_id);
            Ok(json!({
                "topmost": topmost, "pinned": pinned,
                "chipLeft": chip_left, "chipTop": chip_top
            }))
        }
        "window.setTopmost" => {
            require_tool_window(label, tool_id)?;
            let enabled = read_bool(payload, "enabled")?;
            shell::set_tool_topmost(window, state, tool_id, enabled)?;
            let (topmost, pinned, _, _) = shell::window_state_view(state, tool_id);
            Ok(json!({ "topmost": topmost, "pinned": pinned }))
        }
        "window.setPinned" => {
            require_tool_window(label, tool_id)?;
            let enabled = read_bool(payload, "enabled")?;
            shell::set_tool_pinned(window, state, tool_id, enabled)?;
            let (topmost, pinned, _, _) = shell::window_state_view(state, tool_id);
            Ok(json!({ "topmost": topmost, "pinned": pinned }))
        }
        "window.openVersionManager" => {
            require_tool_window(label, tool_id)?;
            shell::open_version_manager(app, state, tool_id)?;
            Ok(json!({ "ok": true }))
        }
        "window.saveChipPosition" => {
            require_tool_window(label, tool_id)?;
            let left = read_number(payload, "left")?;
            let top = read_number(payload, "top")?;
            shell::save_chip_position(state, tool_id, left, top)?;
            let (topmost, pinned, chip_left, chip_top) = shell::window_state_view(state, tool_id);
            Ok(json!({
                "topmost": topmost, "pinned": pinned,
                "chipLeft": chip_left, "chipTop": chip_top
            }))
        }
        "manager.getServerInfo" => Ok(json!({
            "version": env!("CARGO_PKG_VERSION"),
            "port": state.port(),
            "platform": std::env::consts::OS,
            "webView": if cfg!(windows) { "WebView2（Chromium）" } else if cfg!(target_os = "macos") { "WKWebView" } else { "WebKitGTK" },
            "webViewRuntimeVersion": webview_runtime_version(),
            "nativeVersion": format!("Rust + Tauri {}", env!("CARGO_PKG_VERSION")),
            "dataDirectory": state.paths.root.to_string_lossy()
        })),
        "manager.listTools" => Ok(list_tools(state)),
        "manager.launcherList" => Ok(serde_json::to_value(build_launcher_list(
            LauncherSources {
                tools: &state.tools,
                apps: &state.apps,
                web_tools: &state.web_tools,
                program_index: &state.program_index.read(),
                usage: &state.usage,
                hidden: &state.hidden,
                pinned: &state.pinned,
                icon_cache_dir: &state.paths.icon_cache_dir(),
            },
        ))?),
        "manager.openTool" => {
            let target_id = read_string(payload, "toolId")?;
            let tool = state
                .tools
                .find(&target_id)
                .filter(|tool| tool.is_valid)
                .ok_or_else(|| {
                    BridgeError::new(
                        "TOOL_NOT_FOUND",
                        format!("工具不存在或 manifest 无效：{target_id}"),
                    )
                })?;
            shell::open_tool_window(app, state, &tool)?;
            state.usage.record(&target_id);
            Ok(json!({ "ok": true }))
        }
        "manager.deleteTool" => {
            let target_id = read_string(payload, "toolId")?;
            let delete_storage = read_optional_bool(payload, "deleteStorage").unwrap_or(false);
            let tool = state.tools.find(&target_id).ok_or_else(|| {
                BridgeError::new("TOOL_NOT_FOUND", format!("工具不存在：{target_id}"))
            })?;
            if let Some(window) = app.get_webview_window(&format!("tool-{target_id}")) {
                let _ = window.close();
            }
            let mut last_error = None;
            for _ in 0..20 {
                match std::fs::remove_dir_all(&tool.directory) {
                    Ok(_) => {
                        last_error = None;
                        break;
                    }
                    Err(error) => {
                        last_error = Some(error);
                        std::thread::sleep(std::time::Duration::from_millis(50));
                    }
                }
            }
            if let Some(error) = last_error {
                return Err(BridgeError::new(
                    "TOOL_DELETE_FAILED",
                    format!("删除失败：{error}"),
                ));
            }
            if delete_storage {
                state.storage.delete(&target_id);
            }
            state.tools.remove(&target_id);
            state.pinned.forget("tool", &target_id);
            state.revoke_embed_tokens(&target_id);
            Ok(json!({ "ok": true }))
        }
        "manager.renameTool" => {
            let target_id = read_string(payload, "toolId")?;
            let name = read_string(payload, "name")?;
            state.tools.rename(&target_id, &name)?;
            Ok(json!({ "ok": true }))
        }
        "manager.listVersions" => {
            let target_id = read_string(payload, "toolId")?;
            let tool = require_tool(state, &target_id)?;
            Ok(json!({
                "toolId": target_id,
                "activeVersion": tool.manifest.version,
                "versions": tool.versions
            }))
        }
        "manager.switchVersion" => {
            let target_id = read_string(payload, "toolId")?;
            let version = read_string(payload, "version")?;
            let tool = state.tools.switch_version(&target_id, &version)?;
            if !tool.is_valid {
                return Err(BridgeError::new(
                    "VERSION_SWITCH_FAILED",
                    format!(
                        "版本 {version} 切换后未通过校验：{}",
                        tool.errors.join("；")
                    ),
                ));
            }
            let _ = app.emit("launcher-changed", ());
            Ok(json!({ "ok": true }))
        }
        "manager.deleteVersion" => {
            let target_id = read_string(payload, "toolId")?;
            let version = read_string(payload, "version")?;
            state.tools.delete_version(&target_id, &version)?;
            Ok(json!({ "ok": true }))
        }
        "manager.openContent" => {
            let view = read_string(payload, "view")?;
            if !matches!(
                view.as_str(),
                "tool" | "version" | "create" | "import" | "web"
            ) {
                return Err(BridgeError::bad_request(
                    "view 必须是 tool / version / create / import / web",
                ));
            }
            let target_id = if matches!(view.as_str(), "tool" | "version") {
                Some(read_string(payload, "toolId")?)
            } else {
                None
            };
            if let Some(id) = target_id.as_deref() {
                let tool = require_tool(state, id)?;
                if !tool.is_valid {
                    return Err(BridgeError::new(
                        "TOOL_NOT_FOUND",
                        "工具不存在或 manifest 无效",
                    ));
                }
                if view == "tool" {
                    state.usage.record(id);
                }
            }
            shell::open_content_window(app, state, &view, target_id.as_deref())?;
            Ok(json!({ "ok": true }))
        }
        "manager.closeContent" => {
            shell::close_content_window(app);
            Ok(json!({ "ok": true }))
        }
        "manager.getContentWindowState" => {
            let config = state.config_snapshot();
            Ok(json!({
                "topmost": config.shell.content_window_topmost,
                "pinned": config.shell.content_window_pinned
            }))
        }
        "manager.setContentWindowTopmost" => {
            let enabled = read_bool(payload, "enabled")?;
            if let Some(window) = app.get_webview_window(shell::CONTENT_LABEL) {
                let _ = window.set_always_on_top(enabled);
            }
            state
                .config
                .update(|config| config.shell.content_window_topmost = enabled);
            let config = state.config_snapshot();
            Ok(json!({
                "topmost": config.shell.content_window_topmost,
                "pinned": config.shell.content_window_pinned
            }))
        }
        "manager.setContentWindowPinned" => {
            let enabled = read_bool(payload, "enabled")?;
            if let Some(window) = app.get_webview_window(shell::CONTENT_LABEL) {
                let _ = window.set_resizable(!enabled);
                #[cfg(windows)]
                crate::window_pin::set_position_locked(&window, enabled);
            }
            state
                .config
                .update(|config| config.shell.content_window_pinned = enabled);
            let config = state.config_snapshot();
            Ok(json!({
                "topmost": config.shell.content_window_topmost,
                "pinned": config.shell.content_window_pinned
            }))
        }
        "manager.openSettings" => {
            shell::open_settings_window(app, state)?;
            Ok(json!({ "ok": true }))
        }
        "manager.embedTool" => {
            let target_id = read_string(payload, "toolId")?;
            let tool = require_tool(state, &target_id)?;
            if !tool.is_valid {
                return Err(BridgeError::new(
                    "TOOL_NOT_FOUND",
                    "工具不存在或 manifest 无效",
                ));
            }
            let token = state.register_embed_token(&target_id);
            let url = format!(
                "{}?bt={}",
                tool_url(state.port(), &target_id, tool.entry()),
                token
            );
            Ok(json!({
                "toolId": tool.manifest.id,
                "name": tool.name(),
                "url": url,
                "permissions": tool.permissions
            }))
        }
        "manager.addWebTool" => {
            let name = read_string(payload, "name")?;
            let url = read_string(payload, "url")?;
            let category = read_optional_string(payload, "category");
            let glyph = read_optional_string(payload, "glyph");
            let description = read_optional_string(payload, "description");
            let web = state.web_store.add(
                &name,
                &url,
                category.as_deref(),
                glyph.as_deref(),
                description.as_deref(),
            )?;
            state.usage.ensure_seeded([web.entry.id.clone()]);
            Ok(json!({ "ok": true, "webId": web.entry.id }))
        }
        "manager.deleteWebTool" => {
            let web_id = read_string(payload, "webId")?;
            state.web_store.delete(&web_id)?;
            state.pinned.forget("web", &web_id);
            if let Some(window) = app.get_webview_window(&format!("web-{web_id}")) {
                let _ = window.close();
            }
            Ok(json!({ "ok": true }))
        }
        "manager.renameWebTool" => {
            let web_id = read_string(payload, "webId")?;
            let name = read_string(payload, "name")?;
            state.web_store.rename(&web_id, &name)?;
            Ok(json!({ "ok": true }))
        }
        "manager.openWebTool" => {
            let web_id = read_string(payload, "webId")?;
            let web = state
                .web_tools
                .find(&web_id)
                .filter(|web| web.is_valid)
                .ok_or_else(|| {
                    BridgeError::new(
                        "WEB_NOT_FOUND",
                        format!("在线工具不存在或配置无效：{web_id}"),
                    )
                })?;
            shell::open_web_tool_window(app, state, &web)?;
            state.usage.record(&web_id);
            Ok(json!({ "ok": true }))
        }
        "manager.addApp" => {
            let name = read_string(payload, "name")?;
            let target = read_string(payload, "target")?;
            let app_entry = state.app_store.add(
                &name,
                &target,
                read_optional_string(payload, "arguments").as_deref(),
                read_optional_string(payload, "workingDirectory").as_deref(),
                read_optional_string(payload, "category").as_deref(),
                read_optional_string(payload, "glyph").as_deref(),
            )?;
            state.usage.ensure_seeded([app_entry.entry.id.clone()]);
            Ok(json!({ "id": app_entry.entry.id }))
        }
        "manager.deleteApp" => {
            let app_id = read_string(payload, "appId")?;
            state.app_store.delete(&app_id)?;
            state.pinned.forget("app", &app_id);
            Ok(json!({ "ok": true }))
        }
        "manager.launchApp" => {
            let app_id = read_string(payload, "appId")?;
            let app_entry = state.apps.find(&app_id).ok_or_else(|| {
                BridgeError::new("APP_NOT_FOUND", format!("未找到启动项：{app_id}"))
            })?;
            let pid = shell::launch_entry(state, &app_entry)?;
            Ok(json!({
                "appId": app_id,
                "appName": app_entry.name(),
                "started": true,
                "processId": pid,
                "message": null
            }))
        }
        "manager.listApps" => Ok(json!({
            "apps": state.apps.all().into_iter().map(|app| json!({
                "id": app.entry.id,
                "name": app.name(),
                "target": app.entry.target,
                "category": app.entry.category,
                "glyph": app.entry.glyph,
                "isValid": app.is_valid,
                "targetExists": app.target_exists(),
                "arguments": app.entry.arguments,
                "iconUrl": null,
                "searchKey": build_search_key(app.name(), app.id(), Some(app.entry.target.as_str()))
            })).collect::<Vec<_>>()
        })),
        "manager.pickProgram" => {
            let file = rfd::FileDialog::new()
                .add_filter("程序", &["exe", "lnk", "bat", "cmd", "app", "desktop"])
                .pick_file();
            Ok(json!({ "path": file.map(|path| path.to_string_lossy().into_owned()) }))
        }
        "manager.pickImportFile" => {
            let picked = crate::item_package::pick_import_file()?;
            Ok(serde_json::to_value(picked)?)
        }
        "manager.importBackupFile" => {
            let file = rfd::FileDialog::new()
                .add_filter("QTools 备份包", &["zip"])
                .pick_file();
            let Some(file) = file else {
                return Ok(json!({ "name": null }));
            };
            let name = state.backup.import_file(&file.to_string_lossy())?;
            Ok(json!({ "name": name }))
        }
        "manager.openBackupDirectory" => {
            state.backup.open_directory()?;
            Ok(json!({ "ok": true }))
        }
        "manager.setItemPinned" => {
            let kind = read_string(payload, "kind")?;
            let id = read_string(payload, "id")?;
            if kind != "tool" {
                return Err(BridgeError::bad_request("当前仅支持小工具置顶"));
            }
            let pinned = read_bool(payload, "pinned")?;
            state.pinned.set_pinned(&kind, &id, pinned);
            Ok(json!({ "ok": true }))
        }
        "manager.hideProgram" => {
            let program_id = read_string(payload, "programId")?;
            state.hidden.hide(&program_id);
            Ok(json!({ "ok": true }))
        }
        "manager.unhideProgram" => {
            let program_id = read_string(payload, "programId")?;
            state.hidden.unhide(&program_id);
            Ok(json!({ "ok": true }))
        }
        "manager.revealItem" => {
            let kind = read_string(payload, "kind")?;
            let id = read_string(payload, "id")?;
            let target = find_item_target(state, &kind, &id)
                .ok_or_else(|| BridgeError::new("ITEM_NOT_FOUND", "未找到条目目标"))?;
            crate::launcher::reveal(&target)?;
            Ok(json!({ "ok": true }))
        }
        "manager.getTheme" => Ok(theme_view(state)),
        "manager.setTheme" => {
            let theme = read_string(payload, "theme")?;
            let normalized = normalize_theme(&theme).to_string();
            if theme != normalized {
                return Err(BridgeError::bad_request(
                    "theme 必须是 system / light / dark",
                ));
            }
            state
                .config
                .update(|config| config.shell.theme = normalized.clone());
            crate::window_theme::broadcast(app);
            let _ = app.emit("theme-changed", normalized.clone());
            Ok(theme_view(state))
        }
        "manager.getBackupConfig" => Ok(backup_config_view(state)),
        "manager.setBackupConfig" => {
            let git_remote_url = read_optional_string(payload, "gitRemoteUrl");
            state.config.update(|config| {
                config.backup.git_remote_url = git_remote_url.clone();
            });
            let _ = git_remote_url;
            Ok(backup_config_view(state))
        }
        "manager.setHotkeyEnabled" => {
            let enabled = read_bool(payload, "enabled")?;
            state
                .config
                .update(|config| config.shell.hotkey_enabled = enabled);
            if enabled {
                crate::hotkey::start(app.clone());
            } else {
                crate::hotkey::stop();
            }
            Ok(backup_config_view(state))
        }
        "manager.setTrayMenuItems" => {
            let count = read_i64(payload, "count")?;
            if !(1..=20).contains(&count) {
                return Err(BridgeError::bad_request("count 必填，且为 1~20 的整数"));
            }
            state
                .config
                .update(|config| config.shell.tray_menu_items = count as i32);
            Ok(backup_config_view(state))
        }
        "manager.trayMenuList" => {
            let config = state.config_snapshot();
            let list = build_launcher_list(LauncherSources {
                tools: &state.tools,
                apps: &state.apps,
                web_tools: &state.web_tools,
                program_index: &state.program_index.read(),
                usage: &state.usage,
                hidden: &state.hidden,
                pinned: &state.pinned,
                icon_cache_dir: &state.paths.icon_cache_dir(),
            });
            let items: Vec<Value> = list
                .items
                .into_iter()
                .take(config.shell.tray_menu_items.clamp(1, 20) as usize)
                .map(|item| {
                    let label = match item.kind.as_str() {
                        "tool" => "小工具",
                        "web" => "在线工具",
                        _ => "本机程序",
                    };
                    json!({
                        "kind": item.kind, "id": item.id, "name": item.name,
                        "kindLabel": label, "glyph": item.glyph, "iconUrl": item.icon_url
                    })
                })
                .collect();
            Ok(json!({ "items": items }))
        }
        "manager.trayMenuReady" => {
            shell::show_tray_menu_window(app, state);
            Ok(json!({ "ok": true }))
        }
        "manager.hideTrayMenu" => {
            shell::hide_tray_menu(app, state);
            Ok(json!({ "ok": true }))
        }
        "manager.exitApp" => {
            state
                .exit_requested
                .store(true, std::sync::atomic::Ordering::SeqCst);
            app.exit(0);
            Ok(json!({ "ok": true }))
        }
        "manager.cancelRequest" => {
            let id = read_string(payload, "id")?;
            let cancelled = if let Some(sender) = state.pending_cancel.lock().get(&id) {
                sender.send(true).is_ok()
            } else {
                false
            };
            Ok(json!({ "ok": cancelled }))
        }
        "manager.reindexPrograms" => {
            crate::program_index::start_background_refresh(app.clone(), state.clone());
            Ok(json!({ "ok": true }))
        }
        "manager.discoverPrograms" => {
            let programs: Vec<Value> = crate::program_index::discover()
                .into_iter()
                .map(|program| {
                    json!({
                        "name": program.name,
                        "target": program.target,
                        "source": program.source,
                        "category": program.category
                    })
                })
                .collect();
            Ok(json!({ "programs": programs }))
        }
        "manager.getAiConfig" => {
            let config = state.config_snapshot();
            Ok(json!({
                "configured": state.secrets.is_configured() && !config.ai.base_url.trim().is_empty(),
                "baseUrl": config.ai.base_url,
                "model": config.ai.model,
                "temperature": config.ai.temperature,
                "timeoutSeconds": config.ai.timeout_seconds,
                "secretStoreMode": state.secrets.mode_name()
            }))
        }
        "manager.setAiConfig" => {
            let base_url = read_optional_string(payload, "baseUrl");
            if let Some(value) = base_url.as_deref() {
                if !(value.starts_with("http://") || value.starts_with("https://")) {
                    return Err(BridgeError::bad_request("baseUrl 必须是绝对 HTTP(S) 地址"));
                }
            }
            let model = read_optional_string(payload, "model");
            let temperature = payload.get("temperature").and_then(Value::as_f64);
            let timeout_seconds = payload.get("timeoutSeconds").and_then(Value::as_i64);
            if let Some(value) = temperature {
                if !(0.0..=2.0).contains(&value) || !value.is_finite() {
                    return Err(BridgeError::bad_request(
                        "temperature 必须是 0~2 之间的有限数值",
                    ));
                }
            }
            if let Some(value) = timeout_seconds {
                if !(1..=600).contains(&value) {
                    return Err(BridgeError::bad_request(
                        "timeoutSeconds 必须是 1~600 之间的整数",
                    ));
                }
            }
            let api_key = payload
                .get("apiKey")
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|value| !value.is_empty());
            let clear_key = payload
                .get("clearKey")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            if clear_key {
                state.secrets.clear();
            } else if let Some(api_key) = api_key {
                state.secrets.set(api_key)?;
            }
            state.config.update(|config| {
                if let Some(base_url) = base_url.as_deref().filter(|value| !value.trim().is_empty())
                {
                    config.ai.base_url = base_url.trim().to_string();
                }
                if let Some(model) = model.as_deref().filter(|value| !value.trim().is_empty()) {
                    config.ai.model = model.trim().to_string();
                }
                if let Some(temperature) = temperature {
                    config.ai.temperature = temperature;
                }
                if let Some(timeout_seconds) = timeout_seconds {
                    config.ai.timeout_seconds = timeout_seconds as u64;
                }
            });
            Ok(json!({ "ok": true }))
        }
        "ai.chat" => {
            let messages = read_chat_messages(payload)?;
            let temperature = payload.get("temperature").and_then(Value::as_f64);
            let config = state.config_snapshot();
            let Some(api_key) = state.secrets.get() else {
                return Err(BridgeError::new(
                    "AI_NOT_CONFIGURED",
                    "宿主尚未配置 AI 接口（baseUrl / model / API Key）",
                ));
            };
            let (cancel, receiver) = tokio::sync::watch::channel(false);
            state
                .pending_cancel
                .lock()
                .insert(request_id.to_string(), cancel);
            let result =
                crate::ai::chat(&config.ai, &api_key, &messages, temperature, receiver).await;
            state.pending_cancel.lock().remove(request_id);
            let content = result?;
            Ok(json!({ "content": content }))
        }
        "files.save" => {
            let tool = require_tool(state, tool_id)?;
            let name = read_string(payload, "name")?;
            let data = crate::files::decode_base64(&read_string(payload, "dataBase64")?)?;
            let content_type = read_optional_string(payload, "contentType");
            let files = crate::files::ToolFiles::new(&tool.directory);
            let (size, _) = files.save(&name, &data, content_type.as_deref())?;
            Ok(json!({
                "name": name,
                "sizeBytes": size,
                "url": format!("/tools/{}/files/{}", tool_id, urlencoding::encode(&name))
            }))
        }
        "files.list" => {
            let tool = require_tool(state, tool_id)?;
            let files = crate::files::ToolFiles::new(&tool.directory);
            let items: Vec<Value> = files
                .list()
                .into_iter()
                .map(|(name, size, modified)| {
                    json!({
                        "name": name, "sizeBytes": size, "modifiedUtc": modified
                    })
                })
                .collect();
            Ok(json!({ "files": items }))
        }
        "files.read" => {
            let tool = require_tool(state, tool_id)?;
            let name = read_string(payload, "name")?;
            let files = crate::files::ToolFiles::new(&tool.directory);
            let (data, content_type) = files.read(&name)?;
            Ok(json!({
                "name": name,
                "contentType": content_type,
                "dataBase64": crate::files::encode_base64(&data)
            }))
        }
        "files.delete" => {
            let tool = require_tool(state, tool_id)?;
            let name = read_string(payload, "name")?;
            let files = crate::files::ToolFiles::new(&tool.directory);
            Ok(json!({ "deleted": files.delete(&name)? }))
        }
        "manager.generateTool" => handle_generate_tool(state, window, request_id, payload).await,
        "manager.generateUpdate" => {
            handle_generate_update(state, window, request_id, payload).await
        }
        "manager.fixTool" => handle_fix_tool(state, window, request_id, payload).await,
        "manager.createTool" => handle_create_tool(state, payload),
        "manager.applyUpdate" => {
            let result = handle_apply_update(state, payload);
            if result.is_ok() {
                let _ = app.emit("launcher-changed", ());
            }
            result
        }
        "manager.importTool" => handle_import_tool(state, payload),
        "manager.createToolShortcut" => {
            crate::shortcut::create_tool_shortcut(state, &read_string(payload, "toolId")?)
        }
        "manager.exportItem" => {
            let kind = read_string(payload, "kind")?;
            let id = read_string(payload, "id")?;
            let result = crate::item_package::export_item(state, &kind, &id)?;
            Ok(serde_json::to_value(result)?)
        }
        "manager.importItemPackage" => {
            let path = read_string(payload, "path")?;
            let accept = read_optional_bool(payload, "acceptHighSeverity").unwrap_or(false);
            let result = crate::item_package::import_package(state, &path, accept)?;
            if result.saved {
                let _ = app.emit("launcher-changed", ());
            }
            Ok(serde_json::to_value(result)?)
        }
        "manager.listBackups" => Ok(json!({ "backups": state.backup.list() })),
        "manager.createBackup" => {
            let note = read_optional_string(payload, "note");
            let (name, size, created_at) = state.backup.create(note.as_deref())?;
            Ok(json!({ "name": name, "sizeBytes": size, "createdAtUtc": created_at }))
        }
        "manager.restoreBackup" => {
            let name = read_string(payload, "name")?;
            let (_, safety) = state.backup.restore(&name)?;
            reload_after_restore(app, state);
            Ok(json!({ "ok": true, "safetyBackupName": safety }))
        }
        "manager.deleteBackup" => {
            let name = read_string(payload, "name")?;
            state.backup.delete(&name)?;
            Ok(json!({ "ok": true }))
        }

        "manager.gitPush" => {
            let remote = read_string(payload, "gitRemoteUrl")?;
            crate::git_backup::validate_remote_url(&remote)?;
            let mut progress = |delta: &str| send_stream(window, request_id, delta, false);
            let result = crate::git_backup::push(
                &state.paths.root,
                &state.paths.data_dir(),
                &remote,
                &mut progress,
            );
            let status = match &result {
                Ok((_, _, message)) => message.clone(),
                Err(error) => error.message.clone(),
            };
            state.config.update(|config| {
                config.backup.git_remote_url = Some(remote.clone());
                if result.is_ok() {
                    config.backup.last_push_utc = Some(chrono::Utc::now().to_rfc3339());
                }
                config.backup.last_push_status = Some(status.clone());
            });
            let (pushed, committed, message) = result?;
            Ok(json!({ "pushed": pushed, "committed": committed, "message": message }))
        }
        "manager.gitRestore" => {
            let remote = read_string(payload, "gitRemoteUrl")?;
            crate::git_backup::validate_remote_url(&remote)?;
            let mut progress = |delta: &str| send_stream(window, request_id, delta, false);
            let restored = crate::git_backup::restore(
                &state.backup,
                &state.paths.data_dir(),
                &remote,
                &mut progress,
            );
            match &restored {
                Ok((_, safety)) => {
                    reload_after_restore(app, state);
                    Ok(json!({ "ok": true, "safetyBackupName": safety }))
                }
                Err(error) => Err(error.clone()),
            }
        }
        "manager.notify" => {
            let title = read_string(payload, "title")?;
            let body = read_optional_string(payload, "body").unwrap_or_default();
            shell::notify(app, &title, &body)?;
            Ok(json!({ "ok": true }))
        }
        _ => Err(BridgeError::unknown_method(method)),
    }
}

fn reload_after_restore(app: &AppHandle, state: &Arc<AppState>) {
    state.config.reload();
    state.tools.rescan();
    state.apps.rescan();
    state.web_tools.rescan();
    state.config.reload();
    let _ = app.emit("launcher-changed", ());
    crate::window_theme::broadcast(app);
}

fn require_ai(state: &Arc<AppState>) -> BridgeResult<(String, crate::config::AiConfigSection)> {
    let config = state.config_snapshot();
    let Some(api_key) = state.secrets.get() else {
        return Err(BridgeError::new(
            "AI_NOT_CONFIGURED",
            "宿主尚未配置 AI 接口（baseUrl / model / API Key）",
        ));
    };
    if config.ai.base_url.trim().is_empty() || config.ai.model.trim().is_empty() {
        return Err(BridgeError::new(
            "AI_NOT_CONFIGURED",
            "宿主尚未配置 AI 接口（baseUrl / model / API Key）",
        ));
    }
    Ok((api_key, config.ai))
}

struct GeneratedInput {
    manifest: crate::tools::ToolManifest,
    index_html: String,
}

fn read_generated_value(payload: &Value, key: &str) -> BridgeResult<GeneratedInput> {
    let generated = payload
        .get(key)
        .filter(|value| value.is_object())
        .ok_or_else(|| BridgeError::bad_request(format!("payload.{key} 必填且为对象")))?;
    let manifest_value = generated
        .get("manifest")
        .filter(|value| value.is_object())
        .ok_or_else(|| BridgeError::bad_request(format!("payload.{key}.manifest 必填")))?;
    let manifest = crate::creator::manifest_from_value(manifest_value)?;
    let index_html = generated
        .get("indexHtml")
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| BridgeError::bad_request(format!("payload.{key}.indexHtml 必填")))?
        .to_string();
    Ok(GeneratedInput {
        manifest,
        index_html,
    })
}

pub(crate) fn build_generated(
    raw: &str,
    identity: Option<(&crate::tools::LoadedTool, String)>,
) -> BridgeResult<crate::creator::GeneratedResult> {
    let parsed = crate::creator::parse_ai_output(raw)?;
    let mut manifest = crate::creator::manifest_from_value(&parsed.manifest)?;
    if let Some((tool, version)) = identity {
        manifest.id = tool.manifest.id.clone();
        manifest.entry = "index.html".into();
        manifest.name = tool.name().to_string();
        manifest.version = version;
    }
    let (findings, has_fatal) = crate::creator::scan(&manifest, &parsed.index_html);
    let requires_explicit_acceptance = findings.iter().any(|finding| finding.severity == 1);
    Ok(crate::creator::GeneratedResult {
        manifest,
        index_html: parsed.index_html,
        findings,
        has_fatal,
        requires_explicit_acceptance,
    })
}

async fn handle_generate_tool(
    state: &Arc<AppState>,
    window: &WebviewWindow,
    request_id: &str,
    payload: &Value,
) -> BridgeResult<Value> {
    let prompt = read_string(payload, "prompt")?;
    if prompt.trim().is_empty() {
        return Err(BridgeError::new("AI_BAD_REQUEST", "需求描述不能为空"));
    }
    if prompt.chars().count() > 4000 {
        return Err(BridgeError::new(
            "AI_BAD_REQUEST",
            "需求描述不超过 4000 字符",
        ));
    }
    let (api_key, config) = require_ai(state)?;
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
    let (cancel, receiver) = tokio::sync::watch::channel(false);
    state
        .pending_cancel
        .lock()
        .insert(request_id.to_string(), cancel);
    let mut on_delta = |delta: &str, reasoning: bool| {
        send_stream(window, request_id, delta, reasoning);
    };
    let raw =
        crate::ai::chat_streaming(&config, &api_key, &messages, None, &mut on_delta, receiver)
            .await;
    state.pending_cancel.lock().remove(request_id);
    let raw = raw?;
    let result = build_generated(&raw, None)?;
    Ok(result.to_view())
}

async fn handle_generate_update(
    state: &Arc<AppState>,
    window: &WebviewWindow,
    request_id: &str,
    payload: &Value,
) -> BridgeResult<Value> {
    let tool_id = read_string(payload, "toolId")?;
    let instruction = read_string(payload, "instruction")?;
    if instruction.trim().is_empty() {
        return Err(BridgeError::new("AI_BAD_REQUEST", "修改诉求不能为空"));
    }
    if instruction.chars().count() > 4000 {
        return Err(BridgeError::new(
            "AI_BAD_REQUEST",
            "修改诉求不超过 4000 字符",
        ));
    }
    let tool = require_tool(state, &tool_id)?;
    let current_html =
        std::fs::read_to_string(tool.directory.join(tool.entry())).map_err(|error| {
            BridgeError::new("ENTRY_FILE_MISSING", format!("读取当前版本失败：{error}"))
        })?;
    if current_html.len() > 512 * 1024 {
        return Err(BridgeError::new(
            "AI_BAD_REQUEST",
            "当前版本源码过大（>512KB），无法交给 AI 更新",
        ));
    }
    let bump = read_optional_string(payload, "versionBump").unwrap_or_else(|| "patch".into());
    let next_version = crate::creator::next_version(&tool.versions, &bump);
    let manifest_json = serde_json::to_string(&tool.manifest)?;
    let user_message = format!(
        "下面是一个已存在工具的当前版本，请按我的要求改造成新版本。\n\n【工具标识（必须原样保留，不得更改）】\nid = {}\n当前版本 = {}\n新版本号 = {}（manifest.version 必须填这个）\n\n【当前 manifest.json】\n{}\n\n【当前 index.html】\n{}\n\n【我的修改要求】\n{}\n\n请输出完整的 JSON 对象（格式同系统提示）：manifest 用上面这份、只把 version 改成「新版本号」，indexHtml 输出改造后的完整单文件 HTML。其余字段（id/name/entry/window/createdAt/allowEval/csp）保持不变。permissions 按改造后代码的实际用法重新确定，用不到的要去掉，新用到的要补上。",
        tool.manifest.id,
        tool.manifest.version,
        next_version,
        manifest_json,
        current_html,
        instruction.trim()
    );
    let (api_key, config) = require_ai(state)?;
    let messages = vec![
        crate::ai::ChatMessage {
            role: "system".into(),
            content: crate::creator::SYSTEM_PROMPT.into(),
        },
        crate::ai::ChatMessage {
            role: "user".into(),
            content: user_message,
        },
    ];
    let (cancel, receiver) = tokio::sync::watch::channel(false);
    state
        .pending_cancel
        .lock()
        .insert(request_id.to_string(), cancel);
    let mut on_delta = |delta: &str, reasoning: bool| {
        send_stream(window, request_id, delta, reasoning);
    };
    let raw =
        crate::ai::chat_streaming(&config, &api_key, &messages, None, &mut on_delta, receiver)
            .await;
    state.pending_cancel.lock().remove(request_id);
    let raw = raw?;
    let result = build_generated(&raw, Some((&tool, next_version)))?;
    Ok(result.to_view())
}

async fn handle_fix_tool(
    state: &Arc<AppState>,
    window: &WebviewWindow,
    request_id: &str,
    payload: &Value,
) -> BridgeResult<Value> {
    let tool_id = read_string(payload, "toolId")?;
    let error_description = read_string(payload, "error")?;
    let tool = require_tool(state, &tool_id)?;
    let current_html =
        std::fs::read_to_string(tool.directory.join(tool.entry())).map_err(|error| {
            BridgeError::new("ENTRY_FILE_MISSING", format!("读取当前版本失败：{error}"))
        })?;
    let manifest_json = serde_json::to_string(&tool.manifest)?;
    let user_message = format!(
        "请修复以下 QTools 工具。用户报告的问题：\n---\n{}\n---\n\n当前 manifest.json：\n---\n{}\n---\n\n当前 {}：\n---\n{}\n---\n\n请输出修复后的完整 JSON（manifest + indexHtml），规则与之前一致。",
        error_description.trim(),
        manifest_json,
        tool.entry(),
        current_html
    );
    let (api_key, config) = require_ai(state)?;
    let messages = vec![
        crate::ai::ChatMessage {
            role: "system".into(),
            content: crate::creator::SYSTEM_PROMPT.into(),
        },
        crate::ai::ChatMessage {
            role: "user".into(),
            content: user_message,
        },
    ];
    let (cancel, receiver) = tokio::sync::watch::channel(false);
    state
        .pending_cancel
        .lock()
        .insert(request_id.to_string(), cancel);
    let mut on_delta = |delta: &str, reasoning: bool| {
        send_stream(window, request_id, delta, reasoning);
    };
    let raw =
        crate::ai::chat_streaming(&config, &api_key, &messages, None, &mut on_delta, receiver)
            .await;
    state.pending_cancel.lock().remove(request_id);
    let raw = raw?;
    let result = build_generated(&raw, Some((&tool, tool.manifest.version.clone())))?;
    if result.manifest.id != tool.manifest.id {
        return Err(BridgeError::new(
            "MANIFEST_INVALID",
            format!(
                "修复结果的工具 ID 必须是 {}（收到 {}）",
                tool.manifest.id, result.manifest.id
            ),
        ));
    }
    Ok(result.to_view())
}

fn handle_create_tool(state: &Arc<AppState>, payload: &Value) -> BridgeResult<Value> {
    let generated = read_generated_value(payload, "generated")?;
    let accept = read_optional_bool(payload, "acceptHighSeverity").unwrap_or(false);
    let (findings, _) = crate::creator::scan(&generated.manifest, &generated.index_html);
    let tools_root = state.paths.tools_dir();
    let tool_id = crate::creator::create_tool(
        crate::creator::CreateDeps {
            registry: &state.tools,
            tools_root: &tools_root,
        },
        &generated.manifest,
        &generated.index_html,
        &findings,
        accept,
    )?;
    state.usage.ensure_seeded([tool_id.clone()]);
    Ok(json!({ "toolId": tool_id, "opened": false }))
}

fn handle_apply_update(state: &Arc<AppState>, payload: &Value) -> BridgeResult<Value> {
    let tool_id = read_string(payload, "toolId")?;
    let generated = read_generated_value(payload, "generated")?;
    let accept = read_optional_bool(payload, "acceptHighSeverity").unwrap_or(false);
    let tool = require_tool(state, &tool_id)?;
    let (findings, _) = crate::creator::scan(&generated.manifest, &generated.index_html);
    crate::creator::apply_update(
        &state.tools,
        &tool,
        &generated.manifest,
        &generated.index_html,
        &findings,
        accept,
    )?;
    Ok(json!({ "ok": true }))
}

fn handle_import_tool(state: &Arc<AppState>, payload: &Value) -> BridgeResult<Value> {
    let name = read_string(payload, "name")?;
    let html = read_string(payload, "html")?;
    let description = read_optional_string(payload, "description");
    let category = read_optional_string(payload, "category");
    let glyph = read_optional_string(payload, "glyph");
    let accept = read_optional_bool(payload, "acceptHighSeverity").unwrap_or(false);
    let tools_root = state.paths.tools_dir();
    let result = crate::creator::import_html(
        &state.tools,
        &tools_root,
        &name,
        &html,
        description.as_deref(),
        category.as_deref(),
        glyph.as_deref(),
        accept,
    )?;
    if result.saved {
        state.usage.ensure_seeded([result.tool_id.clone()]);
    }
    Ok(json!({
        "toolId": result.tool_id,
        "toolName": result.tool_name,
        "url": result.url,
        "saved": result.saved,
        "permissions": result.permissions,
        "findings": result.findings,
        "hasFatal": result.has_fatal,
        "requiresExplicitAcceptance": result.requires_explicit_acceptance,
        "externalAssets": result.external_assets
    }))
}

fn require_tool(state: &Arc<AppState>, tool_id: &str) -> BridgeResult<crate::tools::LoadedTool> {
    if !is_valid_tool_id(tool_id) {
        return Err(BridgeError::bad_request(format!("非法 toolId '{tool_id}'")));
    }
    state
        .tools
        .find(tool_id)
        .ok_or_else(|| BridgeError::new("TOOL_NOT_FOUND", format!("未找到工具 '{tool_id}'")))
}

fn require_tool_window(label: &str, tool_id: &str) -> BridgeResult<()> {
    if label == format!("tool-{tool_id}") {
        Ok(())
    } else {
        Err(BridgeError::permission_denied(
            "窗口控制仅允许独立工具窗口自身调用",
        ))
    }
}

fn list_tools(state: &Arc<AppState>) -> Value {
    let tools: Vec<Value> = state
        .tools
        .all()
        .into_iter()
        .map(|tool| {
            json!({
                "id": tool.manifest.id,
                "name": tool.name(),
                "version": tool.manifest.version,
                "description": tool.manifest.description,
                "permissions": tool.permissions,
                "lastModifiedUtc": tool.last_modified_utc,
                "isValid": tool.is_valid,
                "errors": tool.errors,
                "hasStorageData": state.storage.size_bytes(&tool.manifest.id) > 0,
                "searchKey": build_search_key(tool.name(), &tool.manifest.id, tool.manifest.description.as_deref())
            })
        })
        .collect();
    json!({ "tools": tools })
}

fn webview_runtime_version() -> Option<String> {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        const CLIENT_ID: &str = r"{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        for (root, key) in [
            (
                "HKCU",
                format!(r"Software\Microsoft\EdgeUpdate\Clients\{CLIENT_ID}"),
            ),
            (
                "HKLM",
                format!(r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{CLIENT_ID}"),
            ),
            (
                "HKLM",
                format!(r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{CLIENT_ID}"),
            ),
        ] {
            let Ok(output) = std::process::Command::new("reg")
                .args(["query", &format!(r"{root}\{key}"), "/v", "pv"])
                .creation_flags(CREATE_NO_WINDOW)
                .output()
            else {
                continue;
            };
            if !output.status.success() {
                continue;
            }
            let text = String::from_utf8_lossy(&output.stdout);
            for line in text.lines() {
                let mut parts = line.split_whitespace();
                if parts.next() == Some("pv") && parts.next() == Some("REG_SZ") {
                    if let Some(version) = parts.next() {
                        return Some(version.to_string());
                    }
                }
            }
        }
        None
    }
    #[cfg(not(windows))]
    {
        None
    }
}

fn theme_view(state: &Arc<AppState>) -> Value {
    let configured = state.config_snapshot().shell.theme;
    let theme = normalize_theme(&configured);
    json!({ "theme": theme, "dark": is_dark_now(theme) })
}

fn backup_config_view(state: &Arc<AppState>) -> Value {
    let config = state.config_snapshot();
    json!({
        "gitRemoteUrl": config.backup.git_remote_url,
        "lastPushUtc": config.backup.last_push_utc,
        "lastPushStatus": config.backup.last_push_status,
        "hotkeyEnabled": config.shell.hotkey_enabled,
        "hotkeySupported": cfg!(windows),
        "hotkeyRegistered": false,
        "trayMenuItems": config.shell.tray_menu_items
    })
}

fn find_item_target(state: &Arc<AppState>, kind: &str, id: &str) -> Option<String> {
    match kind {
        "tool" => state
            .tools
            .find(id)
            .map(|tool| tool.directory.to_string_lossy().into_owned()),
        "app" => state.apps.find(id).map(|app| app.entry.target),
        "web" => state.web_tools.find(id).map(|web| web.entry.url),
        "program" => state
            .program_index
            .read()
            .entries
            .iter()
            .find(|entry| entry.id == id)
            .map(|entry| entry.target.clone()),
        _ => None,
    }
}

fn read_chat_messages(payload: &Value) -> BridgeResult<Vec<crate::ai::ChatMessage>> {
    let messages = payload
        .get("messages")
        .and_then(Value::as_array)
        .ok_or_else(|| BridgeError::bad_request("payload.messages 必填且为数组"))?;
    let mut result = Vec::with_capacity(messages.len());
    for message in messages {
        let role = message
            .get("role")
            .and_then(Value::as_str)
            .filter(|value| !value.trim().is_empty())
            .ok_or_else(|| BridgeError::bad_request("messages[].role 必填"))?;
        let content = message
            .get("content")
            .and_then(Value::as_str)
            .ok_or_else(|| BridgeError::bad_request("messages[].content 必填"))?;
        result.push(crate::ai::ChatMessage {
            role: role.to_string(),
            content: content.to_string(),
        });
    }
    Ok(result)
}

fn read_string(payload: &Value, key: &str) -> BridgeResult<String> {
    payload
        .get(key)
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| BridgeError::bad_request(format!("payload.{key} 必填且为字符串")))
}

fn read_optional_string(payload: &Value, key: &str) -> Option<String> {
    payload
        .get(key)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn read_bool(payload: &Value, key: &str) -> BridgeResult<bool> {
    payload
        .get(key)
        .and_then(Value::as_bool)
        .ok_or_else(|| BridgeError::bad_request(format!("payload.{key} 必填且为布尔值")))
}

fn read_optional_bool(payload: &Value, key: &str) -> Option<bool> {
    payload.get(key).and_then(Value::as_bool)
}

fn read_number(payload: &Value, key: &str) -> BridgeResult<f64> {
    payload
        .get(key)
        .and_then(Value::as_f64)
        .filter(|value| value.is_finite())
        .ok_or_else(|| BridgeError::bad_request(format!("payload.{key} 必填且为有限数值")))
}

fn read_i64(payload: &Value, key: &str) -> BridgeResult<i64> {
    payload
        .get(key)
        .and_then(Value::as_i64)
        .ok_or_else(|| BridgeError::bad_request(format!("payload.{key} 必填且为整数")))
}

fn error_envelope(request_id: &str, code: &str, message: &str) -> Value {
    json!({
        "id": request_id,
        "error": { "code": code, "message": message }
    })
}

#[cfg(test)]
mod tests {
    use super::method_spec;

    #[test]
    fn manager_methods_are_host_only_and_tool_window_methods_are_not() {
        assert!(method_spec("manager.openTool").unwrap().host_only);
        assert!(method_spec("manager.createTool").is_some());
        assert!(method_spec("manager.importItemPackage").unwrap().host_only);
        assert!(!method_spec("window.getState").unwrap().host_only);
        assert!(!method_spec("window.setPinned").unwrap().host_only);
        assert!(!method_spec("theme.get").unwrap().host_only);
        assert!(method_spec("unknown.method").is_none());
    }
}
