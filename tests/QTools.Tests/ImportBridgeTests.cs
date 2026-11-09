using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Creator;
using QTools.Host.Security;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 导入功能的真实桥接链路验证：驱动 <see cref="BridgeDispatcher.DispatchAsync"/>，
/// 覆盖「信封 → 方法表 → hostOnly 校验 → ToolImporter → 落盘 → 注册表重载」全路径。
/// </summary>
public sealed class ImportBridgeTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolsDir;
    private readonly ToolRegistry _registry;
    private readonly BridgeDispatcher _dispatcher;

    public ImportBridgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-import-bridge-" + Guid.NewGuid().ToString("n")[..8]);
        _toolsDir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(_toolsDir);

        AppPaths.Initialize(_root);
        _registry = new ToolRegistry(_toolsDir);
        _registry.Rescan();

        var storage = new ToolStorage(Path.Combine(_root, "data", "storage"));
        var importer = new ToolImporter(_registry, _toolsDir);
        _dispatcher = new BridgeDispatcher(_registry, storage, null, null, null, importer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private static string BuildRequest(string method, object payload, string toolId)
    {
        var envelope = new
        {
            id = "req-1",
            method,
            toolId,
            payload = JsonSerializer.SerializeToElement(payload)
        };
        return JsonSerializer.Serialize(envelope);
    }

    private async Task<JsonElement> DispatchAsync(string method, object payload, string toolId)
    {
        var raw = await _dispatcher.DispatchAsync(toolId, BuildRequest(method, payload, toolId));
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>成功时返回 result 节点；失败时断言 error.code 并返回 null。</summary>
    private static JsonElement RequireOk(JsonElement response, out JsonElement data)
    {
        if (response.TryGetProperty("result", out var result))
        {
            data = result;
            return result;
        }

        var code = response.TryGetProperty("error", out var err)
            ? err.GetProperty("code").GetString()
            : "(no error)";
        var message = response.TryGetProperty("error", out var err2)
            ? err2.GetProperty("message").GetString()
            : "(no message)";
        Assert.Fail($"桥接返回错误 {code}: {message}");
        data = default;
        return default;
    }

    [Fact]
    public async Task HostCanImportPlainHtml()
    {
        var html = "<!DOCTYPE html><html><body><h1>unit</h1></body></html>";

        var response = await DispatchAsync(
            "manager.importTool",
            new { name = "Unit Converter", html, description = (string?)null, acceptHighSeverity = false },
            BridgeDispatcher.HostToolId);

        var data = RequireOk(response, out _);
        Assert.True(data.GetProperty("saved").GetBoolean());
        Assert.Equal("unit-converter", data.GetProperty("toolId").GetString());
        Assert.Equal("/tools/unit-converter/index.html", data.GetProperty("url").GetString());

        // 真的落到磁盘且能被注册表加载
        var manifest = Path.Combine(_toolsDir, "unit-converter", "manifest.json");
        Assert.True(File.Exists(manifest));
        var loaded = _registry.Reload("unit-converter");
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsValid);
    }

    [Fact]
    public async Task NonHostToolCannotImport()
    {
        // 一个普通工具（无权限）尝试调用 manager.importTool，必须被拒
        var toolDir = Path.Combine(_toolsDir, "sneaky");
        Directory.CreateDirectory(toolDir);
        await File.WriteAllTextAsync(Path.Combine(toolDir, "manifest.json"),
            """{"id":"sneaky","name":"S","version":"1.0.0","description":"d","entry":"index.html","permissions":[],"window":{"width":400,"height":400,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");
        await File.WriteAllTextAsync(Path.Combine(toolDir, "index.html"), "<html><body>x</body></html>");
        _registry.Rescan();

        var response = await DispatchAsync(
            "manager.importTool",
            new { name = "Hijack", html = "<html></html>", description = (string?)null, acceptHighSeverity = false },
            "sneaky");

        Assert.True(response.TryGetProperty("error", out var err));
        Assert.Equal("PERMISSION_DENIED", err.GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(_toolsDir, "hijack")));
    }

    [Fact]
    public async Task ImportRequiresConfirmationForHighSeverity()
    {
        var html = "<html><body><script>eval('1');</script></body></html>";

        var first = await DispatchAsync(
            "manager.importTool",
            new { name = "EvalTool", html, description = (string?)null, acceptHighSeverity = false },
            BridgeDispatcher.HostToolId);

        var firstData = RequireOk(first, out _);
        Assert.False(firstData.GetProperty("saved").GetBoolean());
        Assert.True(firstData.GetProperty("requiresExplicitAcceptance").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(_toolsDir, "evaltool")));

        var second = await DispatchAsync(
            "manager.importTool",
            new { name = "EvalTool", html, description = (string?)null, acceptHighSeverity = true },
            BridgeDispatcher.HostToolId);

        var secondData = RequireOk(second, out _);
        Assert.True(secondData.GetProperty("saved").GetBoolean());
        Assert.True(Directory.Exists(Path.Combine(_toolsDir, "evaltool")));
    }

    [Fact]
    public async Task ImportDetectsPermissionsAndWarnsAboutExternalAssets()
    {
        var html = "<html><head><link rel=\"stylesheet\" href=\"style.css\"></head><body>"
            + "<script>window.host.storage.get('k');navigator.mediaDevices.getUserMedia({audio:true});</script>"
            + "</body></html>";

        var response = await DispatchAsync(
            "manager.importTool",
            new { name = "Recorder", html, description = (string?)null, acceptHighSeverity = false },
            BridgeDispatcher.HostToolId);

        var data = RequireOk(response, out _);
        Assert.True(data.GetProperty("saved").GetBoolean());

        var perms = data.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("storage", perms);
        Assert.Contains("media", perms);

        var assets = data.GetProperty("externalAssets").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Contains("style.css", assets);
    }
}
