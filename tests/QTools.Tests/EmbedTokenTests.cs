using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Security;
using QTools.Host.Shell;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 主窗口 iframe 内嵌工具的凭证机制：
/// manager.embedTool 签发 → bridge 消息携带 token → 分发器把身份解析为对应 toolId，
/// 并验证「无效凭证拒绝 / 工具借内嵌凭证不能越过自身权限 / 不影响独立窗口语义」。
/// </summary>
public sealed class EmbedTokenTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolsDir;
    private readonly ToolRegistry _registry;
    private readonly BridgeDispatcher _dispatcher;

    public EmbedTokenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-embed-" + Guid.NewGuid().ToString("n")[..8]);
        _toolsDir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(_toolsDir);

        AppPaths.Initialize(_root);
        _registry = new ToolRegistry(_toolsDir);
        _dispatcher = new BridgeDispatcher(_registry, new ToolStorage(Path.Combine(_root, "data", "storage")));
        _dispatcher.ServerInfo = new ServerInfoView("test", 5000, "测试", "WebView", null, null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    /// <summary>创建一个 manifest 合法、带指定权限的工具。</summary>
    private void CreateTool(string id, string[] permissions)
    {
        var toolDir = Path.Combine(_toolsDir, id);
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "manifest.json"),
            $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","description":"d","entry":"index.html","permissions":[{{string.Join(",", permissions.Select(p => "\"" + p + "\""))}}],"window":{"width":400,"height":400,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");
        File.WriteAllText(Path.Combine(toolDir, "index.html"), "<html><body>" + id + "</body></html>");
        _registry.Rescan();
    }

    private async Task<JsonElement> DispatchAsync(string raw)
    {
        var response = await _dispatcher.DispatchAsync(BridgeDispatcher.HostToolId, raw);
        var document = JsonDocument.Parse(response);
        return document.RootElement.Clone();
    }

    private static string Envelope(string method, object? payload, string toolId, string? token = null)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "t-1",
            ["toolId"] = toolId,
            ["method"] = method,
            ["payload"] = payload,
            ["token"] = token,
        });
    }

    private static string TokenOf(JsonElement issueReply)
    {
        return issueReply.GetProperty("result").GetProperty("url").GetString()!.Split("bt=")[1];
    }

    [Fact]
    public async Task EmbedIssuesTokenAndResolvesToolIdentityForStorage()
    {
        CreateTool("notes-a", ["storage"]);

        var issue = await DispatchAsync(Envelope("manager.embedTool", new { toolId = "notes-a" }, BridgeDispatcher.HostToolId));
        Assert.True(issue.TryGetProperty("result", out var embed), issue.GetRawText());
        Assert.Equal("notes-a", embed.GetProperty("toolId").GetString());
        var url = embed.GetProperty("url").GetString();
        Assert.NotNull(url);
        Assert.StartsWith("http://127.0.0.1:5000/tools/notes-a/index.html?bt=", url);
        var token = TokenOf(issue);

        // 携带凭证 → 身份解析为 notes-a，其 storage 权限生效
        var setReply = await DispatchAsync(Envelope("storage.set", new { key = "k", value = 42 }, "notes-a", token));
        Assert.True(setReply.TryGetProperty("result", out _), setReply.GetRawText());

        var getReply = await DispatchAsync(Envelope("storage.get", new { key = "k" }, "notes-a", token));
        Assert.True(getReply.TryGetProperty("result", out var got), getReply.GetRawText());
        Assert.True(got.GetProperty("found").GetBoolean());
        Assert.Equal(42, got.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task EmbedDeniedWithoutTokenAndWithInvalidToken()
    {
        CreateTool("plain-b", ["storage"]);

        // 无凭证：宿主绑定 __host__，storage.* 不对宿主开放
        var noToken = await DispatchAsync(Envelope("storage.get", new { key = "k" }, "plain-b"));
        Assert.True(noToken.TryGetProperty("error", out var e0));
        Assert.Equal("PERMISSION_DENIED", e0.GetProperty("code").GetString());

        // 无效凭证直接拒绝
        var badToken = await DispatchAsync(Envelope("storage.get", new { key = "k" }, "plain-b", new string('f', 32)));
        Assert.True(badToken.TryGetProperty("error", out var e1));
        Assert.Equal("PERMISSION_DENIED", e1.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EmbeddedToolCannotCallHostOnlyMethods()
    {
        CreateTool("noted-c", []);

        var issue = await DispatchAsync(Envelope("manager.embedTool", new { toolId = "noted-c" }, BridgeDispatcher.HostToolId));
        var token = TokenOf(issue);

        var reply = await DispatchAsync(Envelope("manager.listTools", new { }, "noted-c", token));
        Assert.True(reply.TryGetProperty("error", out var err));
        Assert.Equal("PERMISSION_DENIED", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EmbeddedToolCannotUsePermissionsItsManifestDoesNotDeclare()
    {
        // 无权限的工具拿到内嵌凭证，storage.* 仍然被拒
        CreateTool("naked-d", []);

        var issue = await DispatchAsync(Envelope("manager.embedTool", new { toolId = "naked-d" }, BridgeDispatcher.HostToolId));
        var token = TokenOf(issue);

        var reply = await DispatchAsync(Envelope("storage.set", new { key = "k", value = 1 }, "naked-d", token));
        Assert.True(reply.TryGetProperty("error", out var err));
        Assert.Equal("PERMISSION_DENIED", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NewIssuanceSupersedesPreviousToken()
    {
        CreateTool("swap-e", ["storage"]);

        var first = await DispatchAsync(Envelope("manager.embedTool", new { toolId = "swap-e" }, BridgeDispatcher.HostToolId));
        var oldToken = TokenOf(first);

        var second = await DispatchAsync(Envelope("manager.embedTool", new { toolId = "swap-e" }, BridgeDispatcher.HostToolId));
        var newToken = TokenOf(second);
        Assert.NotEqual(oldToken, newToken);

        // 旧凭证已被替换：失效并拒绝
        var stale = await DispatchAsync(Envelope("storage.set", new { key = "k", value = 1 }, "swap-e", oldToken));
        Assert.True(stale.TryGetProperty("error", out var err));
        Assert.Equal("PERMISSION_DENIED", err.GetProperty("code").GetString());

        var fresh = await DispatchAsync(Envelope("storage.set", new { key = "k", value = 1 }, "swap-e", newToken));
        Assert.True(fresh.TryGetProperty("result", out _), fresh.GetRawText());
    }

    [Fact]
    public async Task RevokeInvalidatesToken()
    {
        CreateTool("gone-f", ["storage"]);
        var issue = await DispatchAsync(Envelope("manager.embedTool", new { toolId = "gone-f" }, BridgeDispatcher.HostToolId));
        var token = TokenOf(issue);

        _dispatcher.EmbedTokens.Revoke("gone-f");

        var reply = await DispatchAsync(Envelope("storage.get", new { key = "k" }, "gone-f", token));
        Assert.True(reply.TryGetProperty("error", out var err));
        Assert.Equal("PERMISSION_DENIED", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EmbedFailsForMissingTool()
    {
        var reply = await DispatchAsync(Envelope("manager.embedTool", new { toolId = "no-such" }, BridgeDispatcher.HostToolId));
        Assert.True(reply.TryGetProperty("error", out var err));
        Assert.Equal("TOOL_NOT_FOUND", err.GetProperty("code").GetString());
    }

    [Fact]
    public void TokenRegistryIssuesDistinctTokensAndRejectsForeignInput()
    {
        var registry = new EmbedTokenRegistry();
        var a = registry.Issue("a");
        var b = registry.Issue("b");

        Assert.NotEqual(a, b);
        Assert.Equal(32, a.Length);
        Assert.Equal("a", registry.Resolve(a));
        Assert.Equal("b", registry.Resolve(b));
        Assert.Null(registry.Resolve(null));
        Assert.Null(registry.Resolve(""));
        Assert.Null(registry.Resolve("zzz"));
        Assert.Null(registry.Resolve(a[..31]));  // 缺一位
    }
}
