using System.Text.Json;
using QTools.Host.Configuration;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>本机程序自动索引：条目整理 / 稳定 id / 原子覆盖 / 后台刷新期间旧索引始终可用。</summary>
public sealed class ProgramIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _indexPath;
    private readonly string _iconDir;

    public ProgramIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-idx-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        AppPaths.Initialize(_root);
        _indexPath = Path.Combine(_root, "data", "program-index.json");
        _iconDir = Path.Combine(_root, "data", "icon-cache");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void BuildEntriesFiltersUninstallersEmptyAndDuplicates()
    {
        var programs = new[]
        {
            new DiscoveredProgram("正常程序", @"C:\App\a.exe", "开始菜单", "开发"),
            new DiscoveredProgram("Uninstall 正常程序", @"C:\App\unins.exe", "开始菜单", null),
            new DiscoveredProgram("卸载助手", @"C:\App\un.exe", "开始菜单", null),
            new DiscoveredProgram("unins000", @"C:\App\u.exe", "注册表", null),
            new DiscoveredProgram("   ", @"C:\App\b.exe", "开始菜单", null),
            new DiscoveredProgram("重复程序", @"C:\APP\A.EXE", "目录", null),
            new DiscoveredProgram("空白目标", "   ", "开始菜单", null),
            new DiscoveredProgram("  带空格  ", @"  C:\App\c.exe  ", "开始菜单", null),
        };

        var entries = ProgramIndex.BuildEntries(programs);

        Assert.Equal(2, entries.Count);
        Assert.Equal("正常程序", entries[0].Name);
        Assert.Equal("开发", entries[0].Category);
        Assert.Equal(ProgramIndex.BuildId(@"C:\App\a.exe"), entries[0].Id);
        Assert.Equal("带空格", entries[1].Name);
        Assert.Equal(@"C:\App\c.exe", entries[1].Target);
    }

    [Fact]
    public void BuildIdIsStableCaseInsensitiveAndLowercaseHex()
    {
        var first = ProgramIndex.BuildId(@"C:\App\X.exe");
        var second = ProgramIndex.BuildId(@"c:\app\x.exe");

        Assert.Equal(first, second);
        Assert.StartsWith("idx-", first);
        Assert.Equal(16, first.Length);
        Assert.Matches("^idx-[0-9a-f]{12}$", first);
        Assert.NotEqual(first, ProgramIndex.BuildId(@"C:\App\Y.exe"));
    }

    [Fact]
    public void WriteAtomicRoundTripsOverwritesAndLeavesNoTempFile()
    {
        var data = new ProgramIndexFile(1, "2026-01-01T00:00:00.0000000+00:00",
            new[] { new IndexEntry("idx-a", "程序A", @"C:\a.exe", "测试", null) });
        ProgramIndex.WriteAtomic(_indexPath, data);

        var loaded = JsonSerializer.Deserialize(
            File.ReadAllText(_indexPath), AppJsonContext.Default.ProgramIndexFile);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Single(loaded.Entries);
        Assert.Equal("idx-a", loaded.Entries[0].Id);

        var replaced = new ProgramIndexFile(1, "2026-01-02T00:00:00.0000000+00:00",
            Array.Empty<IndexEntry>());
        ProgramIndex.WriteAtomic(_indexPath, replaced);

        var loaded2 = JsonSerializer.Deserialize(
            File.ReadAllText(_indexPath), AppJsonContext.Default.ProgramIndexFile);
        Assert.NotNull(loaded2);
        Assert.Empty(loaded2.Entries);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_indexPath)!, "program-index.json.tmp-*"));
    }

    [Fact]
    public void LoadToleratesCorruptFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        File.WriteAllText(_indexPath, "{ not json");

        var index = new ProgramIndex(_indexPath, new ProgramDiscovery(), _iconDir);

        Assert.Equal(0, index.Count);
        Assert.Null(index.LastIndexedUtc);
    }

    [Fact]
    public void LoadFiltersEntriesWithBlankIdOrTarget()
    {
        var data = new ProgramIndexFile(1, "2026-01-01T00:00:00.0000000+00:00", new[]
        {
            new IndexEntry("idx-good", "好条目", @"C:\good.exe", "测试", null),
            new IndexEntry("", "空 id", @"C:\x.exe", "测试", null),
            new IndexEntry("idx-bad", "空目标", "   ", "测试", null),
        });
        ProgramIndex.WriteAtomic(_indexPath, data);

        var index = new ProgramIndex(_indexPath, new ProgramDiscovery(), _iconDir);

        Assert.Equal(1, index.Count);
        Assert.NotNull(index.Find("idx-good"));
        Assert.Null(index.Find("idx-bad"));
    }

    [Fact]
    public async Task BackgroundRefreshKeepsOldIndexUntilSwapAndFiresCompleted()
    {
        var oldEntry = new IndexEntry("idx-old", "旧程序", @"C:\old\old.exe", "测试", null);
        ProgramIndex.WriteAtomic(_indexPath, new ProgramIndexFile(
            1, "2026-01-01T00:00:00.0000000+00:00", new[] { oldEntry }));
        var index = new ProgramIndex(_indexPath, new ProgramDiscovery(), _iconDir);
        Assert.Equal(1, index.Count);

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Completed += () => completed.TrySetResult();

        Assert.True(index.StartBackgroundRefresh());
        // 核心诉求：后台索引期间旧索引必须仍可用，不能出现「找不到上次已索引程序」的空窗
        if (index.IsIndexing)
        {
            Assert.NotNull(index.Find("idx-old"));
        }

        var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(completed.Task, finished);
        Assert.False(index.IsIndexing);
        Assert.NotNull(index.LastIndexedUtc);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_indexPath)!, "program-index.json.tmp-*"));

        var file = JsonSerializer.Deserialize(
            File.ReadAllText(_indexPath), AppJsonContext.Default.ProgramIndexFile);
        Assert.NotNull(file);
        Assert.Equal(ProgramIndex.CurrentSchemaVersion, file.SchemaVersion);
    }
}

/// <summary>使用频次统计：累加 / 持久化 / 重载 / 容错。</summary>
public sealed class UsageStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _filePath;

    public UsageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-use-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        _filePath = Path.Combine(_root, "data", "usage.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void RecordIncrementsPersistsAndReloads()
    {
        var store = new UsageStore(_filePath);
        store.Record("a");
        store.Record("a");
        store.Record("b");

        Assert.Equal(2, store.Get("a")!.Count);
        Assert.Equal(1, store.Get("b")!.Count);
        Assert.False(string.IsNullOrWhiteSpace(store.Get("a")!.LastUsedUtc));

        var reloaded = new UsageStore(_filePath);
        Assert.Equal(2, reloaded.Get("a")!.Count);
        Assert.Equal(1, reloaded.Get("b")!.Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_filePath)!, "usage.json.tmp-*"));
    }

    [Fact]
    public void RecordIgnoresWhitespaceId()
    {
        var store = new UsageStore(_filePath);
        store.Record("");
        store.Record("   ");

        Assert.Null(store.Get(""));
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public void LoadToleratesCorruptFileAndKeepsWorking()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, "{ not json");

        var store = new UsageStore(_filePath);
        Assert.Null(store.Get("a"));

        store.Record("a");
        Assert.Equal(1, new UsageStore(_filePath).Get("a")!.Count);
    }

    [Fact]
    public void LoadFiltersInvalidEntries()
    {
        var file = new UsageFile(1, new[]
        {
            new UsageEntry("good", 3, "2026-01-01T00:00:00.0000000+00:00"),
            new UsageEntry("", 5, "2026-01-01T00:00:00.0000000+00:00"),
            new UsageEntry("zero", 0, "2026-01-01T00:00:00.0000000+00:00"),
        });
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(file, AppJsonContext.Default.UsageFile));

        var store = new UsageStore(_filePath);

        Assert.Equal(3, store.Get("good")!.Count);
        Assert.Null(store.Get(""));
        Assert.Null(store.Get("zero"));
    }
}

/// <summary>统一列表构建：三类来源合并 / 手动项优先去重 / 使用频次排序 / 索引状态。</summary>
public sealed class LauncherListBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly ToolRegistry _tools;
    private readonly AppRegistry _apps;
    private readonly AppStore _appStore;
    private readonly string _iconDir;
    private readonly string _indexPath;
    private readonly string _usagePath;

    public LauncherListBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-ull-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        AppPaths.Initialize(_root);
        _iconDir = Path.Combine(_root, "data", "icon-cache");
        _indexPath = Path.Combine(_root, "data", "program-index.json");
        _usagePath = Path.Combine(_root, "data", "usage.json");

        _tools = new ToolRegistry(Path.Combine(_root, "tools"));
        _apps = new AppRegistry(Path.Combine(_root, "apps"));
        _apps.Rescan();
        _appStore = new AppStore(_apps, _apps.AppsDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private void AddDemoTool(string name)
    {
        var toolDir = Path.Combine(_root, "tools", "demo");
        Directory.CreateDirectory(toolDir);
        var manifest = new
        {
            id = "demo", name, version = "1.0.0", description = "d", entry = "index.html",
            permissions = Array.Empty<string>(),
            window = new { width = 400, height = 400, resizable = true },
            createdAt = "2026-11-04T00:00:00+08:00", allowEval = false, csp = (string?)null
        };
        File.WriteAllText(Path.Combine(toolDir, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(toolDir, "index.html"), "<html>x</html>");
        _tools.Rescan();
    }

    private ProgramIndex WriteIndex(params IndexEntry[] entries)
    {
        ProgramIndex.WriteAtomic(_indexPath, new ProgramIndexFile(
            1, "2026-01-01T00:00:00.0000000+00:00", entries));
        return new ProgramIndex(_indexPath, new ProgramDiscovery(), _iconDir);
    }

    [Fact]
    public async Task MergesThreeKindsAndManualTargetTakesPrecedence()
    {
        AddDemoTool("演示工具");
        var manualTarget = Path.Combine(_root, "manual.exe");
        await File.WriteAllTextAsync(manualTarget, "MZ");
        var app = await _appStore.AddAsync("Manual App", manualTarget, null, null, null, null, default);

        var index = WriteIndex(
            new IndexEntry("idx-dup", "重复程序", manualTarget, "测试", null),
            new IndexEntry("idx-unique", "索引程序", Path.Combine(_root, "other.exe"), "测试", "开发"));

        var view = LauncherListBuilder.Build(_tools, _apps, index, null, _iconDir);

        Assert.Equal(3, view.Items.Count);
        Assert.Contains(view.Items, i => i.Kind == "tool" && i.Id == "demo");
        Assert.Contains(view.Items, i => i.Kind == "app" && i.Id == app.Id);
        var program = Assert.Single(view.Items, i => i.Kind == "program");
        Assert.Equal("idx-unique", program.Id);
        Assert.Equal("开发", program.Category);
        Assert.False(program.TargetExists);
        Assert.Equal(2, view.Index.ProgramCount);
        Assert.False(view.Index.Indexing);
    }

    [Fact]
    public async Task SortsByUseCountDescending()
    {
        AddDemoTool("演示工具");
        var app = await _appStore.AddAsync("Manual App", Path.Combine(_root, "manual.exe"),
            null, null, null, null, default);
        var index = WriteIndex(new IndexEntry("idx-hot", "热门程序", Path.Combine(_root, "hot.exe"), "测试", null));

        var usage = new UsageStore(_usagePath);
        usage.Record("demo");
        usage.Record("demo");
        usage.Record("demo");
        usage.Record("idx-hot");
        usage.Record("idx-hot");
        usage.Record(app.Id);

        var view = LauncherListBuilder.Build(_tools, _apps, index, usage, _iconDir);

        Assert.Equal(new[] { "demo", "idx-hot", app.Id }, view.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { 3, 2, 1 }, view.Items.Select(i => i.UseCount).ToArray());
    }

    [Fact]
    public void ZeroUsageSortsByNameAndTiesBreakByLastUsed()
    {
        AddDemoTool("Alpha工具");
        var index = WriteIndex(
            new IndexEntry("idx-a", "B程序", @"C:\b.exe", "测试", null),
            new IndexEntry("idx-b", "A程序", @"C:\a.exe", "测试", null));

        // 无使用记录：名称升序（ASCII 先于中文）
        var view = LauncherListBuilder.Build(_tools, new AppRegistry(_apps.AppsDirectory), index, null, _iconDir);
        Assert.Equal(new[] { "demo", "idx-b", "idx-a" }, view.Items.Select(i => i.Id).ToArray());

        // 次数相同时最近使用优先（哪怕名称排序靠后）
        var usageFile = new UsageFile(1, new[]
        {
            new UsageEntry("idx-a", 2, "2026-01-02T00:00:00.0000000+00:00"),
            new UsageEntry("idx-b", 2, "2026-01-01T00:00:00.0000000+00:00"),
        });
        File.WriteAllText(_usagePath, JsonSerializer.Serialize(usageFile, AppJsonContext.Default.UsageFile));
        var usage = new UsageStore(_usagePath);

        var view2 = LauncherListBuilder.Build(_tools, new AppRegistry(_apps.AppsDirectory), index, usage, _iconDir);
        Assert.Equal("idx-a", view2.Items[0].Id);
        Assert.Equal("idx-b", view2.Items[1].Id);
    }

    [Fact]
    public void NullIndexYieldsEmptyStatusAndOnlyToolsAndApps()
    {
        AddDemoTool("演示工具");

        var view = LauncherListBuilder.Build(_tools, _apps, null, null, _iconDir);

        Assert.Single(view.Items);
        Assert.Equal("tool", view.Items[0].Kind);
        Assert.False(view.Index.Indexing);
        Assert.Null(view.Index.LastIndexedUtc);
        Assert.Equal(0, view.Index.ProgramCount);
    }
}