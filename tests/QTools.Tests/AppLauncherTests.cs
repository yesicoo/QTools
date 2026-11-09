using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>本机程序启动器：启动项校验 / 增删 / ID 生成 / 跨平台启动信息构建。</summary>
public sealed class AppLauncherTests : IDisposable
{
    private readonly string _root;
    private readonly string _appsDir;
    private readonly AppRegistry _registry;
    private readonly AppStore _store;

    public AppLauncherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-app-" + Guid.NewGuid().ToString("n")[..8]);
        _appsDir = Path.Combine(_root, "apps");
        Directory.CreateDirectory(_appsDir);
        AppPaths.Initialize(_root);
        _registry = new AppRegistry(_appsDir);
        _registry.Rescan();
        _store = new AppStore(_registry, _appsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void ValidatorRejectsBadIdAndMissingTarget()
    {
        var bad = new AppEntry("Bad_Id", "n", "", null, null, null, null, "2026-01-01T00:00:00+00:00");
        var errors = AppEntryValidator.Validate(bad, "Bad_Id");

        Assert.Contains(errors, e => e.Contains("id"));
        Assert.Contains(errors, e => e.Contains("target"));
    }

    [Fact]
    public void ValidatorAcceptsWellFormedEntry()
    {
        var good = new AppEntry("code", "VS Code", @"C:\app\Code.exe", "--new-window",
            @"C:\app", "开发工具", "</>", "2026-01-01T00:00:00+00:00");
        Assert.Empty(AppEntryValidator.Validate(good, "code"));
    }

    [Fact]
    public void ValidatorRejectsOverlongFields()
    {
        var entry = new AppEntry("x", new string('a', 61), "C:\\a.exe", new string('b', 2001),
            null, new string('c', 31), "abcde", "2026-01-01T00:00:00+00:00");
        var errors = AppEntryValidator.Validate(entry, "x");

        Assert.Contains(errors, e => e.Contains("name"));
        Assert.Contains(errors, e => e.Contains("arguments"));
        Assert.Contains(errors, e => e.Contains("category"));
        Assert.Contains(errors, e => e.Contains("glyph"));
    }

    [Fact]
    public async Task AddAppWritesManifestAndRegisters()
    {
        var app = await _store.AddAsync("Visual Studio Code", @"C:\app\Code.exe", null, null,
            "开发工具", "</>", default);

        Assert.Equal("visual-studio-code", app.Id);
        Assert.True(app.IsValid);
        Assert.True(File.Exists(Path.Combine(_appsDir, "visual-studio-code", "app.json")));
        Assert.Equal(app.Id, _registry.Find("visual-studio-code")!.Id);
    }

    [Fact]
    public async Task AddAppWithChineseNameGetsStableHashId()
    {
        var first = await _store.AddAsync("腾讯会议", @"C:\app\a.exe", null, null, null, null, default);
        var second = await _store.AddAsync("腾讯会议", @"C:\app\b.exe", null, null, null, null, default);

        Assert.StartsWith("app-", first.Id);
        Assert.Equal(first.Id, second.Id[..first.Id.Length]);
        Assert.NotEqual(first.Id, second.Id);   // 重名自动加后缀
    }

    [Fact]
    public async Task DeleteAppRemovesDirectoryButNotSystemProgram()
    {
        var target = Path.Combine(_root, "fake.exe");
        await File.WriteAllTextAsync(target, "MZ");

        var app = await _store.AddAsync("Fake", target, null, null, null, null, default);
        Assert.True(_store.Delete(app.Id));

        Assert.False(Directory.Exists(Path.Combine(_appsDir, app.Id)));
        Assert.True(File.Exists(target));   // 系统里的程序本身不受影响
        Assert.Null(_registry.Find(app.Id));
    }

    [Fact]
    public async Task LaunchFailsWhenTargetMissing()
    {
        var app = await _store.AddAsync("Ghost", @"C:\nope\missing.exe", null, null, null, null, default);
        var launcher = new AppLauncher();

        var ex = Assert.Throws<BridgeException>(() => launcher.Launch(app));
        Assert.Equal("APP_TARGET_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task LaunchRunsRealExecutable()
    {
        // 用一个无害的真实可执行文件验证 Process.Start 链路（AOT 下可用）
        var target = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/bin/echo";

        Assert.True(File.Exists(target), $"测试用可执行文件应存在：{target}");

        var app = await _store.AddAsync("Where", target, null, null, null, null, default);
        var launcher = new AppLauncher();

        var result = launcher.Launch(app);

        Assert.True(result.Started);
        Assert.Equal("Where", result.AppName);
    }

    [Fact]
    public async Task RegistrySkipsDotDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_appsDir, ".hidden"));
        await File.WriteAllTextAsync(Path.Combine(_appsDir, ".hidden", "app.json"), "{}");
        _registry.Rescan();

        Assert.DoesNotContain(_registry.GetAll(), a => a.Id == ".hidden");
    }

    [Fact]
    public async Task RegistrySkipsMalformedEntry()
    {
        Directory.CreateDirectory(Path.Combine(_appsDir, "broken"));
        await File.WriteAllTextAsync(Path.Combine(_appsDir, "broken", "app.json"), "{ not json");
        _registry.Rescan();

        Assert.DoesNotContain(_registry.GetAll(), a => a.Id == "broken");
    }
}

/// <summary>manager.* 启动器桥接方法（hostOnly + 增删 + 扫描）。</summary>
public sealed class LauncherBridgeTests : IDisposable
{
    private readonly string _root;
    private readonly string _appsDir;
    private readonly AppRegistry _apps;
    private readonly AppStore _appStore;
    private readonly BridgeDispatcher _dispatcher;

    public LauncherBridgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-appb-" + Guid.NewGuid().ToString("n")[..8]);
        _appsDir = Path.Combine(_root, "apps");
        Directory.CreateDirectory(_appsDir);
        AppPaths.Initialize(_root);

        var tools = new ToolRegistry(Path.Combine(_root, "tools"));
        tools.Rescan();
        _apps = new AppRegistry(_appsDir);
        _apps.Rescan();
        _appStore = new AppStore(_apps, _appsDir);

        _dispatcher = new BridgeDispatcher(
            tools,
            new ToolStorage(Path.Combine(_root, "data", "storage")),
            null, null, null, null,
            _apps, new AppLauncher(), _appStore, new ProgramDiscovery());
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

    [Fact]
    public async Task HostCanAddListAndDeleteApp()
    {
        // 用一个本机必然存在的可执行文件，保证 targetExists 语义可验证
        var target = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/bin/echo";

        var add = await DispatchAsync("manager.addApp",
            new { name = "Notepad", target, arguments = (string?)null,
                  workingDirectory = (string?)null, category = "系统", glyph = (string?)null },
            BridgeDispatcher.HostToolId);

        Assert.True(add.GetProperty("result").GetProperty("ok").GetBoolean());

        var list = await DispatchAsync("manager.listApps", new { }, BridgeDispatcher.HostToolId);
        var apps = list.GetProperty("result").GetProperty("apps").EnumerateArray().ToArray();
        Assert.Single(apps);
        Assert.Equal("Notepad", apps[0].GetProperty("name").GetString());
        Assert.Equal("notepad", apps[0].GetProperty("id").GetString());
        Assert.True(apps[0].GetProperty("targetExists").GetBoolean());
        Assert.Equal("系统", apps[0].GetProperty("category").GetString());

        var del = await DispatchAsync("manager.deleteApp", new { appId = "notepad" }, BridgeDispatcher.HostToolId);
        Assert.True(del.GetProperty("result").GetProperty("ok").GetBoolean());

        var list2 = await DispatchAsync("manager.listApps", new { }, BridgeDispatcher.HostToolId);
        Assert.Empty(list2.GetProperty("result").GetProperty("apps").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task NonHostToolCannotLaunchApp()
    {
        var toolDir = Path.Combine(_root, "tools", "evil");
        Directory.CreateDirectory(toolDir);
        await File.WriteAllTextAsync(Path.Combine(toolDir, "manifest.json"),
            """{"id":"evil","name":"e","version":"1.0.0","description":"d","entry":"index.html","permissions":[],"window":{"width":400,"height":400,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");
        await File.WriteAllTextAsync(Path.Combine(toolDir, "index.html"), "<html>x</html>");
        _dispatcher.GetType(); // no-op, tools registry already scanned in ctor

        var response = await DispatchAsync("manager.launchApp", new { appId = "whatever" }, "evil");

        Assert.True(response.TryGetProperty("error", out var err));
        Assert.Equal("PERMISSION_DENIED", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LaunchUnknownAppReturnsError()
    {
        var response = await DispatchAsync("manager.launchApp", new { appId = "nope" }, BridgeDispatcher.HostToolId);

        Assert.True(response.TryGetProperty("error", out var err));
        Assert.Equal("APP_NOT_FOUND", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DiscoverProgramsReturnsCandidates()
    {
        var response = await DispatchAsync("manager.discoverPrograms", new { }, BridgeDispatcher.HostToolId);
        var result = response.GetProperty("result");
        var programs = result.GetProperty("programs").EnumerateArray().ToArray();

        // Windows 上开始菜单至少有内容；其他平台可能为空，但接口必须可用
        Assert.NotNull(programs);
    }

    [Fact]
    public async Task AddAppRejectsEmptyName()
    {
        var response = await DispatchAsync("manager.addApp",
            new { name = "", target = @"C:\x.exe", arguments = (string?)null,
                  workingDirectory = (string?)null, category = (string?)null, glyph = (string?)null },
            BridgeDispatcher.HostToolId);

        Assert.True(response.TryGetProperty("error", out var err));
        Assert.Equal("APP_NAME_REQUIRED", err.GetProperty("code").GetString());
    }
}
