using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>统一启动器桥接方法：launcherList / reindexPrograms / openContent / closeContent。</summary>
public sealed class UnifiedLauncherBridgeTests : IDisposable
{
    private readonly string _root;
    private readonly AppRegistry _apps;
    private readonly AppStore _appStore;
    private readonly ProgramIndex _index;
    private readonly UsageStore _usage;
    private readonly BridgeDispatcher _dispatcher;
    private readonly string _indexTarget;
    private readonly string _indexId;

    public UnifiedLauncherBridgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-ulb-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        AppPaths.Initialize(_root);

        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        _apps = new AppRegistry(Path.Combine(_root, "apps"));
        _apps.Rescan();
        _appStore = new AppStore(_apps, _apps.AppsDirectory);

        // 索引条目指向一个本机必然存在的无害可执行文件，验证「无 app.json 也能直接启动」
        _indexTarget = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/bin/echo";
        _indexId = ProgramIndex.BuildId(_indexTarget);
        var indexPath = Path.Combine(_root, "data", "program-index.json");
        ProgramIndex.WriteAtomic(indexPath, new ProgramIndexFile(1, "2026-01-01T00:00:00.0000000+00:00",
            new[] { new IndexEntry(_indexId, "索引程序", _indexTarget, "测试", null) }));
        _index = new ProgramIndex(indexPath, new ProgramDiscovery(), Path.Combine(_root, "data", "icon-cache"));
        _usage = new UsageStore(Path.Combine(_root, "data", "usage.json"));

        _dispatcher = new BridgeDispatcher(
            tools,
            new ToolStorage(Path.Combine(_root, "data", "storage")),
            null, null, null, null,
            _apps, new AppLauncher(), _appStore, new ProgramDiscovery(),
            null, null, _index, _usage);
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

    [Fact]
    public async Task NewManagerMethodsAreHostOnly()
    {
        foreach (var method in new[]
        {
            "manager.launcherList", "manager.reindexPrograms",
            "manager.openContent", "manager.closeContent"
        })
        {
            var response = await DispatchAsync(method, new { view = "create", enabled = true }, "evil");
            Assert.Equal("PERMISSION_DENIED", ErrorCode(response));
        }
    }

    [Fact]
    public async Task LauncherListMergesKindsAndSortsByUsage()
    {
        var manualTarget = Path.Combine(_root, "manual.exe");
        await File.WriteAllTextAsync(manualTarget, "MZ");
        var add = await DispatchAsync("manager.addApp",
            new { name = "Manual", target = manualTarget, arguments = (string?)null,
                  workingDirectory = (string?)null, category = (string?)null, glyph = (string?)null },
            BridgeDispatcher.HostToolId);
        Assert.True(add.GetProperty("result").GetProperty("ok").GetBoolean());

        _usage.Record(_indexId);
        _usage.Record(_indexId);

        var response = await DispatchAsync("manager.launcherList", new { }, BridgeDispatcher.HostToolId);
        var result = response.GetProperty("result");
        var items = result.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.Equal(_indexId, items[0].GetProperty("id").GetString());
        Assert.Equal("program", items[0].GetProperty("kind").GetString());
        Assert.Equal(2, items[0].GetProperty("useCount").GetInt32());
        Assert.True(items[0].GetProperty("targetExists").GetBoolean());
        Assert.Equal(_indexTarget, items[0].GetProperty("target").GetString());
        Assert.Equal("app", items[1].GetProperty("kind").GetString());
        Assert.Equal(1, items[1].GetProperty("useCount").GetInt32());
        Assert.Equal(manualTarget, items[1].GetProperty("target").GetString());

        var index = result.GetProperty("index");
        Assert.False(index.GetProperty("indexing").GetBoolean());
        Assert.Equal(1, index.GetProperty("programCount").GetInt32());
    }

    [Fact]
    public async Task LaunchAppFallsBackToIndexEntryAndRecordsUsage()
    {
        var response = await DispatchAsync("manager.launchApp", new { appId = _indexId }, BridgeDispatcher.HostToolId);
        var result = response.GetProperty("result");

        Assert.True(result.GetProperty("started").GetBoolean());
        Assert.Equal("索引程序", result.GetProperty("appName").GetString());
        Assert.Equal(1, _usage.Get(_indexId)!.Count);
    }

    [Fact]
    public async Task LaunchUnknownAppStillReturnsNotFound()
    {
        var response = await DispatchAsync("manager.launchApp", new { appId = "nope" }, BridgeDispatcher.HostToolId);
        Assert.Equal("APP_NOT_FOUND", ErrorCode(response));
    }

    [Fact]
    public async Task RevealItemIsHostOnly()
    {
        var response = await DispatchAsync("manager.revealItem", new { kind = "app", id = "x" }, "evil");
        Assert.Equal("PERMISSION_DENIED", ErrorCode(response));
    }

    [Fact]
    public async Task RevealItemRejectsUnknownKind()
    {
        var response = await DispatchAsync("manager.revealItem", new { kind = "bogus", id = "x" }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(response));
    }

    [Fact]
    public async Task RevealItemRejectsUnknownId()
    {
        var response = await DispatchAsync("manager.revealItem", new { kind = "program", id = "nope" }, BridgeDispatcher.HostToolId);
        Assert.Equal("ITEM_NOT_FOUND", ErrorCode(response));
    }

    [Fact]
    public async Task RevealItemRejectsMissingTarget()
    {
        // 目标不存在的启动项可以录入（列表标红），但「属性」定位必须给明确错误而不是弹资源管理器
        var app = await _appStore.AddAsync("幽灵", Path.Combine(_root, "ghost.exe"),
            null, null, null, null, CancellationToken.None);
        var response = await DispatchAsync("manager.revealItem", new { kind = "app", id = app.Id }, BridgeDispatcher.HostToolId);
        Assert.Equal("APP_TARGET_NOT_FOUND", ErrorCode(response));
    }

    [Fact]
    public async Task ReindexProgramsReturnsStatusAndCompletes()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _index.Completed += () => completed.TrySetResult();

        var response = await DispatchAsync("manager.reindexPrograms", new { }, BridgeDispatcher.HostToolId);
        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("indexing").ValueKind is JsonValueKind.True or JsonValueKind.False);

        // 等后台扫描结束再清理临时目录，避免后台线程写进已删除的目录
        var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(completed.Task, finished);
        Assert.False(_index.IsIndexing);
    }

    [Fact]
    public async Task OpenContentRejectsUnknownView()
    {
        var response = await DispatchAsync("manager.openContent", new { view = "bogus" }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(response));
    }

    [Fact]
    public async Task OpenContentRejectsUnknownTool()
    {
        var response = await DispatchAsync("manager.openContent",
            new { view = "tool", toolId = "ghost" }, BridgeDispatcher.HostToolId);
        Assert.Equal("TOOL_NOT_FOUND", ErrorCode(response));
    }

    [Fact]
    public async Task ContentWindowMethodsRequireShell()
    {
        // 单测环境没有窗口壳：合法载荷应得到明确的 INTERNAL_ERROR，而不是静默成功
        var open = await DispatchAsync("manager.openContent", new { view = "create" }, BridgeDispatcher.HostToolId);
        Assert.Equal("INTERNAL_ERROR", ErrorCode(open));

        var close = await DispatchAsync("manager.closeContent", new { }, BridgeDispatcher.HostToolId);
        Assert.Equal("INTERNAL_ERROR", ErrorCode(close));
    }
}