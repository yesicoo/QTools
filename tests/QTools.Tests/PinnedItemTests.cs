using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>列表置顶：存储持久化 / 统一列表排序 / 桥接方法（与窗口级 Topmost 无关）。</summary>
public sealed class PinnedItemTests : IDisposable
{
    private readonly string _root;
    private readonly string _filePath;

    public PinnedItemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-pin-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        _filePath = Path.Combine(_root, "data", "pinned-items.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private static void WriteTool(string toolsRoot, string id, string name)
    {
        var directory = Path.Combine(toolsRoot, id);
        Directory.CreateDirectory(directory);
        var manifest = new ToolManifest(
            id, name, "1.0.0", null, "index.html",
            Array.Empty<string>(),
            new ToolWindowSettings(640, 480, true),
            "2026-01-01T00:00:00.0000000+00:00",
            AllowEval: false, Csp: null);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, AppJsonContext.Default.ToolManifest));
        File.WriteAllText(Path.Combine(directory, "index.html"), "<h1>" + name + "</h1>");
    }

    [Fact]
    public void SetPinnedPersistsAcrossReloadAndIsolatesKinds()
    {
        var store = new PinnedItemStore(_filePath);
        Assert.False(store.IsPinned("tool", "same"));
        Assert.True(store.SetPinned("tool", "same", true));
        Assert.False(store.SetPinned("tool", "same", true), "重复置顶返回 false");
        Assert.True(store.IsPinned("tool", "same"));
        Assert.False(store.IsPinned("app", "same"), "不同 kind 的同名 id 互不影响");

        var reloaded = new PinnedItemStore(_filePath);
        Assert.True(reloaded.IsPinned("tool", "same"));

        Assert.True(reloaded.SetPinned("tool", "same", false));
        Assert.False(reloaded.SetPinned("tool", "same", false));
        Assert.False(new PinnedItemStore(_filePath).IsPinned("tool", "same"));
    }

    [Fact]
    public void CorruptFileAndBlankKeysAreTolerated()
    {
        var store = new PinnedItemStore(_filePath);
        Assert.False(store.SetPinned("", "x", true));
        Assert.False(store.SetPinned("tool", "  ", true));

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, "{ not json");
        Assert.Equal(0, new PinnedItemStore(_filePath).Count);
    }

    [Fact]
    public void ForgetRemovesEntry()
    {
        var store = new PinnedItemStore(_filePath);
        store.SetPinned("tool", "a", true);
        store.Forget("tool", "a");
        Assert.False(new PinnedItemStore(_filePath).IsPinned("tool", "a"));
    }

    [Fact]
    public void LauncherListSortsPinnedFirstEvenWithLowerUseCount()
    {
        var toolsRoot = Path.Combine(_root, "tools");
        WriteTool(toolsRoot, "hot", "常用工具");
        WriteTool(toolsRoot, "pinned", "置顶工具");
        var tools = new ToolRegistry(toolsRoot);
        tools.Rescan();

        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();

        var usage = new UsageStore(Path.Combine(_root, "data", "usage.json"));
        for (var i = 0; i < 5; i++) { usage.Record("hot"); }
        usage.EnsureSeeded(new[] { "pinned" });

        var pinned = new PinnedItemStore(_filePath);
        var list = LauncherListBuilder.Build(tools, apps, null, usage, Path.Combine(_root, "data", "icon-cache"));
        Assert.Equal("hot", list.Items[0].Id);   // 未置顶时按使用次数排序
        Assert.False(list.Items[0].Pinned);

        pinned.SetPinned("tool", "pinned", true);
        var pinnedList = LauncherListBuilder.Build(
            tools, apps, null, usage, Path.Combine(_root, "data", "icon-cache"), null, null, pinned);
        Assert.Equal("pinned", pinnedList.Items[0].Id);
        Assert.True(pinnedList.Items[0].Pinned);
        Assert.False(pinnedList.Items[1].Pinned);
    }

    /* ================= 桥接 ================= */

    private BridgeDispatcher CreateDispatcher(ToolRegistry tools, AppRegistry apps, PinnedItemStore pinned) =>
        new(tools, new ToolStorage(Path.Combine(_root, "data", "storage")),
            apps: apps, appStore: new AppStore(apps, apps.AppsDirectory), pinnedItems: pinned);

    private static async Task<JsonElement> DispatchAsync(BridgeDispatcher dispatcher, string method, object payload, string toolId)
    {
        var envelope = new
        {
            id = "req-" + Guid.NewGuid().ToString("n")[..6],
            method,
            toolId,
            payload = JsonSerializer.SerializeToElement(payload)
        };
        var raw = await dispatcher.DispatchAsync(toolId, JsonSerializer.Serialize(envelope));
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task SetItemPinnedIsHostOnlyAndToggles()
    {
        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();
        var pinned = new PinnedItemStore(_filePath);
        var dispatcher = CreateDispatcher(tools, apps, pinned);

        var denied = await DispatchAsync(dispatcher, "manager.setItemPinned",
            new { kind = "tool", id = "a", pinned = true }, "evil");
        Assert.Equal("PERMISSION_DENIED", denied.GetProperty("error").GetProperty("code").GetString());

        var ok = await DispatchAsync(dispatcher, "manager.setItemPinned",
            new { kind = "tool", id = "a", pinned = true }, BridgeDispatcher.HostToolId);
        Assert.True(ok.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.True(new PinnedItemStore(_filePath).IsPinned("tool", "a"));

        var again = await DispatchAsync(dispatcher, "manager.setItemPinned",
            new { kind = "tool", id = "a", pinned = true }, BridgeDispatcher.HostToolId);
        Assert.False(again.GetProperty("result").GetProperty("ok").GetBoolean(), "重复置顶返回 changed=false");

        var off = await DispatchAsync(dispatcher, "manager.setItemPinned",
            new { kind = "tool", id = "a", pinned = false }, BridgeDispatcher.HostToolId);
        Assert.True(off.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.False(new PinnedItemStore(_filePath).IsPinned("tool", "a"));
    }

    [Fact]
    public async Task SetItemPinnedRejectsProgramsAndMissingFlag()
    {
        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();
        var dispatcher = CreateDispatcher(tools, apps, new PinnedItemStore(_filePath));

        var program = await DispatchAsync(dispatcher, "manager.setItemPinned",
            new { kind = "program", id = "idx-1", pinned = true }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", program.GetProperty("error").GetProperty("code").GetString());

        var missing = await DispatchAsync(dispatcher, "manager.setItemPinned",
            new { kind = "tool", id = "a" }, BridgeDispatcher.HostToolId);
        Assert.Equal("BRIDGE_BAD_REQUEST", missing.GetProperty("error").GetProperty("code").GetString());
    }
}