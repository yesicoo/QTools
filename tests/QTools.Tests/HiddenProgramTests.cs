using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>索引程序隐藏列表：存储 / 统一列表过滤 / 桥接权限与错误码。</summary>
public sealed class HiddenProgramTests : IDisposable
{
    private readonly string _root;
    private readonly string _filePath;
    private readonly BridgeDispatcher _dispatcher;

    public HiddenProgramTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-hide-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        AppPaths.Initialize(_root);
        _filePath = Path.Combine(_root, "data", "hidden-programs.json");

        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();

        _dispatcher = new BridgeDispatcher(
            tools,
            new ToolStorage(Path.Combine(_root, "data", "storage")),
            null, null, null, null,
            apps, new AppLauncher(), new AppStore(apps, apps.AppsDirectory), null,
            null, null, null, null,
            null, null,
            new HiddenProgramStore(_filePath));
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

    /* ================= 存储 ================= */

    [Fact]
    public void HidePersistsAcrossReloadAndUnhideRestores()
    {
        var store = new HiddenProgramStore(_filePath);
        Assert.False(store.IsHidden("idx-a"));
        Assert.True(store.Hide("idx-a"));
        Assert.False(store.Hide("idx-a"), "重复隐藏应返回 false");
        Assert.True(store.IsHidden("idx-a"));

        // 新实例从磁盘恢复，隐藏记录跨进程 / 跨重新索引保留
        var reloaded = new HiddenProgramStore(_filePath);
        Assert.True(reloaded.IsHidden("idx-a"));

        Assert.True(reloaded.Unhide("idx-a"));
        Assert.False(reloaded.Unhide("idx-a"));
        Assert.False(new HiddenProgramStore(_filePath).IsHidden("idx-a"));
    }

    [Fact]
    public void HideIgnoresBlankIdAndCorruptFileIsTolerated()
    {
        var store = new HiddenProgramStore(_filePath);
        Assert.False(store.Hide(""));
        Assert.False(store.Hide("  "));

        File.WriteAllText(_filePath, "{ not json");
        Assert.Equal(0, new HiddenProgramStore(_filePath).Count);
    }

    /* ================= 统一列表 ================= */

    [Fact]
    public void LauncherListExcludesHiddenPrograms()
    {
        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();

        var target = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/bin/echo";
        var id = ProgramIndex.BuildId(target);
        var indexPath = Path.Combine(_root, "data", "program-index.json");
        ProgramIndex.WriteAtomic(indexPath, new ProgramIndexFile(1, "2026-01-01T00:00:00.0000000+00:00",
            new[]
            {
                new IndexEntry(id, "可见程序", target, "", null),
                new IndexEntry("idx-hidden", "被隐藏程序", target, "", null),
            }));
        var index = new ProgramIndex(indexPath, new ProgramDiscovery(), Path.Combine(_root, "data", "icon-cache"));
        var hidden = new HiddenProgramStore(_filePath);
        hidden.Hide("idx-hidden");

        var view = LauncherListBuilder.Build(tools, apps, index, null,
            Path.Combine(_root, "data", "icon-cache"), null, hidden);

        Assert.DoesNotContain(view.Items, item => item.Id == "idx-hidden");
        Assert.Contains(view.Items, item => item.Id == id);
    }

    /* ================= 桥接 ================= */

    [Fact]
    public async Task HideProgramMethodsAreHostOnly()
    {
        foreach (var method in new[] { "manager.hideProgram", "manager.unhideProgram" })
        {
            var response = await DispatchAsync(method, new { programId = "idx-x" }, "evil");
            Assert.Equal("PERMISSION_DENIED", ErrorCode(response));
        }
    }

    [Fact]
    public async Task HideAndUnhideViaBridge()
    {
        var hide = await DispatchAsync("manager.hideProgram", new { programId = "idx-x" }, BridgeDispatcher.HostToolId);
        Assert.True(hide.GetProperty("result").GetProperty("ok").GetBoolean());

        var hideAgain = await DispatchAsync("manager.hideProgram", new { programId = "idx-x" }, BridgeDispatcher.HostToolId);
        Assert.False(hideAgain.GetProperty("result").GetProperty("ok").GetBoolean());

        var unhide = await DispatchAsync("manager.unhideProgram", new { programId = "idx-x" }, BridgeDispatcher.HostToolId);
        Assert.True(unhide.GetProperty("result").GetProperty("ok").GetBoolean());

        Assert.False(new HiddenProgramStore(_filePath).IsHidden("idx-x"));
    }
}
