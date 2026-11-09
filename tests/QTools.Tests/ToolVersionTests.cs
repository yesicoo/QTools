using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>工具版本管理：快照 / 切换 / 回滚 / 删除 / 版本号递增。</summary>
public sealed class ToolVersionTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolsDir;
    private readonly ToolRegistry _registry;

    public ToolVersionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-ver-" + Guid.NewGuid().ToString("n")[..8]);
        _toolsDir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(_toolsDir);
        _registry = new ToolRegistry(_toolsDir);
        _registry.Rescan();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private async Task<LoadedTool> CreateToolAsync(string id, string version, string html)
    {
        var dir = Path.Combine(_toolsDir, id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), $$"""
            {"id":"{{id}}","name":"{{id}}","version":"{{version}}","description":"d","entry":"index.html","permissions":[],"window":{"width":500,"height":500,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), html);
        return _registry.Reload(id)!;
    }

    [Fact]
    public async Task LegacyToolIsMigratedToVersionedLayout()
    {
        var tool = await CreateToolAsync("legacy", "1.0.0", "<html>v1</html>");

        Assert.Single(tool.Versions);
        Assert.Equal("1.0.0", tool.Versions[0].Version);
        Assert.True(tool.Versions[0].IsActive);
        Assert.True(Directory.Exists(Path.Combine(tool.Directory, ".versions", "1.0.0")));
    }

    [Fact]
    public async Task SwitchVersionActivatesSnapshotAndUpdatesManifest()
    {
        var tool = await CreateToolAsync("switcher", "1.0.0", "<html>v1</html>");

        // 手工放入一个 1.1.0 快照
        var snapshot = Path.Combine(tool.Directory, ".versions", "1.1.0");
        Directory.CreateDirectory(snapshot);
        await File.WriteAllTextAsync(Path.Combine(snapshot, "index.html"), "<html>v2</html>");
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"),
            """{"id":"switcher","name":"switcher","version":"1.1.0","description":"d","entry":"index.html","permissions":[],"window":{"width":500,"height":500,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");

        var switched = _registry.SwitchVersion("switcher", "1.1.0");

        Assert.Equal("1.1.0", switched.ActiveVersion);
        Assert.Equal("<html>v2</html>", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "index.html")));
        Assert.Contains("1.1.0", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "manifest.json")));
        Assert.True(switched.IsValid);
    }

    [Fact]
    public async Task RollbackRestoresOldContentAndKeepsNewerVersion()
    {
        var tool = await CreateToolAsync("rollback", "1.0.0", "<html>original</html>");
        var snapshot = Path.Combine(tool.Directory, ".versions", "1.1.0");
        Directory.CreateDirectory(snapshot);
        await File.WriteAllTextAsync(Path.Combine(snapshot, "index.html"), "<html>newer</html>");
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"),
            """{"id":"rollback","name":"rollback","version":"1.1.0","description":"d","entry":"index.html","permissions":[],"window":{"width":500,"height":500,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");

        _registry.SwitchVersion("rollback", "1.1.0");
        Assert.Equal("<html>newer</html>", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "index.html")));

        // 回滚
        var back = _registry.SwitchVersion("rollback", "1.0.0");
        Assert.Equal("1.0.0", back.ActiveVersion);
        Assert.Equal("<html>original</html>", await File.ReadAllTextAsync(Path.Combine(tool.Directory, "index.html")));

        // 1.1.0 仍然可以再切回去
        var again = _registry.SwitchVersion("rollback", "1.1.0");
        Assert.Equal("1.1.0", again.ActiveVersion);
        Assert.Contains(again.Versions, v => v.Version == "1.0.0");
        Assert.Contains(again.Versions, v => v.Version == "1.1.0");
    }

    [Fact]
    public async Task CannotDeleteActiveVersion()
    {
        var tool = await CreateToolAsync("nodelete", "1.0.0", "<html>x</html>");

        var ex = Assert.Throws<BridgeException>(() => _registry.DeleteVersion("nodelete", "1.0.0"));
        Assert.Equal("VERSION_ACTIVE", ex.Code);
    }

    [Fact]
    public async Task DeleteNonActiveVersionRemovesSnapshot()
    {
        var tool = await CreateToolAsync("delver", "1.0.0", "<html>x</html>");
        var snapshot = Path.Combine(tool.Directory, ".versions", "1.1.0");
        Directory.CreateDirectory(snapshot);
        await File.WriteAllTextAsync(Path.Combine(snapshot, "index.html"), "<html>y</html>");

        var reloaded = _registry.DeleteVersion("delver", "1.1.0");

        Assert.DoesNotContain(reloaded.Versions, v => v.Version == "1.1.0");
        Assert.False(Directory.Exists(snapshot));
    }

    [Fact]
    public async Task SwitchToMissingVersionThrows()
    {
        await CreateToolAsync("missing", "1.0.0", "<html>x</html>");
        var ex = Assert.Throws<BridgeException>(() => _registry.SwitchVersion("missing", "9.9.9"));
        Assert.Equal("VERSION_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task SwitchRejectsInvalidVersionString()
    {
        await CreateToolAsync("badver", "1.0.0", "<html>x</html>");
        var ex = Assert.Throws<BridgeException>(() => _registry.SwitchVersion("badver", "../../etc"));
        Assert.Equal("VERSION_INVALID", ex.Code);
    }

    [Fact]
    public void NextVersionBumpsCorrectly()
    {
        ToolVersion[] V(params string[] versions) => versions
            .Select(v => new ToolVersion(v, "2026-01-01T00:00:00+00:00", 1, false, null))
            .ToArray();

        Assert.Equal("1.0.1", ToolVersionStore.NextVersion(V("1.0.0"), VersionBump.Patch));
        Assert.Equal("1.1.0", ToolVersionStore.NextVersion(V("1.0.0"), VersionBump.Minor));
        Assert.Equal("2.0.0", ToolVersionStore.NextVersion(V("1.0.0"), VersionBump.Major));

        // 多版本时基于最高版本递增，而不是最新创建的
        Assert.Equal("1.1.1", ToolVersionStore.NextVersion(V("1.0.0", "1.1.0"), VersionBump.Patch));
        Assert.Equal("1.2.0", ToolVersionStore.NextVersion(V("1.0.0", "1.1.0", "1.0.5"), VersionBump.Minor));

        // 无历史版本时从 1.0.0 起
        Assert.Equal("1.0.1", ToolVersionStore.NextVersion(Array.Empty<ToolVersion>(), VersionBump.Patch));
    }

    [Fact]
    public async Task SnapshotDoesNotOverwriteExistingVersion()
    {
        var dir = Path.Combine(_toolsDir, "snap");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), "<html>first</html>");
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), "{}");

        Assert.True(_registry.VersionStore.Snapshot(dir, "1.0.0"));
        await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), "<html>second</html>");

        // 同版本号不覆盖
        Assert.False(_registry.VersionStore.Snapshot(dir, "1.0.0"));
        Assert.Equal("<html>first</html>",
            await File.ReadAllTextAsync(Path.Combine(dir, ".versions", "1.0.0", "index.html")));
    }

    [Fact]
    public void VersionsDirectoryIsNotWebServable()
    {
        // .versions 是点号前缀目录，静态服务规则 3b 会拒绝——这里固化该约定
        Assert.StartsWith(".", ToolVersionStore.VersionsDirectoryName);
    }
}
