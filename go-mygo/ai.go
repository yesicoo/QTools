package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

const anthropicVersion = "2023-06-01"

type ChatMessage struct {
	Role    string `json:"role"`
	Content string `json:"content"`
}

func isAnthropicEndpoint(baseURL string) bool {
	value := strings.ToLower(strings.TrimRight(strings.TrimSpace(baseURL), "/"))
	return strings.HasSuffix(value, "/anthropic") || strings.Contains(value, "/anthropic/")
}

func chatCompletionsURL(baseURL string) string {
	value := strings.TrimRight(strings.TrimSpace(baseURL), "/")
	if strings.HasSuffix(strings.ToLower(value), "/chat/completions") {
		return value
	}
	return value + "/chat/completions"
}

func anthropicMessagesURL(baseURL string) string {
	value := strings.TrimRight(strings.TrimSpace(baseURL), "/")
	lower := strings.ToLower(value)
	if strings.HasSuffix(lower, "/v1/messages") {
		return value
	}
	if strings.HasSuffix(lower, "/v1") {
		return value + "/messages"
	}
	return value + "/v1/messages"
}

func chatOnce(ctx context.Context, cfg AIConfigSection, apiKey string, messages []ChatMessage, temperature *float64) (string, error) {
	client := &http.Client{Timeout: time.Duration(cfg.TimeoutSeconds) * time.Second}
	if isAnthropicEndpoint(cfg.BaseURL) {
		system, converted := splitAnthropicMessages(messages)
		body := map[string]any{
			"model": cfg.Model, "max_tokens": 32768,
			"temperature": clampFloat(temperatureValue(temperature, cfg.Temperature), 0, 1),
			"system":      system, "messages": converted,
		}
		response, err := postJSON(ctx, client, anthropicMessagesURL(cfg.BaseURL), body, map[string]string{
			"x-api-key": apiKey, "anthropic-version": anthropicVersion,
		})
		if err != nil {
			return "", err
		}
		defer response.Body.Close()
		data, _ := io.ReadAll(io.LimitReader(response.Body, 30*1024*1024))
		if response.StatusCode < 200 || response.StatusCode >= 300 {
			return "", mapAIHTTPError(response.StatusCode)
		}
		return parseAnthropicContent(data)
	}
	body := map[string]any{
		"model":       cfg.Model,
		"temperature": clampFloat(temperatureValue(temperature, cfg.Temperature), 0, 2),
		"messages":    messages,
	}
	response, err := postJSON(ctx, client, chatCompletionsURL(cfg.BaseURL), body, map[string]string{
		"Authorization": "Bearer " + apiKey,
	})
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	data, _ := io.ReadAll(io.LimitReader(response.Body, 30*1024*1024))
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return "", mapAIHTTPError(response.StatusCode)
	}
	return parseOpenAIContent(data)
}

func chatStreaming(ctx context.Context, cfg AIConfigSection, apiKey string, messages []ChatMessage,
	temperature *float64, onDelta func(string, bool)) (string, error) {
	if strings.TrimSpace(apiKey) == "" {
		return "", bridgeErr("AI_NOT_CONFIGURED", "宿主尚未配置 AI API Key")
	}
	anthropic := isAnthropicEndpoint(cfg.BaseURL)
	body := map[string]any{}
	url := ""
	headers := map[string]string{"Accept": "text/event-stream"}
	if anthropic {
		system, converted := splitAnthropicMessages(messages)
		url = anthropicMessagesURL(cfg.BaseURL)
		body = map[string]any{
			"model": cfg.Model, "max_tokens": 32768,
			"temperature": clampFloat(temperatureValue(temperature, cfg.Temperature), 0, 1),
			"system":      system, "messages": converted, "stream": true,
		}
		headers["x-api-key"] = apiKey
		headers["anthropic-version"] = anthropicVersion
	} else {
		url = chatCompletionsURL(cfg.BaseURL)
		body = map[string]any{
			"model":       cfg.Model,
			"temperature": clampFloat(temperatureValue(temperature, cfg.Temperature), 0, 2),
			"messages":    messages, "stream": true,
		}
		headers["Authorization"] = "Bearer " + apiKey
	}
	requestBody, _ := json.Marshal(body)
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(requestBody))
	if err != nil {
		return "", bridgeErr("AI_NETWORK_ERROR", "无法创建 AI 请求")
	}
	request.Header.Set("Content-Type", "application/json")
	for key, value := range headers {
		request.Header.Set(key, value)
	}
	timeout := cfg.TimeoutSeconds
	if timeout < 60 {
		timeout = 60
	}
	client := &http.Client{Timeout: time.Duration(timeout+540) * time.Second}
	response, err := client.Do(request)
	if err != nil {
		return "", mapAINetworkError(err)
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return "", mapAIHTTPError(response.StatusCode)
	}
	contentType := strings.ToLower(response.Header.Get("Content-Type"))
	if !strings.Contains(contentType, "text/event-stream") {
		data, _ := io.ReadAll(io.LimitReader(response.Body, 30*1024*1024))
		var content string
		if anthropic {
			content, err = parseAnthropicContent(data)
		} else {
			content, err = parseOpenAIContent(data)
		}
		if err != nil {
			return "", err
		}
		onDelta(content, false)
		return content, nil
	}
	reader := bufio.NewReaderSize(response.Body, 64*1024)
	var output strings.Builder
	for {
		select {
		case <-ctx.Done():
			return "", bridgeErr("AI_CANCELLED", "AI 请求已取消")
		default:
		}
		line, readErr := reader.ReadString('\n')
		if len(line) > 0 {
			line = strings.TrimRight(line, "\r\n")
			if strings.HasPrefix(line, "data:") {
				data := strings.TrimSpace(strings.TrimPrefix(line, "data:"))
				if data == "[DONE]" {
					return finishStream(output.String())
				}
				if anthropic {
					stop, parseErr := appendAnthropicChunk(data, &output, onDelta)
					if parseErr != nil {
						return "", parseErr
					}
					if stop {
						return finishStream(output.String())
					}
				} else {
					appendOpenAIChunk(data, &output, onDelta)
				}
			}
		}
		if readErr != nil {
			if readErr == io.EOF {
				break
			}
			if ctx.Err() != nil {
				return "", bridgeErr("AI_CANCELLED", "AI 请求已取消")
			}
			return "", mapAINetworkError(readErr)
		}
	}
	return finishStream(output.String())
}

func postJSON(ctx context.Context, client *http.Client, url string, body any, headers map[string]string) (*http.Response, error) {
	encoded, err := json.Marshal(body)
	if err != nil {
		return nil, bridgeErr("AI_BAD_REQUEST", "无法编码 AI 请求")
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(encoded))
	if err != nil {
		return nil, bridgeErr("AI_NETWORK_ERROR", "无法创建 AI 请求")
	}
	request.Header.Set("Content-Type", "application/json")
	for key, value := range headers {
		request.Header.Set(key, value)
	}
	response, err := client.Do(request)
	if err != nil {
		return nil, mapAINetworkError(err)
	}
	return response, nil
}

func temperatureValue(value *float64, fallback float64) float64 {
	if value == nil {
		return fallback
	}
	return *value
}

func clampFloat(value, low, high float64) float64 {
	if value < low {
		return low
	}
	if value > high {
		return high
	}
	return value
}

func splitAnthropicMessages(messages []ChatMessage) (string, []ChatMessage) {
	system := []string{}
	converted := []ChatMessage{}
	for _, message := range messages {
		if strings.EqualFold(message.Role, "system") {
			if strings.TrimSpace(message.Content) != "" {
				system = append(system, message.Content)
			}
		} else {
			converted = append(converted, message)
		}
	}
	return strings.Join(system, "\n\n"), converted
}

func parseOpenAIContent(data []byte) (string, error) {
	var value map[string]any
	if json.Unmarshal(data, &value) != nil {
		return "", bridgeErr("AI_INVALID_RESPONSE", "AI 响应不是合法 JSON")
	}
	content := ""
	choices, _ := value["choices"].([]any)
	if len(choices) > 0 {
		choice, _ := choices[0].(map[string]any)
		message, _ := choice["message"].(map[string]any)
		content, _ = message["content"].(string)
	}
	if strings.TrimSpace(content) == "" {
		return "", bridgeErr("AI_INVALID_RESPONSE", "AI 响应缺少 choices[0].message.content")
	}
	return content, nil
}

func parseAnthropicContent(data []byte) (string, error) {
	var value map[string]any
	if json.Unmarshal(data, &value) != nil {
		return "", bridgeErr("AI_INVALID_RESPONSE", "AI 响应不是合法 JSON")
	}
	var builder strings.Builder
	blocks, _ := value["content"].([]any)
	for _, raw := range blocks {
		block, _ := raw.(map[string]any)
		if block["type"] == "text" {
			text, _ := block["text"].(string)
			builder.WriteString(text)
		}
	}
	if strings.TrimSpace(builder.String()) == "" {
		return "", bridgeErr("AI_INVALID_RESPONSE", "AI 响应缺少文本内容")
	}
	return builder.String(), nil
}

func appendOpenAIChunk(data string, output *strings.Builder, onDelta func(string, bool)) {
	var value map[string]any
	if json.Unmarshal([]byte(data), &value) != nil {
		return
	}
	choices, _ := value["choices"].([]any)
	if len(choices) == 0 {
		return
	}
	choice, _ := choices[0].(map[string]any)
	delta, _ := choice["delta"].(map[string]any)
	if reasoning, _ := delta["reasoning_content"].(string); reasoning != "" {
		onDelta(reasoning, true)
	}
	if content, _ := delta["content"].(string); content != "" {
		output.WriteString(content)
		onDelta(content, false)
	}
}

func appendAnthropicChunk(data string, output *strings.Builder, onDelta func(string, bool)) (bool, error) {
	var value map[string]any
	if json.Unmarshal([]byte(data), &value) != nil {
		return false, nil
	}
	eventType, _ := value["type"].(string)
	if eventType == "error" {
		return false, bridgeErr("AI_UPSTREAM_ERROR", "AI 流式响应返回错误")
	}
	if eventType == "message_stop" {
		return true, nil
	}
	if eventType != "content_block_delta" {
		return false, nil
	}
	delta, _ := value["delta"].(map[string]any)
	if thinking, _ := delta["thinking"].(string); thinking != "" {
		onDelta(thinking, true)
	}
	if text, _ := delta["text"].(string); text != "" {
		output.WriteString(text)
		onDelta(text, false)
	}
	return false, nil
}

func finishStream(content string) (string, error) {
	if strings.TrimSpace(content) == "" {
		return "", bridgeErr("AI_INVALID_RESPONSE", "AI 流式响应没有内容")
	}
	return content, nil
}

func mapAIHTTPError(status int) error {
	switch {
	case status == 401 || status == 403:
		return bridgeErr("AI_UNAUTHORIZED", fmt.Sprintf("AI 服务拒绝访问（HTTP %d）：请检查 API Key", status))
	case status == 402:
		return bridgeErr("AI_INSUFFICIENT_BALANCE", fmt.Sprintf("AI 服务账户余额不足（HTTP %d）：请充值后再试", status))
	case status == 404 || status == 405:
		return bridgeErr("AI_INVALID_BASE_URL", fmt.Sprintf("接口地址无法处理该请求（HTTP %d）：OpenAI 兼容填 https://api.deepseek.com，Anthropic 兼容填 https://api.deepseek.com/anthropic", status))
	case status == 422:
		return bridgeErr("AI_BAD_REQUEST", fmt.Sprintf("AI 服务拒绝请求参数（HTTP %d）：请检查模型名称（如 deepseek-flash / deepseek-v4-pro）", status))
	case status == 429:
		return bridgeErr("AI_RATE_LIMITED", "AI 服务限流（HTTP 429）")
	case status == 529 || status >= 500:
		return bridgeErr("AI_UPSTREAM_ERROR", fmt.Sprintf("AI 服务内部错误（HTTP %d）：请稍后重试", status))
	default:
		return bridgeErr("AI_BAD_REQUEST", fmt.Sprintf("AI 服务拒绝请求（HTTP %d）：请检查接口地址与请求参数", status))
	}
}

func mapAINetworkError(err error) error {
	if err == nil {
		return bridgeErr("AI_NETWORK_ERROR", "无法连接 AI 服务")
	}
	if strings.Contains(strings.ToLower(err.Error()), "timeout") {
		return bridgeErr("AI_TIMEOUT", "AI 请求超时")
	}
	return bridgeErr("AI_NETWORK_ERROR", "无法连接或读取 AI 服务，请检查接口地址和网络连接")
}

func (c *bridgeCall) aiChat(ctx context.Context) (any, error) {
	rawMessages, ok := c.payload["messages"].([]any)
	if !ok || len(rawMessages) == 0 {
		return nil, bridgeErr("BRIDGE_BAD_REQUEST", "payload.messages 必填且为数组")
	}
	messages := make([]ChatMessage, 0, len(rawMessages))
	for _, raw := range rawMessages {
		message, _ := raw.(map[string]any)
		role, _ := message["role"].(string)
		content, _ := message["content"].(string)
		if strings.TrimSpace(role) == "" {
			return nil, bridgeErr("BRIDGE_BAD_REQUEST", "messages[].role 必填")
		}
		if _, ok := message["content"].(string); !ok {
			return nil, bridgeErr("BRIDGE_BAD_REQUEST", "messages[].content 必填")
		}
		messages = append(messages, ChatMessage{Role: role, Content: content})
	}
	var temperature *float64
	if value, ok := c.payload["temperature"].(float64); ok {
		temperature = &value
	}
	key, cfg, err := requireAIConfig(c.state)
	if err != nil {
		return nil, err
	}
	content, err := chatOnce(ctx, cfg, key, messages, temperature)
	if err != nil {
		return nil, err
	}
	return map[string]any{"content": content}, nil
}
