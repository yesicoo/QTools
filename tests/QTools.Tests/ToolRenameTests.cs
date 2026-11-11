using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 小工具重命名（manager.renameTool / ToolRegistry.Rename）：
/// 只改显示名，锁定 id/目录/版本/入口不变；hostOnly；空名/超长/未知工具的明确拒绝。
/// </summary>
public sealed class ToolRenameTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolsDir;
    private readonly ToolRegistry _registry;
    private readonly BridgeDispatcher _dispatcher;

    public ToolRenameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-rename-" + Guid.NewGuid().ToString("n")[..8]);
        _toolsDir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(_toolsDir);

        AppPaths.Initialize(_root);
        _registry = new ToolRegistry(_toolsDir);
        _dispatcher = new BridgeDispatcher(_registry, new ToolStorage(Path.Combine(_root, "data", "storage")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private void CreateTool(string id)
    {
        var toolDir = Path.Combine(_toolsDir, id);
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "manifest.json"),
            $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","description":"d","entry":"index.html","permissions":[],"window":{"width":400,"height":400,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");
        File.WriteAllText(Path.Combine(toolDir, "index.html"), "<html><body>" + id + "</body></html>");
        _registry.Rescan();
    }

    private async Task<JsonElement> DispatchAsync(string method, object? payload, string toolId)
    {
        var envelope = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "r-1",
            ["toolId"] = toolId,
            ["method"] = method,
            ["payload"] = payload,
        });
        var response = await _dispatcher.DispatchAsync(toolId, envelope);
        using var document = JsonDocument.Parse(response);
        return document.RootElement.Clone();
    }

    private static string ErrorCode(JsonElement response)
    {
        Assert.True(response.TryGetProperty("error", out var error), response.GetRawText());
        return error.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task RenameIsHostOnly()
    {
        CreateTool("demo");
        var response = await DispatchAsync("manager.renameTool", new { toolId = "demo", name = "x" }, "evil");
        Assert.Equal("PERMISSION_DENIED", ErrorCode(response));
    }

    [Fact]
    public async Task RenameUpdatesNameAndPreservesIdentity()
    {
        CreateTool("demo");
        var response = await DispatchAsync("manager.renameTool",
            new { toolId = "demo", name = "我的工具" }, BridgeDispatcher.HostToolId);
        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean(), response.GetRawText());

        var tool = _registry.Find("demo")!;
        Assert.Equal("我的工具", tool.Name);
        Assert.Equal("demo", tool.Id);
        Assert.Equal("1.0.0", tool.Manifest.Version);
        Assert.Equal("index.html", tool.Manifest.Entry);
        Assert.True(tool.IsValid);

        // 落盘持久化：新名写入、旧名消失，且目录名不变
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(_toolsDir, "demo", "manifest.json"));
        using var persisted = JsonDocument.Parse(manifestJson);
        Assert.Equal("我的工具", persisted.RootElement.GetProperty("name").GetString());
        Assert.Equal("demo", persisted.RootElement.GetProperty("id").GetString());
        Assert.True(Directory.Exists(Path.Combine(_toolsDir, "demo")));
        Assert.False(Directory.Exists(Path.Combine(_toolsDir, "我的工具")));
    }

    [Fact]
    public async Task RenameTrimsWhitespace()
    {
        CreateTool("demo");
        var response = await DispatchAsync("manager.renameTool",
            new { toolId = "demo", name = "  带空格  " }, BridgeDispatcher.HostToolId);
        Assert.True(response.TryGetProperty("result", out _), response.GetRawText());
        Assert.Equal("带空格", _registry.Find("demo")!.Name);
    }

    [Fact]
    public async Task RenameRejectsEmptyName()
    {
        CreateTool("demo");
        var response = await DispatchAsync("manager.renameTool",
            new { toolId = "demo", name = "" }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(response));
        Assert.Equal("demo", _registry.Find("demo")!.Name);
    }

    [Fact]
    public async Task RenameRejectsWhitespaceOnlyName()
    {
        CreateTool("demo");
        var response = await DispatchAsync("manager.renameTool",
            new { toolId = "demo", name = "   " }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(response));
    }

    [Fact]
    public async Task RenameRejectsMissingName()
    {
        CreateTool("demo");
        var response = await DispatchAsync("manager.renameTool",
            new { toolId = "demo" }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(response));
    }

    [Fact]
    public async Task RenameRejectsTooLongName()
    {
        CreateTool("demo");
        var response = await DispatchAsync("manager.renameTool",
            new { toolId = "demo", name = new string('x', 61) }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(response));
        Assert.Equal("demo", _registry.Find("demo")!.Name);
    }

    [Fact]
    public async Task RenameUnknownToolReturnsNotFound()
    {
        var response = await DispatchAsync("manager.renameTool",
            new { toolId = "ghost", name = "x" }, BridgeDispatcher.HostToolId);
        Assert.Equal("TOOL_NOT_FOUND", ErrorCode(response));
    }

    [Fact]
    public void RegistryRenameRejectsTooLongName()
    {
        CreateTool("demo");
        var ex = Assert.Throws<BridgeException>(() => _registry.Rename("demo", new string('x', 61)));
        Assert.Equal("MANIFEST_INVALID", ex.Code);
    }

    [Fact]
    public void RegistryRenameUnknownToolThrows()
    {
        var ex = Assert.Throws<BridgeException>(() => _registry.Rename("ghost", "x"));
        Assert.Equal("TOOL_NOT_FOUND", ex.Code);
    }
}