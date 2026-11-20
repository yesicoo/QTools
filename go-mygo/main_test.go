package main

import (
	"strings"
	"testing"
)

func TestParseAIOutputFencedHTML(t *testing.T) {
	raw := "```json\n{\"manifest\":{\"id\":\"demo\",\"name\":\"Demo\"},\"indexHtml\":\"<html><style>a{color:red}</style><script>const x = { a: 1 };</script></html>\"}\n```"
	parsed, err := parseAIOutput(raw)
	if err != nil {
		t.Fatalf("解析失败：%v", err)
	}
	if !strings.Contains(parsed.IndexHTML, "const x = { a: 1 }") {
		t.Fatalf("HTML 丢失：%s", parsed.IndexHTML)
	}
}

func TestParseAIOutputRejectsMissingHTML(t *testing.T) {
	_, err := parseAIOutput(`{"manifest":{}}`)
	if errorCode(err) != "AI_OUTPUT_UNPARSEABLE" {
		t.Fatalf("期望 AI_OUTPUT_UNPARSEABLE，得到 %v", err)
	}
}

func TestSearchKeyIncludesPinyin(t *testing.T) {
	key := buildSearchKey("图片压缩", "image-compress", "")
	for _, expected := range []string{"tupian", "tupianyasuo", "tpys"} {
		if !strings.Contains(key, expected) {
			t.Fatalf("搜索键缺少 %s：%s", expected, key)
		}
	}
}

func TestManifestValidationRejectsCSPInjection(t *testing.T) {
	csp := "default-src 'self'\r\nX-Injected: 1"
	manifest := ToolManifest{
		ID: "demo", Name: "Demo", Version: "1.0.0", Entry: "index.html",
		Permissions: []string{}, Window: ToolWindowSettings{Width: 520, Height: 720, Resizable: true},
		CreatedAt: "2026-11-20T00:00:00Z", CSP: &csp,
	}
	errors := validateManifest(&manifest, "demo")
	found := false
	for _, message := range errors {
		if strings.Contains(message, "csp") {
			found = true
		}
	}
	if !found {
		t.Fatalf("应拒绝含换行的 CSP：%v", errors)
	}
}

func TestSecurityScannerFlagsEval(t *testing.T) {
	manifest := ToolManifest{
		ID: "demo", Name: "Demo", Version: "1.0.0", Entry: "index.html",
		Permissions: []string{}, Window: ToolWindowSettings{Width: 520, Height: 720, Resizable: true},
		CreatedAt: "2026-11-20T00:00:00Z",
	}
	findings, _ := scanContent(&manifest, "<script>eval('1')</script>")
	for _, finding := range findings {
		if finding.Code == "DYNAMIC_CODE" && finding.Severity == 1 {
			return
		}
	}
	t.Fatalf("未检测到 eval 高危项：%v", findings)
}

func TestSanitizeShortcutName(t *testing.T) {
	if got := sanitizeShortcutName(`a/b:c*?"<>|`); strings.ContainsAny(got, `/\:*?"<>|`) {
		t.Fatalf("未清理非法字符：%s", got)
	}
	if got := sanitizeShortcutName("CON"); got != "_CON" {
		t.Fatalf("未规避保留名：%s", got)
	}
}

func TestResolveLnkRejectsGarbage(t *testing.T) {
	dir := t.TempDir()
	path := dir + "/bad.lnk"
	if err := writeAtomicFile(path, []byte("not a shortcut")); err != nil {
		t.Fatal(err)
	}
	if got := resolveLnkTarget(path); got != "" {
		t.Fatalf("无效 .lnk 不应解析出目标：%s", got)
	}
}

func TestBuildIDStableCaseInsensitive(t *testing.T) {
	left := buildIDFromTarget(`C:\Program Files\Demo\Demo.exe`)
	right := buildIDFromTarget(`c:\program files\demo\demo.exe`)
	if left != right || !strings.HasPrefix(left, "idx-") || len(left) != 16 {
		t.Fatalf("索引 ID 不稳定：%s %s", left, right)
	}
}

func TestAnthropicEndpointDetection(t *testing.T) {
	if !isAnthropicEndpoint("https://api.deepseek.com/anthropic/") {
		t.Fatal("未识别 Anthropic 兼容端点")
	}
	if isAnthropicEndpoint("https://api.deepseek.com") {
		t.Fatal("普通端点被误判为 Anthropic")
	}
	if got := anthropicMessagesURL("https://api.deepseek.com/anthropic"); got != "https://api.deepseek.com/anthropic/v1/messages" {
		t.Fatalf("Anthropic URL 错误：%s", got)
	}
	if got := chatCompletionsURL("https://api.deepseek.com"); got != "https://api.deepseek.com/chat/completions" {
		t.Fatalf("OpenAI URL 错误：%s", got)
	}
}
