using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Bridge;
using QTools.Host.Creator;
using Xunit;

namespace QTools.Tests;

/// <summary>AI 输出容错解析（围栏、噪音、缺字段）。</summary>
public sealed class AiOutputParserTests
{
    private const string Manifest = """
        {"id":"gen-tool","name":"Gen","version":"1.0.0","description":"d","entry":"index.html",
         "permissions":["storage"],"window":{"width":520,"height":680,"resizable":true},
         "createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}
        """;

    private static string Wrap(string json) =>
        $"{{\"manifest\":{Manifest},\"indexHtml\":\"<html><body>ok</body></html>\"}}";

    [Fact]
    public void ParsesPlainJson()
    {
        var result = AiOutputParser.Parse(Wrap("{}"));
        Assert.Equal("gen-tool", result.Manifest.GetProperty("id").GetString());
        Assert.Contains("ok", result.IndexHtml);
    }

    [Fact]
    public void ParsesJsonWithMarkdownFenceAndPreamble()
    {
        var text = "Sure, here is the tool:\n```json\n" + Wrap("{}") + "\n```\nHope this helps!";
        var result = AiOutputParser.Parse(text);
        Assert.Equal("gen-tool", result.Manifest.GetProperty("id").GetString());
    }

    [Fact]
    public void RejectsEmptyText()
    {
        var ex = Assert.Throws<BridgeException>(() => AiOutputParser.Parse("   "));
        Assert.Equal("AI_OUTPUT_UNPARSEABLE", ex.Code);
    }

    [Fact]
    public void RejectsTextWithoutJson()
    {
        var ex = Assert.Throws<BridgeException>(() => AiOutputParser.Parse("no json here"));
        Assert.Equal("AI_OUTPUT_UNPARSEABLE", ex.Code);
    }

    [Fact]
    public void RejectsMissingManifest()
    {
        var ex = Assert.Throws<BridgeException>(() =>
            AiOutputParser.Parse("{\"indexHtml\":\"<html></html>\"}"));
        Assert.Equal("AI_OUTPUT_UNPARSEABLE", ex.Code);
    }

    [Fact]
    public void RejectsMissingIndexHtml()
    {
        var ex = Assert.Throws<BridgeException>(() =>
            AiOutputParser.Parse($"{{\"manifest\":{Manifest}}}"));
        Assert.Equal("AI_OUTPUT_UNPARSEABLE", ex.Code);
    }

    [Fact]
    public void ExtractsFirstBalancedObjectOnly()
    {
        var text = "noise {\"a\":1} then " + Wrap("{}");
        var extracted = AiOutputParser.ExtractBalancedObject(text);
        Assert.Equal("{\"a\":1}", extracted);
    }

    [Fact]
    public void HandlesBracesInsideStrings()
    {
        var text = "{\"html\":\"<div>{not a brace}</div>\"}";
        Assert.Equal(text, AiOutputParser.ExtractBalancedObject(text));
    }

    [Fact]
    public void ReturnsNullForUnterminatedObject()
    {
        Assert.Null(AiOutputParser.ExtractBalancedObject("{\"a\":1"));
    }

    [Fact]
    public void StripsCodeFence()
    {
        var inner = "{\"a\":1}";
        var text = "```json\n" + inner + "\n```";
        Assert.Equal(inner, AiOutputParser.StripCodeFence(text));
    }

    [Fact]
    public void StripCodeFenceLeavesPlainTextAlone()
    {
        const string text = "{\"a\":1}";
        Assert.Equal(text, AiOutputParser.StripCodeFence(text));
    }

    /// <summary>
    /// 回归测试：indexHtml 内含大量引号/反斜杠时，括号配对的奇偶性可能错位，
    /// 但「剥围栏后整体解析」必须仍然成功——这正是录音工具曾失败的场景。
    /// </summary>
    [Fact]
    public void ParsesPayloadWithQuoteHeavyHtml()
    {
        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>R</title></head><body>"
                 + "<script>(function(){var h=window.host;h.files.save({name:\"a.webm\","
                 + "dataBase64:\"VGhl\"}).then(function(){var s=\"a\\\"b\";});})();</script>"
                 + "</body></html>";
        var payload = "{\"manifest\":" + Manifest + ",\"indexHtml\":" + JsonSerializer.Serialize(html) + "}";
        var wrapped = "```json\n" + payload + "\n```";

        var result = AiOutputParser.Parse(wrapped);
        Assert.Contains("files.save", result.IndexHtml);
        Assert.Equal("gen-tool", result.Manifest.GetProperty("id").GetString());
    }

    /// <summary>
    /// PowerShell 风格转义（\u003c 代替 &lt;）会让括号配对的引号奇偶性错位，
    /// 此时「剥围栏后整体解析」是唯一可靠路径——用真实录音工具的载荷形态验证。
    /// </summary>
    [Fact]
    public void ParsesPowerShellEscapedPayload()
    {
        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Recorder</title></head><body>"
                 + "<script>(function(){var h=window.host;h.files.save({name:\"a.webm\","
                 + "dataBase64:\"VGhl\"});})();</script></body></html>";

        // 先正常 JSON 序列化，再把 < > & 替换为 \uXXXX（模拟 PowerShell ConvertTo-Json）
        var payload = "{\"manifest\":" + Manifest + ",\"indexHtml\":" + JsonSerializer.Serialize(html) + "}";
        payload = payload.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026");
        var wrapped = "```json\n" + payload + "\n```";

        var result = AiOutputParser.Parse(wrapped);
        Assert.Contains("files.save", result.IndexHtml);
        Assert.Equal("gen-tool", result.Manifest.GetProperty("id").GetString());
    }

    [Fact]
    public void ParsesPlainJsonWithoutFence()
    {
        var payload = "{\"manifest\":" + Manifest + ",\"indexHtml\":\"<html><body>ok</body></html>\"}";
        var result = AiOutputParser.Parse(payload);
        Assert.Equal("gen-tool", result.Manifest.GetProperty("id").GetString());
    }

    [Fact]
    public void ParsesJsonWithLeadingProse()
    {
        var payload = "{\"manifest\":" + Manifest + ",\"indexHtml\":\"<html>ok</html>\"}";
        var result = AiOutputParser.Parse("Sure, here you go:\n" + payload + "\nHope this helps!");
        Assert.Equal("gen-tool", result.Manifest.GetProperty("id").GetString());
    }
}

/// <summary>生成内容安全审查（对应验收标准 #7 的宿主校验环节）。</summary>
public sealed class ContentSecurityScannerTests
{
    private static string Html(params string[] snippets) =>
        "<!DOCTYPE html><html><body>" + string.Join("", snippets) + "</body></html>";

    [Theory]
    [InlineData("eval('x')")]
    [InlineData("eval ( 'x' )")]
    [InlineData("new Function('a','return a')")]
    [InlineData("import('./mod.js')")]
    public void DetectsDynamicCode(string snippet)
    {
        var findings = ContentSecurityScanner.ScanHtml(Html(snippet));
        Assert.Contains(findings, f => f.Code == "DYNAMIC_CODE" && f.Severity == SecuritySeverity.High);
    }

    [Theory]
    [InlineData("<script src=\"https://cdn.example.com/a.js\"></script>")]
    [InlineData("<script src='//cdn.example.com/a.js'></script>")]
    public void DetectsRemoteScript(string snippet)
    {
        var findings = ContentSecurityScanner.ScanHtml(Html(snippet));
        // 远程脚本已放开为提示级（允许 CDN/外部资源），但必须向用户明示
        var finding = Assert.Single(findings, f => f.Code == "REMOTE_SCRIPT");
        Assert.Equal(SecuritySeverity.Warning, finding.Severity);
    }

    [Theory]
    [InlineData("fetch('https://api.openai.com/v1/x')")]
    [InlineData("new XMLHttpRequest()")]
    [InlineData("new WebSocket('wss://x')")]
    [InlineData("navigator.sendBeacon('https://x')")]
    public void DetectsExternalNetwork(string snippet)
    {
        var findings = ContentSecurityScanner.ScanHtml(Html(snippet));
        // 联网已放开为提示级（不阻止保存），但必须向用户明示
        Assert.Contains(findings, f =>
            f.Code == "EXTERNAL_NETWORK" && f.Severity == SecuritySeverity.Warning);
    }

    [Theory]
    [InlineData("var k = 'sk-abcdefghijklmnop1234';")]
    [InlineData("apiKey: \"abcdef1234567890abcdef\"")]
    [InlineData("var a = 'AKIAIOSFODNN7EXAMPLE';")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz")]
    public void DetectsHardcodedSecrets(string snippet)
    {
        var findings = ContentSecurityScanner.ScanHtml(Html(snippet));
        Assert.Contains(findings, f => f.Code == "HARDCODED_KEY" && f.Severity == SecuritySeverity.High);
    }

    [Fact]
    public void CleanHtmlHasNoFindings()
    {
        var findings = ContentSecurityScanner.ScanHtml(
            Html("<h1>Hi</h1>", "<script>document.querySelector('h1').textContent='Yo';</script>"));

        Assert.Empty(findings);
    }

    [Fact]
    public void SameOriginFetchIsAllowed()
    {
        // connect-src 'self' 允许同源请求：不应判为外联
        var findings = ContentSecurityScanner.ScanHtml(Html("<script>fetch('/api/x')</script>"));
        Assert.DoesNotContain(findings, f => f.Code is "EXTERNAL_AI" or "EXTERNAL_NETWORK");
    }

    [Fact]
    public void DocumentWriteIsWarningNotHigh()
    {
        var findings = ContentSecurityScanner.ScanHtml(Html("<script>document.write('x')</script>"));
        var finding = Assert.Single(findings, f => f.Code == "DOCUMENT_WRITE");
        Assert.Equal(SecuritySeverity.Warning, finding.Severity);
    }

    [Fact]
    public void ScanReturnsFatalForInvalidManifest()
    {
        var manifest = new QTools.Host.Tools.ToolManifest(
            "Bad_Id", "", "x", null, "../escape", Array.Empty<string>(),
            new QTools.Host.Tools.ToolWindowSettings(10, 10, true), "nope", false, null);

        var (findings, hasFatal) = ContentSecurityScanner.Scan(manifest, Html("ok"));

        Assert.True(hasFatal);
        Assert.Contains(findings, f => f.Severity == SecuritySeverity.Fatal && f.Code == "MANIFEST_INVALID");
    }

    [Fact]
    public void ScanWarnsAboutAiPermission()
    {
        var manifest = ValidManifest(permissions: new[] { "ai" });
        var (findings, hasFatal) = ContentSecurityScanner.Scan(manifest, Html("ok"));

        Assert.False(hasFatal);
        Assert.Contains(findings, f => f.Code == "PERMISSION_AI" && f.Severity == SecuritySeverity.Warning);
    }

    [Fact]
    public void ScanFlagsAllowEvalAsHigh()
    {
        var manifest = ValidManifest() with { AllowEval = true };
        var (findings, _) = ContentSecurityScanner.Scan(manifest, Html("ok"));

        Assert.Contains(findings, f => f.Code == "ALLOW_EVAL" && f.Severity == SecuritySeverity.High);
    }

    [Fact]
    public void ScanFlagsCspOverrideAsHigh()
    {
        var manifest = ValidManifest() with { Csp = "default-src *" };
        var (findings, _) = ContentSecurityScanner.Scan(manifest, Html("ok"));

        Assert.Contains(findings, f => f.Code == "CSP_OVERRIDE" && f.Severity == SecuritySeverity.High);
    }

    [Fact]
    public void ValidManifestAndCleanHtmlProduceNoFindings()
    {
        var (findings, hasFatal) = ContentSecurityScanner.Scan(ValidManifest(), Html("<h1>ok</h1>"));
        Assert.Empty(findings);
        Assert.False(hasFatal);
    }

    private static QTools.Host.Tools.ToolManifest ValidManifest(IReadOnlyList<string>? permissions = null) => new(
        "gen-tool",
        "Gen Tool",
        "1.0.0",
        "desc",
        "index.html",
        permissions ?? new[] { "storage" },
        new QTools.Host.Tools.ToolWindowSettings(520, 680, true),
        "2026-11-04T00:00:00+08:00",
        false,
        null);
}
