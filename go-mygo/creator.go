package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

const systemPrompt = `你是 QTools 桌面小工具宿主的工具生成器。用户会用自然语言描述需求，你必须只输出一个 JSON 对象，不要有任何解释文字或 Markdown 代码围栏。

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
`

const generateRequirements = `【生成要求（QTools 宿主规范，必须全部满足）】
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
`

type SecurityFinding struct {
	Severity int    `json:"severity"`
	Code     string `json:"code"`
	Message  string `json:"message"`
}

type GeneratedResult struct {
	Manifest                   ToolManifest      `json:"manifest"`
	IndexHTML                  string            `json:"indexHtml"`
	Findings                   []SecurityFinding `json:"findings"`
	HasFatal                   bool              `json:"hasFatal"`
	RequiresExplicitAcceptance bool              `json:"requiresExplicitAcceptance"`
}

type parsedAIOutput struct {
	Manifest  json.RawMessage
	IndexHTML string
}

type securityRule struct {
	severity int
	code     string
	message  string
	pattern  *regexp.Regexp
}

var securityRules = []securityRule{
	{1, "DYNAMIC_CODE", "检测到 eval(——动态执行代码", regexp.MustCompile(`\beval\s*\(`)},
	{1, "DYNAMIC_CODE", "检测到 new Function(——动态执行代码", regexp.MustCompile(`\bnew\s+Function\s*\(`)},
	{1, "DYNAMIC_CODE", "检测到动态 import(——运行时加载模块", regexp.MustCompile(`\bimport\s*\(`)},
	{2, "REMOTE_SCRIPT", "检测到远程 script 标签——引用了外部脚本，请确认来源可信", regexp.MustCompile(`(?i)<script[^>]+src\s*=\s*["']?\s*(?:https?:)?//`)},
	{2, "EXTERNAL_NETWORK", "检测到 fetch 外部地址——该工具会访问网络", regexp.MustCompile(`(?i)\bfetch\s*\(\s*["']\s*(?:https?:)?//`)},
	{2, "EXTERNAL_NETWORK", "检测到 XMLHttpRequest——该工具会访问网络", regexp.MustCompile(`(?i)\bXMLHttpRequest\b`)},
	{2, "EXTERNAL_NETWORK", "检测到 WebSocket(——该工具会建立外部长连接", regexp.MustCompile(`(?i)\bWebSocket\s*\(`)},
	{2, "EXTERNAL_NETWORK", "检测到 navigator.sendBeacon(——该工具会向外部上报数据", regexp.MustCompile(`(?i)\bnavigator\s*\.\s*sendBeacon\s*\(`)},
	{2, "REMOTE_IFRAME", "检测到远程 iframe——内容来自外部站点", regexp.MustCompile(`(?i)<\s*iframe[^>]+src\s*=\s*["']?\s*(?:https?:)?//`)},
	{2, "DOCUMENT_WRITE", "检测到 document.write(——建议改用 DOM API", regexp.MustCompile(`(?i)\bdocument\s*\.\s*write\s*\(`)},
	{2, "SRCDOC", "检测到 srcdoc 属性", regexp.MustCompile(`(?i)\bsrcdoc\s*=`)},
	{1, "HARDCODED_KEY", "检测到疑似 OpenAI 风格 API Key（sk-...）", regexp.MustCompile(`sk-[A-Za-z0-9_\-]{16,}`)},
	{1, "HARDCODED_KEY", "检测到疑似硬编码 apiKey/accessToken", regexp.MustCompile(`(?i)\b(?:api[_-]?key|apikey|access[_-]?token)\b\s*[:=]\s*["'][^"']{12,}["']`)},
	{1, "HARDCODED_KEY", "检测到疑似 AWS Access Key（AKIA...）", regexp.MustCompile(`\bAKIA[0-9A-Z]{16}\b`)},
	{1, "HARDCODED_KEY", "检测到疑似 Bearer 令牌", regexp.MustCompile(`(?i)\bbearer\s+[A-Za-z0-9._\-]{20,}`)},
}

var permissionUseChecks = []struct {
	permission string
	pattern    *regexp.Regexp
	message    string
}{
	{"media", regexp.MustCompile(`\bget(?:User|Display)Media\s*\(`), "页面使用了 getUserMedia/getDisplayMedia，但 manifest 未声明 media 权限"},
	{"files", regexp.MustCompile(`\bwindow\s*\.\s*host\s*\.\s*files\s*\.`), "页面使用了 window.host.files，但 manifest 未声明 files 权限"},
	{"storage", regexp.MustCompile(`\bwindow\s*\.\s*host\s*\.\s*storage\s*\.`), "页面使用了 window.host.storage，但 manifest 未声明 storage 权限"},
	{"ai", regexp.MustCompile(`\bwindow\s*\.\s*host\s*\.\s*ai\s*\.`), "页面使用了 window.host.ai，但 manifest 未声明 ai 权限"},
}

func parseAIOutput(text string) (parsedAIOutput, error) {
	if strings.TrimSpace(text) == "" {
		return parsedAIOutput{}, bridgeErr("AI_OUTPUT_UNPARSEABLE", "AI 返回了空内容")
	}
	unfenced := stripCodeFence(text)
	candidates := []string{}
	if tryParseAIObject(unfenced) {
		candidates = append(candidates, unfenced)
	}
	if balanced := extractBalancedObject(text); balanced != "" && tryParseAIObject(balanced) {
		candidates = append(candidates, balanced)
	}
	if unfenced != text {
		if balanced := extractBalancedObject(unfenced); balanced != "" && tryParseAIObject(balanced) {
			candidates = append(candidates, balanced)
		}
	}
	if len(candidates) == 0 {
		return parsedAIOutput{}, bridgeErr("AI_OUTPUT_UNPARSEABLE", "AI 输出中找不到 JSON 对象")
	}
	var value struct {
		Manifest  json.RawMessage `json:"manifest"`
		IndexHTML string          `json:"indexHtml"`
	}
	if err := json.Unmarshal([]byte(candidates[0]), &value); err != nil {
		return parsedAIOutput{}, bridgeErr("AI_OUTPUT_UNPARSEABLE", fmt.Sprintf("AI 输出的 JSON 无法解析：%v", err))
	}
	if len(value.Manifest) == 0 || strings.TrimSpace(value.IndexHTML) == "" {
		return parsedAIOutput{}, bridgeErr("AI_OUTPUT_UNPARSEABLE", "AI 输出缺少 manifest 或 indexHtml 字段")
	}
	return parsedAIOutput{Manifest: value.Manifest, IndexHTML: value.IndexHTML}, nil
}

func tryParseAIObject(text string) bool {
	var value struct {
		Manifest  json.RawMessage `json:"manifest"`
		IndexHTML string          `json:"indexHtml"`
	}
	if json.Unmarshal([]byte(text), &value) != nil {
		return false
	}
	return len(value.Manifest) > 0 && strings.TrimSpace(value.IndexHTML) != ""
}

func stripCodeFence(text string) string {
	trimmed := strings.TrimSpace(text)
	if !strings.HasPrefix(trimmed, "```") {
		return text
	}
	firstLine := strings.IndexByte(trimmed, '\n')
	lastFence := strings.LastIndex(trimmed, "```")
	if firstLine < 0 || lastFence <= firstLine {
		return text
	}
	return strings.TrimSpace(trimmed[firstLine+1 : lastFence])
}

func extractBalancedObject(text string) string {
	start := strings.IndexByte(text, '{')
	if start < 0 {
		return ""
	}
	depth := 0
	inString := false
	escaped := false
	for index := start; index < len(text); index++ {
		ch := text[index]
		if inString {
			if escaped {
				escaped = false
			} else if ch == '\\' {
				escaped = true
			} else if ch == '"' {
				inString = false
			}
			continue
		}
		switch ch {
		case '"':
			inString = true
		case '{':
			depth++
		case '}':
			depth--
			if depth == 0 {
				return text[start : index+1]
			}
		}
	}
	return ""
}

func manifestFromJSON(data []byte) (ToolManifest, error) {
	var manifest ToolManifest
	if err := json.Unmarshal(data, &manifest); err != nil {
		return manifest, bridgeErr("MANIFEST_INVALID", fmt.Sprintf("生成的 manifest 不是合法 JSON：%v", err))
	}
	normalizeManifest(&manifest)
	return manifest, nil
}

func scanContent(manifest *ToolManifest, html string) ([]SecurityFinding, bool) {
	findings := []SecurityFinding{}
	for _, message := range validateManifest(manifest, manifest.ID) {
		findings = append(findings, SecurityFinding{Severity: 0, Code: "MANIFEST_INVALID", Message: message})
	}
	for _, rule := range securityRules {
		if rule.pattern.MatchString(html) {
			findings = append(findings, SecurityFinding{Severity: rule.severity, Code: rule.code, Message: rule.message})
		}
	}
	for _, permission := range manifest.Permissions {
		switch permission {
		case "ai":
			findings = append(findings, SecurityFinding{2, "PERMISSION_AI", "manifest 声明了 ai 权限：该工具可调用宿主 AI 接口（消耗你的配额）"})
		case "media":
			findings = append(findings, SecurityFinding{2, "PERMISSION_MEDIA", "manifest 声明了 media 权限：该工具可使用麦克风 / 摄像头 / 屏幕捕获"})
		case "files":
			findings = append(findings, SecurityFinding{2, "PERMISSION_FILES", "manifest 声明了 files 权限：该工具可在自己的目录内读写二进制文件"})
		}
	}
	if manifest.AllowEval {
		findings = append(findings, SecurityFinding{1, "ALLOW_EVAL", "manifest 启用了 allowEval：CSP 将放开 'unsafe-eval'，允许 eval/new Function"})
	}
	if manifest.CSP != nil && strings.TrimSpace(*manifest.CSP) != "" {
		findings = append(findings, SecurityFinding{1, "CSP_OVERRIDE", "manifest 自定义了 CSP：可能削弱 script-src / connect-src 限制"})
	}
	declared := map[string]bool{}
	for _, permission := range manifest.Permissions {
		declared[strings.ToLower(permission)] = true
	}
	for _, check := range permissionUseChecks {
		if check.pattern.MatchString(html) && !declared[check.permission] {
			findings = append(findings, SecurityFinding{0, "PERMISSION_MISSING", check.message})
		}
	}
	sort.SliceStable(findings, func(i, j int) bool {
		if findings[i].Severity != findings[j].Severity {
			return findings[i].Severity < findings[j].Severity
		}
		return findings[i].Code < findings[j].Code
	})
	hasFatal := false
	for _, finding := range findings {
		if finding.Severity == 0 {
			hasFatal = true
		}
	}
	return findings, hasFatal
}

func buildGenerated(raw string, identity *LoadedTool, version string) (GeneratedResult, error) {
	parsed, err := parseAIOutput(raw)
	if err != nil {
		return GeneratedResult{}, err
	}
	manifest, err := manifestFromJSON(parsed.Manifest)
	if err != nil {
		return GeneratedResult{}, err
	}
	if identity != nil {
		manifest.ID = identity.Manifest.ID
		manifest.Entry = "index.html"
		manifest.Name = identity.Name()
		manifest.Version = version
	}
	findings, hasFatal := scanContent(&manifest, parsed.IndexHTML)
	requires := false
	for _, finding := range findings {
		if finding.Severity == 1 {
			requires = true
		}
	}
	return GeneratedResult{
		Manifest: manifest, IndexHTML: parsed.IndexHTML, Findings: findings,
		HasFatal: hasFatal, RequiresExplicitAcceptance: requires,
	}, nil
}

func ensureSaveAllowed(findings []SecurityFinding, accepted bool) error {
	for _, finding := range findings {
		if finding.Severity == 0 {
			return bridgeErr("MANIFEST_INVALID", fmt.Sprintf("生成内容存在致命问题，已拒绝保存：%s", finding.Message))
		}
	}
	if !accepted {
		for _, finding := range findings {
			if finding.Severity == 1 {
				return bridgeErr("CONTENT_SECURITY_VIOLATION",
					fmt.Sprintf("生成内容包含高危项（%s: %s），需显式确认后才能保存", finding.Code, finding.Message))
			}
		}
	}
	return nil
}

func requireAIConfig(state *AppState) (string, AIConfigSection, error) {
	key, ok := state.Secrets.Get()
	cfg := state.Config.Snapshot()
	if !ok || strings.TrimSpace(key) == "" || strings.TrimSpace(cfg.AI.BaseURL) == "" || strings.TrimSpace(cfg.AI.Model) == "" {
		return "", cfg.AI, bridgeErr("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口（baseUrl / model / API Key）")
	}
	return key, cfg.AI, nil
}

func (c *bridgeCall) generateTool(ctx context.Context) (any, error) {
	prompt, err := readString(c.payload, "prompt")
	if err != nil {
		return nil, err
	}
	if len([]rune(prompt)) > 4000 {
		return nil, bridgeErr("AI_BAD_REQUEST", "需求描述不超过 4000 字符")
	}
	key, aiConfig, err := requireAIConfig(c.state)
	if err != nil {
		return nil, err
	}
	messages := []ChatMessage{
		{Role: "system", Content: systemPrompt},
		{Role: "user", Content: generateRequirements + strings.TrimSpace(prompt)},
	}
	raw, err := chatStreaming(ctx, aiConfig, key, messages, nil, c.stream)
	if err != nil {
		return nil, err
	}
	result, err := buildGenerated(raw, nil, "")
	if err != nil {
		return nil, err
	}
	return result, nil
}

func (c *bridgeCall) generateUpdate(ctx context.Context) (any, error) {
	toolID, err := readString(c.payload, "toolId")
	if err != nil {
		return nil, err
	}
	instruction, err := readString(c.payload, "instruction")
	if err != nil {
		return nil, err
	}
	if len([]rune(instruction)) > 4000 {
		return nil, bridgeErr("AI_BAD_REQUEST", "修改诉求不超过 4000 字符")
	}
	tool := c.state.Tools.Find(toolID)
	if tool == nil {
		return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
	}
	currentHTML, err := os.ReadFile(filepath.Join(tool.Directory, tool.Entry()))
	if err != nil {
		return nil, bridgeErr("ENTRY_FILE_MISSING", fmt.Sprintf("读取当前版本失败：%v", err))
	}
	if len(currentHTML) > 512*1024 {
		return nil, bridgeErr("AI_BAD_REQUEST", "当前版本源码过大（>512KB），无法交给 AI 更新")
	}
	bump := readOptionalString(c.payload, "versionBump")
	if bump == "" {
		bump = "patch"
	}
	next := nextVersion(tool.Versions, tool.Manifest.Version, bump)
	manifestJSON, _ := json.Marshal(tool.Manifest)
	userMessage := fmt.Sprintf(
		"下面是一个已存在工具的当前版本，请按我的要求改造成新版本。\n\n【工具标识（必须原样保留，不得更改）】\nid = %s\n当前版本 = %s\n新版本号 = %s（manifest.version 必须填这个）\n\n【当前 manifest.json】\n%s\n\n【当前 index.html】\n%s\n\n【我的修改要求】\n%s\n\n请输出完整的 JSON 对象（格式同系统提示）：manifest 用上面这份、只把 version 改成「新版本号」，indexHtml 输出改造后的完整单文件 HTML。其余字段（id/name/entry/window/createdAt/allowEval/csp）保持不变。permissions 按改造后代码的实际用法重新确定，用不到的要去掉，新用到的要补上。",
		tool.Manifest.ID, tool.Manifest.Version, next, manifestJSON, currentHTML, strings.TrimSpace(instruction),
	)
	key, aiConfig, err := requireAIConfig(c.state)
	if err != nil {
		return nil, err
	}
	raw, err := chatStreaming(ctx, aiConfig, key, []ChatMessage{
		{Role: "system", Content: systemPrompt},
		{Role: "user", Content: userMessage},
	}, nil, c.stream)
	if err != nil {
		return nil, err
	}
	result, err := buildGenerated(raw, tool, next)
	if err != nil {
		return nil, err
	}
	return result, nil
}

func (c *bridgeCall) fixTool(ctx context.Context) (any, error) {
	toolID, err := readString(c.payload, "toolId")
	if err != nil {
		return nil, err
	}
	description, err := readString(c.payload, "error")
	if err != nil {
		return nil, err
	}
	tool := c.state.Tools.Find(toolID)
	if tool == nil {
		return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
	}
	currentHTML, _ := os.ReadFile(filepath.Join(tool.Directory, tool.Entry()))
	manifestJSON, _ := json.Marshal(tool.Manifest)
	key, aiConfig, err := requireAIConfig(c.state)
	if err != nil {
		return nil, err
	}
	raw, err := chatStreaming(ctx, aiConfig, key, []ChatMessage{
		{Role: "system", Content: systemPrompt},
		{Role: "user", Content: fmt.Sprintf(
			"请修复以下 QTools 工具。用户报告的问题：\n---\n%s\n---\n\n当前 manifest.json：\n---\n%s\n---\n\n当前 %s：\n---\n%s\n---\n\n请输出修复后的完整 JSON（manifest + indexHtml），规则与之前一致。",
			strings.TrimSpace(description), manifestJSON, tool.Entry(), currentHTML)},
	}, nil, c.stream)
	if err != nil {
		return nil, err
	}
	return buildGenerated(raw, tool, tool.Manifest.Version)
}

func (c *bridgeCall) readGenerated(payload map[string]any, key string) (GeneratedResult, error) {
	raw, ok := payload[key].(map[string]any)
	if !ok {
		return GeneratedResult{}, bridgeErr("BRIDGE_BAD_REQUEST", fmt.Sprintf("payload.%s 必填且为对象", key))
	}
	manifestValue, ok := raw["manifest"].(map[string]any)
	if !ok {
		return GeneratedResult{}, bridgeErr("BRIDGE_BAD_REQUEST", "generated.manifest 必填")
	}
	encoded, _ := json.Marshal(manifestValue)
	manifest, err := manifestFromJSON(encoded)
	if err != nil {
		return GeneratedResult{}, err
	}
	html, _ := raw["indexHtml"].(string)
	if strings.TrimSpace(html) == "" {
		return GeneratedResult{}, bridgeErr("BRIDGE_BAD_REQUEST", "generated.indexHtml 必填")
	}
	findings, hasFatal := scanContent(&manifest, html)
	requires := false
	for _, finding := range findings {
		if finding.Severity == 1 {
			requires = true
		}
	}
	return GeneratedResult{Manifest: manifest, IndexHTML: html, Findings: findings, HasFatal: hasFatal, RequiresExplicitAcceptance: requires}, nil
}

func (c *bridgeCall) createTool() (any, error) {
	generated, err := c.readGenerated(c.payload, "generated")
	if err != nil {
		return nil, err
	}
	accept := readOptionalBool(c.payload, "acceptHighSeverity")
	toolID, err := saveGeneratedTool(c.state, generated, accept)
	if err != nil {
		return nil, err
	}
	return map[string]any{"toolId": toolID, "opened": false}, nil
}

func saveGeneratedTool(state *AppState, generated GeneratedResult, accept bool) (string, error) {
	if err := ensureSaveAllowed(generated.Findings, accept); err != nil {
		return "", err
	}
	if state.Tools.Find(generated.Manifest.ID) != nil {
		return "", bridgeErr("TOOL_ALREADY_EXISTS", fmt.Sprintf("工具 ID 已存在：%s", generated.Manifest.ID))
	}
	directory := state.Paths.ToolDir(generated.Manifest.ID)
	if fileExists(filepath.Join(directory, "manifest.json")) {
		return "", bridgeErr("TOOL_ALREADY_EXISTS", fmt.Sprintf("工具目录已存在：%s", generated.Manifest.ID))
	}
	manifest := generated.Manifest
	manifest.CreatedAt = time.Now().Format(time.RFC3339)
	if err := writeManifest(directory, manifest); err != nil {
		return "", err
	}
	entry := manifest.Entry
	if strings.TrimSpace(entry) == "" {
		entry = "index.html"
	}
	if err := writeAtomicFile(filepath.Join(directory, entry), []byte(generated.IndexHTML)); err != nil {
		return "", err
	}
	state.Tools.Rescan()
	if state.Tools.Find(manifest.ID) == nil {
		return "", bridgeErr("MANIFEST_INVALID", "工具已写入磁盘但重新扫描失败，请手动检查")
	}
	state.Usage.EnsureSeeded([]string{manifest.ID})
	pushHostEvent(state, "launcher-changed")
	return manifest.ID, nil
}

func (c *bridgeCall) applyUpdate() (any, error) {
	toolID, err := readString(c.payload, "toolId")
	if err != nil {
		return nil, err
	}
	generated, err := c.readGenerated(c.payload, "generated")
	if err != nil {
		return nil, err
	}
	accept := readOptionalBool(c.payload, "acceptHighSeverity")
	if err := ensureSaveAllowed(generated.Findings, accept); err != nil {
		return nil, err
	}
	tool := c.state.Tools.Find(toolID)
	if tool == nil {
		return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在")
	}
	version := generated.Manifest.Version
	target := filepath.Join(tool.Directory, ".versions", version)
	if fileExists(target) {
		return nil, bridgeErr("VERSION_EXISTS", fmt.Sprintf("版本 %s 已存在，请改换递增粒度", version))
	}
	if err := snapshotVersion(tool.Directory, tool.Manifest.Version, "更新前自动备份"); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(target, 0o755); err != nil {
		return nil, bridgeErr("VERSION_SNAPSHOT_FAILED", err.Error())
	}
	if err := writeAtomicFile(filepath.Join(target, "index.html"), []byte(generated.IndexHTML)); err != nil {
		return nil, err
	}
	manifest := generated.Manifest
	manifest.CreatedAt = tool.Manifest.CreatedAt
	if err := writeManifest(target, manifest); err != nil {
		return nil, err
	}
	if err := activateVersion(tool.Directory, tool.Manifest.Version, version); err != nil {
		return nil, err
	}
	c.state.Tools.Rescan()
	if updated := c.state.Tools.Find(toolID); updated == nil || !updated.IsValid {
		return nil, bridgeErr("MANIFEST_INVALID", "更新后的版本未通过校验")
	}
	pushHostEvent(c.state, "launcher-changed")
	return map[string]any{"ok": true}, nil
}

func (c *bridgeCall) importTool() (any, error) {
	name, err := readString(c.payload, "name")
	if err != nil {
		return nil, err
	}
	html, err := readString(c.payload, "html")
	if err != nil {
		return nil, err
	}
	accept := readOptionalBool(c.payload, "acceptHighSeverity")
	result, err := importHTML(c.state, name, html,
		readOptionalString(c.payload, "description"), readOptionalString(c.payload, "category"),
		readOptionalString(c.payload, "glyph"), accept)
	if err != nil {
		return nil, err
	}
	if result["saved"] == true {
		c.state.Usage.EnsureSeeded([]string{result["toolId"].(string)})
		pushHostEvent(c.state, "launcher-changed")
	}
	return result, nil
}

func importHTML(state *AppState, name, html, description, category, glyph string, accept bool) (map[string]any, error) {
	name = strings.TrimSpace(name)
	if name == "" {
		return nil, bridgeErr("IMPORT_NAME_REQUIRED", "请填写工具名称")
	}
	if len([]rune(name)) > 60 {
		return nil, bridgeErr("IMPORT_NAME_TOO_LONG", "工具名称不超过 60 字符")
	}
	if strings.TrimSpace(html) == "" {
		return nil, bridgeErr("IMPORT_HTML_REQUIRED", "HTML 内容为空")
	}
	if len(html) > 512*1024 {
		return nil, bridgeErr("IMPORT_HTML_TOO_LARGE", "HTML 文件过大，上限 512 KB")
	}
	id := generateSlug(name, "imported-", state.Tools.Contains)
	manifest := ToolManifest{
		ID: id, Name: name, Version: "1.0.0", Description: optionalText(description),
		Entry: "index.html", Permissions: detectPermissions(html),
		Window:    ToolWindowSettings{Width: 960, Height: 720, Resizable: true},
		CreatedAt: time.Now().Format(time.RFC3339), Category: optionalText(category), Glyph: optionalText(glyph),
	}
	findings, hasFatal := scanContent(&manifest, html)
	external := detectExternalAssets(html)
	if hasFatal {
		reasons := []string{}
		for _, finding := range findings {
			if finding.Severity == 0 {
				reasons = append(reasons, finding.Message)
			}
		}
		return nil, bridgeErr("IMPORT_REJECTED", "内容未通过安全审查："+strings.Join(reasons, "；"))
	}
	requires := false
	for _, finding := range findings {
		if finding.Severity == 1 {
			requires = true
		}
	}
	if requires && !accept {
		return map[string]any{
			"toolId": id, "toolName": name, "url": "", "saved": false,
			"permissions": manifest.Permissions, "findings": findings, "hasFatal": false,
			"requiresExplicitAcceptance": true, "externalAssets": external,
		}, nil
	}
	dir := state.Paths.ToolDir(id)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	if err := writeAtomicFile(filepath.Join(dir, "index.html"), []byte(html)); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	if err := writeManifest(dir, manifest); err != nil {
		return nil, bridgeErr("IMPORT_REJECTED", err.Error())
	}
	state.Tools.Rescan()
	loaded := state.Tools.Find(id)
	if loaded == nil || !loaded.IsValid {
		return nil, bridgeErr("IMPORT_REJECTED", "工具未通过运行时校验")
	}
	return map[string]any{
		"toolId": id, "toolName": name, "url": "/tools/" + id + "/index.html", "saved": true,
		"permissions": manifest.Permissions, "findings": findings, "hasFatal": false,
		"requiresExplicitAcceptance": false, "externalAssets": external,
	}, nil
}

func detectExternalAssets(html string) []string {
	pattern := regexp.MustCompile(`(?i)(?:src|href)\s*=\s*["']([^"']+)["']`)
	result := []string{}
	seen := map[string]bool{}
	for _, match := range pattern.FindAllStringSubmatch(html, -1) {
		value := match[1]
		lower := strings.ToLower(value)
		if value == "" || strings.HasPrefix(value, "#") || strings.HasPrefix(lower, "data:") ||
			strings.HasPrefix(lower, "http://") || strings.HasPrefix(lower, "https://") || strings.HasPrefix(value, "//") {
			continue
		}
		if !seen[value] {
			seen[value] = true
			result = append(result, value)
		}
	}
	return result
}

func writeManifest(directory string, manifest ToolManifest) error {
	if err := os.MkdirAll(directory, 0o755); err != nil {
		return bridgeErr("TOOL_WRITE_FAILED", err.Error())
	}
	return writeJSON(filepath.Join(directory, "manifest.json"), manifest)
}

func writeAtomicFile(path string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return bridgeErr("TOOL_WRITE_FAILED", err.Error())
	}
	temp := path + ".tmp"
	if err := os.WriteFile(temp, data, 0o644); err != nil {
		return bridgeErr("TOOL_WRITE_FAILED", err.Error())
	}
	if err := os.Rename(temp, path); err != nil {
		return bridgeErr("TOOL_WRITE_FAILED", err.Error())
	}
	return nil
}
