using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>在线工具：校验规则 / 存储 / 统一列表合并 / 桥接权限与错误码。</summary>
public sealed class WebToolTests : IDisposable
{
    private readonly string _root;
    private readonly WebToolRegistry _webTools;
    private readonly WebToolStore _store;
    private readonly BridgeDispatcher _dispatcher;

    public WebToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-web-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        AppPaths.Initialize(_root);

        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();
        _webTools = new WebToolRegistry(Path.Combine(_root, "webtools"));
        _webTools.Rescan();
        _store = new WebToolStore(_webTools, _webTools.WebToolsDirectory);

        _dispatcher = new BridgeDispatcher(
            tools,
            new ToolStorage(Path.Combine(_root, "data", "storage")),
            null, null, null, null,
            apps, new AppLauncher(), new AppStore(apps, apps.AppsDirectory), null,
            null, null, null, null,
            _webTools, _store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private async Task<JsonElement> DispatchAsync(string method, object payload, string toolId)
    {
        var envelope = new
        {
            id = "req-" + Guid.NewGuid().ToString("n")[..6],
            method,
            toolId,
            payload = JsonSerializer.SerializeToElement(payload)
        };
        var raw = await _dispatcher.DispatchAsync(toolId, JsonSerializer.Serialize(envelope));
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string ErrorCode(JsonElement response)
    {
        Assert.True(response.TryGetProperty("error", out var error), "响应应当是错误信封");
        return error.GetProperty("code").GetString()!;
    }

    private static IReadOnlyList<string> Validate(WebToolEntry entry) =>
        WebToolValidator.Validate(entry, entry.Id);

    /* ================= 校验器 ================= */

    [Theory]
    [InlineData("https://example.com/news")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/search?q=中文&tag=a+b")]
    public void ValidatorAcceptsHttpAndHttpsUrls(string url)
    {
        var entry = new WebToolEntry("news", "资讯站", url, null, null, "");
        Assert.Empty(Validate(entry));
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///C:/Windows/System32")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//example.com/path")]
    [InlineData("example.com/path")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatorRejectsNonHttpUrls(string url)
    {
        var entry = new WebToolEntry("news", "资讯站", url, null, null, "");
        Assert.Contains(Validate(entry), e => e.Contains("url"));
    }

    [Fact]
    public void ValidatorRejectsControlCharactersInUrl()
    {
        var entry = new WebToolEntry("news", "资讯站", "https://example.com/\u0007x", null, null, "");
        Assert.Contains(Validate(entry), e => e.Contains("非法"));
    }

    [Fact]
    public void ValidatorRejectsOverlongUrl()
    {
        var url = "https://example.com/" + new string('a', WebToolValidator.MaxUrlLength);
        var entry = new WebToolEntry("news", "资讯站", url, null, null, "");
        Assert.Contains(Validate(entry), e => e.Contains("2048"));
    }

    [Fact]
    public void ValidatorEnforcesIdDirectoryMatchAndName()
    {
        var mismatch = new WebToolEntry("other", "资讯站", "https://example.com", null, null, "");
        Assert.Contains(WebToolValidator.Validate(mismatch, "news"), e => e.Contains("目录名"));

        var badId = new WebToolEntry("Bad_ID", "资讯站", "https://example.com", null, null, "");
        Assert.Contains(WebToolValidator.Validate(badId, "Bad_ID"), e => e.Contains("id 必须匹配"));

        var longName = new WebToolEntry("news", new string('名', 61), "https://example.com", null, null, "");
        Assert.Contains(WebToolValidator.Validate(longName, "news"), e => e.Contains("name"));
    }

    [Fact]
    public void ValidatorEnforcesCategoryAndGlyphLengths()
    {
        var entry = new WebToolEntry("news", "资讯站", "https://example.com", new string('类', 31), "12345", "");
        var errors = Validate(entry);
        Assert.Contains(errors, e => e.Contains("category"));
        Assert.Contains(errors, e => e.Contains("glyph"));
    }

    /* ================= 存储 ================= */

    [Fact]
    public async Task AddAsyncWritesJsonAndReloadsRegistry()
    {
        var loaded = await _store.AddAsync("GitHub", "https://github.com", null, null, CancellationToken.None);

        Assert.True(loaded.IsValid);
        Assert.Equal("github", loaded.Id);
        var path = Path.Combine(_root, "webtools", "github", "web.json");
        Assert.True(File.Exists(path));

        var roundtrip = JsonSerializer.Deserialize(
            File.ReadAllText(path), AppJsonContext.Default.WebToolEntry);
        Assert.NotNull(roundtrip);
        Assert.Equal("https://github.com", roundtrip!.Url);
        Assert.Equal("GitHub", roundtrip.Name);
        Assert.NotNull(_webTools.Find("github"));
    }

    [Fact]
    public async Task GenerateIdHandlesChineseNamesAndCollisions()
    {
        var chinese = _store.GenerateId("资讯站点");
        Assert.StartsWith("web-", chinese);
        Assert.Matches(AppEntryValidator.AppIdRegexPattern, chinese);

        var first = await _store.AddAsync("News", "https://a.example.com", null, null, CancellationToken.None);
        var second = await _store.AddAsync("News", "https://b.example.com", null, null, CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id + "-2", second.Id);
    }

    [Fact]
    public async Task AddAsyncRejectsInvalidInput()
    {
        await Assert.ThrowsAsync<BridgeException>(() =>
            _store.AddAsync("", "https://example.com", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<BridgeException>(() =>
            _store.AddAsync("X", "", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<BridgeException>(() =>
            _store.AddAsync("X", "javascript:alert(1)", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteRemovesDirectoryAndRegistryEntry()
    {
        var loaded = await _store.AddAsync("GitHub", "https://github.com", null, null, CancellationToken.None);
        Assert.True(_store.Delete(loaded.Id));
        Assert.False(Directory.Exists(Path.Combine(_root, "webtools", loaded.Id)));
        Assert.Null(_webTools.Find(loaded.Id));
        Assert.False(_store.Delete(loaded.Id));
    }

    [Fact]
    public void RescanRejectsInvalidEntryButKeepsOthers()
    {
        var good = Path.Combine(_root, "webtools", "good");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "web.json"),
            JsonSerializer.Serialize(new WebToolEntry("good", "Good", "https://good.example.com", null, null, ""),
                AppJsonContext.Default.WebToolEntry));

        var bad = Path.Combine(_root, "webtools", "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "web.json"),
            JsonSerializer.Serialize(new WebToolEntry("bad", "Bad", "javascript:alert(1)", null, null, ""),
                AppJsonContext.Default.WebToolEntry));

        var hidden = Path.Combine(_root, "webtools", ".versions");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "web.json"), "{}");

        _webTools.Rescan();

        Assert.NotNull(_webTools.Find("good"));
        var badEntry = _webTools.Find("bad");
        Assert.NotNull(badEntry);
        Assert.False(badEntry!.IsValid);
        Assert.Null(_webTools.Find(".versions"));
    }

    /* ================= 统一列表 ================= */

    [Fact]
    public async Task LauncherListIncludesWebKindSortedByUsage()
    {
        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();
        var usage = new UsageStore(Path.Combine(_root, "data", "usage.json"));

        var webA = await _store.AddAsync("A站", "https://a.example.com", "资讯", null, CancellationToken.None);
        var webB = await _store.AddAsync("B站", "https://b.example.com", null, null, CancellationToken.None);
        usage.Record(webB.Id);
        usage.Record(webB.Id);

        var view = LauncherListBuilder.Build(
            tools, apps, null, usage,
            Path.Combine(_root, "data", "icon-cache"), _webTools);

        Assert.Equal(2, view.Items.Count);
        Assert.Equal("web", view.Items[0].Kind);
        Assert.Equal(webB.Id, view.Items[0].Id);
        Assert.Equal(2, view.Items[0].UseCount);
        Assert.True(view.Items[0].TargetExists);
        Assert.Contains("b.example.com", view.Items[0].SearchKey);
        Assert.Equal("https://b.example.com", view.Items[0].Target);
        Assert.Equal("资讯", view.Items[1].Category);
        Assert.Equal("web", view.Items[1].Kind);
    }

    /* ================= 桥接 ================= */

    [Fact]
    public async Task WebToolMethodsAreHostOnly()
    {
        foreach (var method in new[] { "manager.addWebTool", "manager.deleteWebTool", "manager.openWebTool" })
        {
            var response = await DispatchAsync(method, new { name = "x", url = "https://x", webId = "x" }, "evil");
            Assert.Equal("PERMISSION_DENIED", ErrorCode(response));
        }
    }

    [Fact]
    public async Task AddWebToolViaBridgeThenListAndDelete()
    {
        var add = await DispatchAsync("manager.addWebTool",
            new { name = "GitHub", url = "https://github.com", category = (string?)null, glyph = (string?)null },
            BridgeDispatcher.HostToolId);
        Assert.True(add.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.NotNull(_webTools.Find("github"));

        var list = await DispatchAsync("manager.launcherList", new { }, BridgeDispatcher.HostToolId);
        var items = list.GetProperty("result").GetProperty("items").EnumerateArray().ToArray();
        var item = items.Single(i => i.GetProperty("kind").GetString() == "web");
        Assert.Equal("GitHub", item.GetProperty("name").GetString());
        Assert.Equal("github", item.GetProperty("id").GetString());

        var remove = await DispatchAsync("manager.deleteWebTool", new { webId = "github" }, BridgeDispatcher.HostToolId);
        Assert.True(remove.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Null(_webTools.Find("github"));

        var removeAgain = await DispatchAsync("manager.deleteWebTool", new { webId = "github" }, BridgeDispatcher.HostToolId);
        Assert.False(removeAgain.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task AddWebToolRejectsBadUrlViaBridge()
    {
        var response = await DispatchAsync("manager.addWebTool",
            new { name = "Bad", url = "javascript:alert(1)" }, BridgeDispatcher.HostToolId);
        Assert.Equal("WEB_INVALID", ErrorCode(response));
    }

    [Fact]
    public async Task OpenWebToolWithoutShellReturnsInternalError()
    {
        await _store.AddAsync("GitHub", "https://github.com", null, null, CancellationToken.None);

        var missing = await DispatchAsync("manager.openWebTool", new { webId = "ghost" }, BridgeDispatcher.HostToolId);
        Assert.Equal("WEB_NOT_FOUND", ErrorCode(missing));

        var noShell = await DispatchAsync("manager.openWebTool", new { webId = "github" }, BridgeDispatcher.HostToolId);
        Assert.Equal("INTERNAL_ERROR", ErrorCode(noShell));
    }

    [Fact]
    public async Task ContentWindowAcceptsWebView()
    {
        // view=web 应通过白名单（到达壳校验并因无壳返回 INTERNAL_ERROR），而不是 BRIDGE_BAD_REQUEST
        var response = await DispatchAsync("manager.openContent", new { view = "web" }, BridgeDispatcher.HostToolId);
        Assert.Equal("INTERNAL_ERROR", ErrorCode(response));
    }
}
