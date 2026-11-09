using QTools.Host.Bridge;
using QTools.Host.Creator;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>本地 HTML 导入（导入功能：选文件 + 起名字 → 变成工具）。</summary>
public sealed class ToolImporterTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolsDir;
    private readonly ToolRegistry _registry;
    private readonly ToolImporter _importer;

    public ToolImporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-import-" + Guid.NewGuid().ToString("n")[..8]);
        _toolsDir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(_toolsDir);
        _registry = new ToolRegistry(_toolsDir);
        _registry.Rescan();
        _importer = new ToolImporter(_registry, _toolsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private const string PlainHtml = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>"
        + "<body><h1>Hello</h1><p>a plain local tool</p></body></html>";

    [Fact]
    public async Task ImportsPlainHtmlAndWritesManifest()
    {
        var result = await _importer.ImportAsync("我的计算器", PlainHtml, null, acceptHighSeverity: false, default);

        Assert.True(result.Saved);
        Assert.Equal("imported-", result.ToolId[..9]);   // 中文名退化为哈希后缀
        Assert.Empty(result.Permissions);                 // 未用到任何宿主能力
        Assert.Empty(result.Findings);

        var manifestPath = Path.Combine(_toolsDir, result.ToolId, "manifest.json");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(Path.Combine(_toolsDir, result.ToolId, "index.html")));

        // 落盘的 manifest 必须通过运行时校验
        var loaded = _registry.Reload(result.ToolId);
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsValid);
        Assert.Equal("我的计算器", loaded.Manifest.Name);
    }

    [Fact]
    public async Task GeneratesAsciiSlugFromAsciiName()
    {
        var result = await _importer.ImportAsync("My Cool Tool!", PlainHtml, null, false, default);
        Assert.Equal("my-cool-tool", result.ToolId);
    }

    [Fact]
    public async Task SameNonAsciiNameIsStable()
    {
        var first = await _importer.ImportAsync("录音机", PlainHtml, null, false, default);
        var second = await _importer.ImportAsync("录音机", PlainHtml, null, false, default);

        Assert.Equal(first.ToolId, second.ToolId[..first.ToolId.Length]);
        Assert.NotEqual(first.ToolId, second.ToolId);   // 第二个自动加 -2 后缀保证唯一
    }

    [Fact]
    public async Task DetectsPermissionsFromUsage()
    {
        var html = "<html><body><script>"
            + "window.host.storage.get('k');"
            + "window.host.files.save({name:'a.webm',dataBase64:'AA=='});"
            + "navigator.mediaDevices.getUserMedia({audio:true});"
            + "</script></body></html>";

        var result = await _importer.ImportAsync("Recorder", html, null, false, default);

        Assert.True(result.Saved);
        Assert.Contains("storage", result.Permissions);
        Assert.Contains("files", result.Permissions);
        Assert.Contains("media", result.Permissions);
        // 权限已自动补齐：不应出现 PERMISSION_MISSING，也不应有任何致命项
        Assert.DoesNotContain(result.Findings, f => f.Code == "PERMISSION_MISSING");
        Assert.DoesNotContain(result.Findings, f => f.Severity == SecuritySeverity.Fatal);
    }

    [Fact]
    public async Task DoesNotDeclareUnusedPermissions()
    {
        var html = "<html><body><script>window.host.storage.set('k',1);</script></body></html>";
        var result = await _importer.ImportAsync("Notes", html, null, false, default);

        Assert.Equal(new[] { "storage" }, result.Permissions);
    }

    [Fact]
    public async Task RejectsFatalFindings()
    {
        // eval( 是高危，不是致命；这里用「entry 非法」不可能（导入固定 index.html），
        // 所以用一个会触发致命的场景：HTML 里硬编码 Key 只是高危……
        // 致命场景：manifest 结构非法。导入固定字段均合法，故改用超长名称走校验。
        var longName = new string('x', 61);
        await Assert.ThrowsAsync<QTools.Host.Bridge.BridgeException>(
            () => _importer.ImportAsync(longName, PlainHtml, null, false, default));
    }

    [Fact]
    public async Task HighSeverityRequiresConfirmation()
    {
        var html = "<html><body><script>eval('1+1');</script></body></html>";

        var first = await _importer.ImportAsync("EvalTool", html, null, false, default);
        Assert.False(first.Saved);
        Assert.True(first.RequiresExplicitAcceptance);
        Assert.Contains(first.Findings, f => f.Code == "DYNAMIC_CODE");
        Assert.False(Directory.Exists(Path.Combine(_toolsDir, first.ToolId)));

        var second = await _importer.ImportAsync("EvalTool", html, null, true, default);
        Assert.True(second.Saved);
        Assert.True(Directory.Exists(Path.Combine(_toolsDir, second.ToolId)));
    }

    [Fact]
    public async Task RejectsEmptyHtmlAndName()
    {
        await Assert.ThrowsAsync<QTools.Host.Bridge.BridgeException>(
            () => _importer.ImportAsync("", PlainHtml, null, false, default));
        await Assert.ThrowsAsync<QTools.Host.Bridge.BridgeException>(
            () => _importer.ImportAsync("Name", "   ", null, false, default));
    }

    [Fact]
    public async Task RejectsOversizedHtml()
    {
        var huge = "<html><body>" + new string('a', ToolImporter.MaxHtmlBytes + 1024) + "</body></html>";
        var ex = await Assert.ThrowsAsync<QTools.Host.Bridge.BridgeException>(
            () => _importer.ImportAsync("Huge", huge, null, false, default));
        Assert.Equal("IMPORT_HTML_TOO_LARGE", ex.Code);
    }

    [Fact]
    public void DetectsExternalAssetReferences()
    {
        var html = "<html><head><link rel=\"stylesheet\" href=\"style.css\">"
            + "<script src=\"app.js\"></script></head>"
            + "<body><img src=\"logo.png\"><img src=\"data:image/png;base64,AAA\">"
            + "<a href=\"https://example.com\">x</a><a href=\"#top\">y</a></body></html>";

        var assets = ContentSecurityScanner.DetectExternalAssetReferences(html);

        Assert.Contains("style.css", assets);
        Assert.Contains("app.js", assets);
        Assert.Contains("logo.png", assets);
        Assert.DoesNotContain("data:image/png;base64,AAA", assets);
        Assert.DoesNotContain("https://example.com", assets);
        Assert.DoesNotContain("#top", assets);
    }
}
