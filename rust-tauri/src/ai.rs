use std::time::Duration;

use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use tokio::sync::watch;

use crate::config::AiConfigSection;
use crate::error::{BridgeError, BridgeResult};

const ANTHROPIC_MAX_TOKENS: i64 = 32768;
const ANTHROPIC_VERSION: &str = "2023-06-01";

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: String,
}

pub fn is_anthropic_endpoint(base_url: &str) -> bool {
    let trimmed = base_url.trim().trim_end_matches('/').to_ascii_lowercase();
    trimmed.ends_with("/anthropic") || trimmed.contains("/anthropic/")
}

pub fn build_chat_completions_url(base_url: &str) -> String {
    let trimmed = base_url.trim().trim_end_matches('/');
    if trimmed.to_ascii_lowercase().ends_with("/chat/completions") {
        trimmed.to_string()
    } else {
        format!("{trimmed}/chat/completions")
    }
}

pub fn build_anthropic_messages_url(base_url: &str) -> String {
    let trimmed = base_url.trim().trim_end_matches('/');
    let lower = trimmed.to_ascii_lowercase();
    if lower.ends_with("/v1/messages") {
        trimmed.to_string()
    } else if lower.ends_with("/v1") {
        format!("{trimmed}/messages")
    } else {
        format!("{trimmed}/v1/messages")
    }
}

pub async fn chat(
    config: &AiConfigSection,
    api_key: &str,
    messages: &[ChatMessage],
    temperature: Option<f64>,
    mut cancel: watch::Receiver<bool>,
) -> BridgeResult<String> {
    if api_key.trim().is_empty() {
        return Err(BridgeError::new(
            "AI_NOT_CONFIGURED",
            "宿主尚未配置 AI API Key",
        ));
    }
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(config.timeout_seconds.clamp(1, 600)))
        .build()
        .map_err(network_error)?;
    if is_anthropic_endpoint(&config.base_url) {
        chat_anthropic(&client, config, api_key, messages, temperature, &mut cancel).await
    } else {
        chat_openai(&client, config, api_key, messages, temperature, &mut cancel).await
    }
}

async fn chat_openai(
    client: &reqwest::Client,
    config: &AiConfigSection,
    api_key: &str,
    messages: &[ChatMessage],
    temperature: Option<f64>,
    cancel: &mut watch::Receiver<bool>,
) -> BridgeResult<String> {
    let body = json!({
        "model": config.model,
        "temperature": (temperature.unwrap_or(config.temperature)).clamp(0.0, 2.0),
        "messages": messages,
    });
    let request = client
        .post(build_chat_completions_url(&config.base_url))
        .bearer_auth(api_key)
        .json(&body)
        .send();
    let response = tokio::select! {
        _ = cancel.changed() => return Err(cancelled()),
        result = request => result.map_err(network_error)?,
    };
    finish_response(response, false).await
}

async fn chat_anthropic(
    client: &reqwest::Client,
    config: &AiConfigSection,
    api_key: &str,
    messages: &[ChatMessage],
    temperature: Option<f64>,
    cancel: &mut watch::Receiver<bool>,
) -> BridgeResult<String> {
    let (system, converted) = split_anthropic_messages(messages);
    let body = json!({
        "model": config.model,
        "max_tokens": ANTHROPIC_MAX_TOKENS,
        "temperature": (temperature.unwrap_or(config.temperature)).clamp(0.0, 1.0),
        "system": system,
        "messages": converted,
    });
    let request = client
        .post(build_anthropic_messages_url(&config.base_url))
        .header("x-api-key", api_key)
        .header("anthropic-version", ANTHROPIC_VERSION)
        .json(&body)
        .send();
    let response = tokio::select! {
        _ = cancel.changed() => return Err(cancelled()),
        result = request => result.map_err(network_error)?,
    };
    finish_response(response, true).await
}

async fn finish_response(response: reqwest::Response, anthropic: bool) -> BridgeResult<String> {
    let status = response.status().as_u16();
    let body = response.text().await.map_err(network_error)?;
    crate::logging::info(&format!(
        "AI 请求完成：HTTP {status}，响应 {} 字节",
        body.len()
    ));
    if !(200..300).contains(&status) {
        return Err(map_http_error(status));
    }
    if anthropic {
        parse_anthropic_content(&body)
    } else {
        parse_openai_content(&body)
    }
}

/// 流式聊天：正文与思考过程分别通过 on_delta 回调推送；空闲超时按配置放宽到至少 60 秒。
pub async fn chat_streaming(
    config: &AiConfigSection,
    api_key: &str,
    messages: &[ChatMessage],
    temperature: Option<f64>,
    on_delta: &mut (dyn FnMut(&str, bool) + Send),
    mut cancel: watch::Receiver<bool>,
) -> BridgeResult<String> {
    if api_key.trim().is_empty() {
        return Err(BridgeError::new(
            "AI_NOT_CONFIGURED",
            "宿主尚未配置 AI API Key",
        ));
    }
    let idle_seconds = config.timeout_seconds.max(60);
    let client = reqwest::Client::builder().build().map_err(network_error)?;
    let anthropic = is_anthropic_endpoint(&config.base_url);

    let request = if anthropic {
        let (system, converted) = split_anthropic_messages(messages);
        client
            .post(build_anthropic_messages_url(&config.base_url))
            .header("x-api-key", api_key)
            .header("anthropic-version", ANTHROPIC_VERSION)
            .header("Accept", "text/event-stream")
            .json(&json!({
                "model": config.model,
                "max_tokens": ANTHROPIC_MAX_TOKENS,
                "temperature": (temperature.unwrap_or(config.temperature)).clamp(0.0, 1.0),
                "system": system,
                "messages": converted,
                "stream": true,
            }))
            .send()
    } else {
        client
            .post(build_chat_completions_url(&config.base_url))
            .bearer_auth(api_key)
            .header("Accept", "text/event-stream")
            .json(&json!({
                "model": config.model,
                "temperature": (temperature.unwrap_or(config.temperature)).clamp(0.0, 2.0),
                "messages": messages,
                "stream": true,
            }))
            .send()
    };

    let response = tokio::select! {
        _ = cancel.changed() => return Err(cancelled()),
        result = request => result.map_err(network_error)?,
    };
    let status = response.status().as_u16();
    if !(200..300).contains(&status) {
        let body = response.text().await.unwrap_or_default();
        crate::logging::info(&format!(
            "AI 流式请求失败：HTTP {status}，响应 {} 字节",
            body.len()
        ));
        return Err(map_http_error(status));
    }

    let is_sse = response
        .headers()
        .get(reqwest::header::CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .map(|value| value.to_ascii_lowercase().contains("text/event-stream"))
        .unwrap_or(false);
    if !is_sse {
        let body = response.text().await.map_err(network_error)?;
        let content = if anthropic {
            parse_anthropic_content(&body)?
        } else {
            parse_openai_content(&body)?
        };
        on_delta(&content, false);
        return Ok(content);
    }

    let mut stream = response.bytes_stream();
    let mut pending = Vec::<u8>::new();
    let mut output = String::new();
    loop {
        let chunk = tokio::select! {
            _ = cancel.changed() => return Err(cancelled()),
            result = tokio::time::timeout(Duration::from_secs(idle_seconds), stream.next()) => {
                match result {
                    Err(_) => return Err(BridgeError::new("AI_TIMEOUT", format!("AI 请求超时（{idle_seconds}s 内未收到数据）"))),
                    Ok(None) => break,
                    Ok(Some(Err(error))) => return Err(network_error(error)),
                    Ok(Some(Ok(chunk))) => chunk,
                }
            }
        };
        pending.extend_from_slice(&chunk);
        while let Some(newline) = pending.iter().position(|byte| *byte == b'\n') {
            let line_bytes: Vec<u8> = pending.drain(..=newline).collect();
            let line = String::from_utf8_lossy(&line_bytes);
            let line = line.trim_end_matches(['\r', '\n']);
            if line.starts_with("data:") {
                let data = line[5..].trim_start();
                if data == "[DONE]" {
                    return finish_stream(output);
                }
                if anthropic {
                    if append_anthropic_chunk(data, &mut output, on_delta)? {
                        return finish_stream(output);
                    }
                } else {
                    append_openai_chunk(data, &mut output, on_delta);
                }
            }
        }
    }
    finish_stream(output)
}

fn finish_stream(output: String) -> BridgeResult<String> {
    if output.trim().is_empty() {
        Err(BridgeError::new(
            "AI_INVALID_RESPONSE",
            "AI 流式响应没有内容",
        ))
    } else {
        Ok(output)
    }
}

fn append_openai_chunk(
    data: &str,
    output: &mut String,
    on_delta: &mut (dyn FnMut(&str, bool) + Send),
) {
    let Ok(value) = serde_json::from_str::<Value>(data) else {
        return;
    };
    let delta = value
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|choices| choices.first())
        .and_then(|choice| choice.get("delta"));
    let Some(delta) = delta else {
        return;
    };
    if let Some(reasoning) = delta.get("reasoning_content").and_then(Value::as_str) {
        if !reasoning.is_empty() {
            on_delta(reasoning, true);
        }
    }
    if let Some(content) = delta.get("content").and_then(Value::as_str) {
        if !content.is_empty() {
            output.push_str(content);
            on_delta(content, false);
        }
    }
}

fn append_anthropic_chunk(
    data: &str,
    output: &mut String,
    on_delta: &mut (dyn FnMut(&str, bool) + Send),
) -> BridgeResult<bool> {
    let Ok(value) = serde_json::from_str::<Value>(data) else {
        return Ok(false);
    };
    let event_type = value
        .get("type")
        .and_then(Value::as_str)
        .unwrap_or_default();
    if event_type == "error" {
        return Err(BridgeError::new("AI_UPSTREAM_ERROR", "AI 流式响应返回错误"));
    }
    if event_type == "message_stop" {
        return Ok(true);
    }
    if event_type != "content_block_delta" {
        return Ok(false);
    }
    let Some(delta) = value.get("delta") else {
        return Ok(false);
    };
    if let Some(thinking) = delta.get("thinking").and_then(Value::as_str) {
        if !thinking.is_empty() {
            on_delta(thinking, true);
        }
    }
    if let Some(text) = delta.get("text").and_then(Value::as_str) {
        if !text.is_empty() {
            output.push_str(text);
            on_delta(text, false);
        }
    }
    Ok(false)
}

fn split_anthropic_messages(messages: &[ChatMessage]) -> (Option<String>, Vec<ChatMessage>) {
    let mut system = Vec::new();
    let mut converted = Vec::new();
    for message in messages {
        if message.role.eq_ignore_ascii_case("system") {
            if !message.content.trim().is_empty() {
                system.push(message.content.clone());
            }
        } else {
            converted.push(message.clone());
        }
    }
    let system = if system.is_empty() {
        None
    } else {
        Some(system.join("\n\n"))
    };
    (system, converted)
}

fn parse_openai_content(body: &str) -> BridgeResult<String> {
    let value: Value = serde_json::from_str(body)
        .map_err(|_| BridgeError::new("AI_INVALID_RESPONSE", "AI 响应不是合法 JSON"))?;
    let content = value
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|choices| choices.first())
        .and_then(|choice| choice.get("message"))
        .and_then(|message| message.get("content"))
        .and_then(Value::as_str)
        .unwrap_or_default();
    if content.trim().is_empty() {
        Err(BridgeError::new(
            "AI_INVALID_RESPONSE",
            "AI 响应缺少 choices[0].message.content",
        ))
    } else {
        Ok(content.to_string())
    }
}

fn parse_anthropic_content(body: &str) -> BridgeResult<String> {
    let value: Value = serde_json::from_str(body)
        .map_err(|_| BridgeError::new("AI_INVALID_RESPONSE", "AI 响应不是合法 JSON"))?;
    let text = value
        .get("content")
        .and_then(Value::as_array)
        .map(|blocks| {
            blocks
                .iter()
                .filter(|block| block.get("type").and_then(Value::as_str) == Some("text"))
                .filter_map(|block| block.get("text").and_then(Value::as_str))
                .collect::<String>()
        })
        .unwrap_or_default();
    if text.trim().is_empty() {
        Err(BridgeError::new(
            "AI_INVALID_RESPONSE",
            "AI 响应缺少文本内容",
        ))
    } else {
        Ok(text)
    }
}

pub fn map_http_error(status: u16) -> BridgeError {
    match status {
        401 | 403 => BridgeError::new(
            "AI_UNAUTHORIZED",
            format!("AI 服务拒绝访问（HTTP {status}）：请检查 API Key"),
        ),
        402 => BridgeError::new(
            "AI_INSUFFICIENT_BALANCE",
            format!("AI 服务账户余额不足（HTTP {status}）：请充值后再试"),
        ),
        404 | 405 => BridgeError::new(
            "AI_INVALID_BASE_URL",
            format!(
                "接口地址无法处理该请求（HTTP {status}）：OpenAI 兼容填 https://api.deepseek.com，Anthropic 兼容填 https://api.deepseek.com/anthropic"
            ),
        ),
        422 => BridgeError::new(
            "AI_BAD_REQUEST",
            format!(
                "AI 服务拒绝请求参数（HTTP {status}）：请检查模型名称（如 deepseek-flash / deepseek-v4-pro）"
            ),
        ),
        429 => BridgeError::new("AI_RATE_LIMITED", "AI 服务限流（HTTP 429）"),
        value if value == 529 || value >= 500 => BridgeError::new(
            "AI_UPSTREAM_ERROR",
            format!("AI 服务内部错误（HTTP {status}）：请稍后重试"),
        ),
        _ => BridgeError::new(
            "AI_BAD_REQUEST",
            format!("AI 服务拒绝请求（HTTP {status}）：请检查接口地址与请求参数"),
        ),
    }
}

fn network_error(error: reqwest::Error) -> BridgeError {
    if error.is_timeout() {
        BridgeError::new("AI_TIMEOUT", "AI 请求超时")
    } else {
        BridgeError::new(
            "AI_NETWORK_ERROR",
            "无法连接或读取 AI 服务，请检查接口地址和网络连接",
        )
    }
}

fn cancelled() -> BridgeError {
    BridgeError::new("AI_CANCELLED", "AI 请求已取消")
}
