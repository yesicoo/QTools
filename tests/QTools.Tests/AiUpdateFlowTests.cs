using System.Net;
using System.Text;
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
/// AI 更新的完整链路（进程内 fake HTTP，不打真实网络）：
/// 桥接 → ToolCreator.UpdateAsync → AiOutputParser → 安全审查 → 存为新版本 → 激活。
/// </summary>
public sealed class AiUpdateFlowTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolsDir;
    private readonly ToolRegistry _registry;
    private BridgeDispatcher _dispatcher = null!;

    public AiUpdateFlowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-upd-" + Guid.NewGuid().ToString("n")[..8]);
        _toolsDir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(_toolsDir);
        AppPaths.Initialize(_root);
        _registry = new ToolRegistry(_toolsDir);
        _registry.Rescan();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private async Task<LoadedTool> CreateToolAsync(string id, string html)
    {
        var dir = Path.Combine(_toolsDir, id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), $$"""
            {"id":"{{id}}","name":"{{id}}","version":"1.0.0","description":"d","entry":"index.html","permissions":[],"window":{"width":600,"height":600,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), html);
        return _registry.Reload(id)!;
    }

    /// <summary>返回一个把预设 JSON 作为 assistant content 回包的假 HttpClient。</summary>
    private static HttpClient FakeAiClient(string responseJson)
    {
        var handler = new FakeHandler(responseJson);
        return new HttpClient(handler);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FakeHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static ISecretStore FakeSecrets()
    {
        var store = new EncryptedFileSecretStore(
            Path.Combine(Path.GetTempPath(), "qtools-secrets-" + Guid.NewGuid().ToString("n")[..8]));
        store.Set(SecretStoreFactory.AiServiceName, SecretStoreFactory.AiAccountName, "sk-fake-key-000");
        return store;
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

    private void WireDispatcher(string aiResponseJson)
    {
        var config = new AppConfig(
            new AiConfigSection("https://api.example.com/v1", "mock", 0.7, 30),
            new ServerConfigSection(1024 * 1024, 8 * 1024 * 1024),
            new StorageConfigSection(256 * 1024, 5 * 1024 * 1024));
        var ai = new AiClient(FakeAiClient(aiResponseJson), FakeSecrets(), config);
        var creator = new ToolCreator(ai, _registry);
        _dispatcher = new BridgeDispatcher(_registry, new ToolStorage(Path.Combine(_root, "data", "storage")),
            ai, FakeSecrets(), creator);
    }

    private static string BuildAiResponse(string manifest, string html)
    {
        var gen = "{\"manifest\":" + manifest + ",\"indexHtml\":" + JsonSerializer.Serialize(html) + "}";
        var wrapped = "```json\n" + gen + "\n```";
        return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" +
               JsonSerializer.Serialize(wrapped) + "}}]}";
    }

    [Fact]
    public async Task AiUpdateCreatesNewVersionAndKeepsOldOne()
    {
        await CreateToolAsync("mytool", "<html><body><h1>v1</h1></body></html>");

        var newManifest = """{"id":"mytool","name":"mytool","version":"1.0.1","description":"updated","entry":"index.html","permissions":[],"window":{"width":600,"height":600,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""";
        WireDispatcher(BuildAiResponse(newManifest, "<html><body><h1>v1.0.1</h1></body></html>"));

        // 1) 生成新版本（审查）
        var generated = await DispatchAsync("manager.generateUpdate",
            new { toolId = "mytool", instruction = "add dark mode", versionBump = "patch" },
            BridgeDispatcher.HostToolId);

        var view = generated.GetProperty("result");
        Assert.Equal("1.0.1", view.GetProperty("manifest").GetProperty("version").GetString());
        Assert.Contains("v1.0.1", view.GetProperty("indexHtml").GetString());

        // 2) 应用
        var applied = await DispatchAsync("manager.applyUpdate",
            new { toolId = "mytool", generated = view, acceptHighSeverity = false },
            BridgeDispatcher.HostToolId);
        Assert.True(applied.GetProperty("result").GetProperty("ok").GetBoolean());

        // 3) 磁盘状态：根目录是新版本，.versions 下两个版本都在
        var tool = _registry.Find("mytool")!;
        Assert.Equal("1.0.1", tool.ActiveVersion);
        Assert.Contains("<h1>v1.0.1</h1>", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "index.html")));
        Assert.True(File.Exists(Path.Combine(tool.Directory, ".versions", "1.0.0", "index.html")));
        Assert.True(File.Exists(Path.Combine(tool.Directory, ".versions", "1.0.1", "index.html")));

        // 4) 版本列表：1.0.1 激活、1.0.0 历史
        var list = await DispatchAsync("manager.listVersions", new { toolId = "mytool" }, BridgeDispatcher.HostToolId);
        var versions = list.GetProperty("result").GetProperty("versions").EnumerateArray().ToArray();
        Assert.Equal(2, versions.Length);
        Assert.Equal("1.0.1", list.GetProperty("result").GetProperty("activeVersion").GetString());
        Assert.Contains(versions, v => v.GetProperty("version").GetString() == "1.0.1" &&
                                       v.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task UpdateThenRollbackRestoresOriginal()
    {
        var tool = await CreateToolAsync("rollme", "<html><body><h1>ORIGINAL</h1></body></html>");

        var newManifest = """{"id":"rollme","name":"rollme","version":"1.1.0","description":"updated","entry":"index.html","permissions":[],"window":{"width":600,"height":600,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""";
        WireDispatcher(BuildAiResponse(newManifest, "<html><body><h1>UPDATED</h1></body></html>"));

        var generated = await DispatchAsync("manager.generateUpdate",
            new { toolId = "rollme", instruction = "make it better", versionBump = "minor" },
            BridgeDispatcher.HostToolId);
        await DispatchAsync("manager.applyUpdate",
            new { toolId = "rollme", generated = generated.GetProperty("result"), acceptHighSeverity = false },
            BridgeDispatcher.HostToolId);

        Assert.Contains("<h1>UPDATED</h1>", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "index.html")));

        // 回滚到 1.0.0
        var rolled = await DispatchAsync("manager.switchVersion",
            new { toolId = "rollme", version = "1.0.0" }, BridgeDispatcher.HostToolId);
        Assert.True(rolled.GetProperty("result").GetProperty("ok").GetBoolean());

        Assert.Equal("1.0.0", _registry.Find("rollme")!.ActiveVersion);
        Assert.Contains("<h1>ORIGINAL</h1>", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "index.html")));

        // 1.1.0 仍在，可再次前进
        var forward = await DispatchAsync("manager.switchVersion",
            new { toolId = "rollme", version = "1.1.0" }, BridgeDispatcher.HostToolId);
        Assert.True(forward.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Contains("<h1>UPDATED</h1>", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "index.html")));
    }

    [Fact]
    public async Task UpdatePreservesToolIdentity()
    {
        await CreateToolAsync("identity", "<html>x</html>");

        // AI 试图把 id / name / entry 改掉
        var evilManifest = """{"id":"hijacked","name":"Hijacked","version":"1.0.1","description":"d","entry":"evil.html","permissions":["ai"],"window":{"width":600,"height":600,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""";
        WireDispatcher(BuildAiResponse(evilManifest, "<html>ok</html>"));

        var generated = await DispatchAsync("manager.generateUpdate",
            new { toolId = "identity", instruction = "change everything", versionBump = "patch" },
            BridgeDispatcher.HostToolId);

        var manifest = generated.GetProperty("result").GetProperty("manifest");
        Assert.Equal("identity", manifest.GetProperty("id").GetString());   // id 被强制保留
        Assert.Equal("identity", manifest.GetProperty("name").GetString()); // name 被强制保留
        Assert.Equal("index.html", manifest.GetProperty("entry").GetString());
        Assert.Equal("1.0.1", manifest.GetProperty("version").GetString());
    }

    [Fact]
    public async Task NonHostToolCannotSwitchVersion()
    {
        var toolDir = Path.Combine(_toolsDir, "guard");
        Directory.CreateDirectory(toolDir);
        await File.WriteAllTextAsync(Path.Combine(toolDir, "manifest.json"),
            """{"id":"guard","name":"g","version":"1.0.0","description":"d","entry":"index.html","permissions":[],"window":{"width":400,"height":400,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");
        await File.WriteAllTextAsync(Path.Combine(toolDir, "index.html"), "<html>x</html>");
        _registry.Rescan();

        WireDispatcher(BuildAiResponse("{}", "<html>x</html>"));

        var response = await DispatchAsync("manager.switchVersion",
            new { toolId = "guard", version = "1.0.0" }, "guard");

        Assert.True(response.TryGetProperty("error", out var err));
        Assert.Equal("PERMISSION_DENIED", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdatePreservesUserFilesAcrossVersions()
    {
        var tool = await CreateToolAsync("withfiles", "<html>x</html>");

        // 用户的二进制数据（如录音）放在 files/，跨版本必须保留
        var filesDir = Path.Combine(tool.Directory, "files");
        Directory.CreateDirectory(filesDir);
        await File.WriteAllTextAsync(Path.Combine(filesDir, "rec.webm"), "AUDIO-DATA");

        var newManifest = """{"id":"withfiles","name":"withfiles","version":"1.0.1","description":"d","entry":"index.html","permissions":[],"window":{"width":600,"height":600,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""";
        WireDispatcher(BuildAiResponse(newManifest, "<html>y</html>"));

        var generated = await DispatchAsync("manager.generateUpdate",
            new { toolId = "withfiles", instruction = "tweak", versionBump = "patch" },
            BridgeDispatcher.HostToolId);
        await DispatchAsync("manager.applyUpdate",
            new { toolId = "withfiles", generated = generated.GetProperty("result"), acceptHighSeverity = false },
            BridgeDispatcher.HostToolId);

        Assert.True(File.Exists(Path.Combine(filesDir, "rec.webm")));
        Assert.Equal("AUDIO-DATA", await File.ReadAllTextAsync(Path.Combine(filesDir, "rec.webm")));

        // 回滚后文件依然在
        await DispatchAsync("manager.switchVersion",
            new { toolId = "withfiles", version = "1.0.0" }, BridgeDispatcher.HostToolId);
        Assert.Equal("AUDIO-DATA", await File.ReadAllTextAsync(Path.Combine(filesDir, "rec.webm")));
    }
}
