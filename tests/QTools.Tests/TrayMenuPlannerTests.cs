using QTools.Host.Launcher;
using QTools.Host.Shell;
using Xunit;

namespace QTools.Tests;

/// <summary>托盘菜单内容：默认取前 10 项、数量可配置、保持界面顺序、附带类型与图标；弹层定位不越出工作区。</summary>
public sealed class TrayMenuPlannerTests
{
    private static LauncherItemView Item(
        string id, string kind, string name, string? glyph = null, string? iconUrl = null) => new(
        id, kind, name, null, iconUrl, name, true, true, 0, null, glyph);

    [Fact]
    public void TakesDefaultTenInListOrder()
    {
        var items = Enumerable.Range(1, 12)
            .Select(i => Item($"id-{i}", LauncherListBuilder.KindProgram, $"程序{i}"))
            .ToArray();

        var entries = TrayMenuPlanner.FromLauncherItems(items, TrayMenuPlanner.DefaultItems);

        Assert.Equal(10, entries.Count);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(i => $"程序{i}").ToArray(),
            entries.Select(e => e.Name).ToArray());
        Assert.Equal(
            Enumerable.Range(1, 10).Select(i => $"id-{i}").ToArray(),
            entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void CustomCountTakesConfiguredNumber()
    {
        var items = Enumerable.Range(1, 12)
            .Select(i => Item($"id-{i}", LauncherListBuilder.KindTool, $"工具{i}"))
            .ToArray();

        Assert.Equal(3, TrayMenuPlanner.FromLauncherItems(items, 3).Count);
        Assert.Equal(12, TrayMenuPlanner.FromLauncherItems(items, 12).Count);

        var many = Enumerable.Range(1, 25)
            .Select(i => Item($"more-{i}", LauncherListBuilder.KindTool, $"工具{i}"))
            .ToArray();
        Assert.Equal(TrayMenuPlanner.MaxItems, TrayMenuPlanner.FromLauncherItems(many, 99).Count);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(20, 20)]
    [InlineData(21, 10)]
    public void ResolveCountFallsBackToDefault(int? configured, int expected)
    {
        Assert.Equal(expected, TrayMenuPlanner.ResolveCount(configured));
    }

    [Fact]
    public void EntriesCarryKindAndIconInfo()
    {
        var entries = TrayMenuPlanner.FromLauncherItems(new[]
        {
            Item("json", LauncherListBuilder.KindTool, "JSON 格式化", glyph: "🧮"),
            Item("site", LauncherListBuilder.KindWeb, "资讯站", glyph: "🌐"),
            Item("editor", LauncherListBuilder.KindApp, "编辑器", iconUrl: "/__host__/app-icon/editor"),
            Item("idx-1", LauncherListBuilder.KindProgram, "微信", iconUrl: "/__host__/app-icon/idx-1"),
        }, TrayMenuPlanner.DefaultItems);

        Assert.Equal(
            new[] { "小工具", "在线工具", "本机程序", "本机程序" },
            entries.Select(e => e.KindLabel).ToArray());
        Assert.Equal("🧮", entries[0].Glyph);
        Assert.Equal("🌐", entries[1].Glyph);
        Assert.Equal("/__host__/app-icon/editor", entries[2].IconUrl);
        Assert.Equal("/__host__/app-icon/idx-1", entries[3].IconUrl);
    }

    [Fact]
    public void EmptyListProducesEmptyMenu()
    {
        Assert.Empty(TrayMenuPlanner.FromLauncherItems(
            Array.Empty<LauncherItemView>(), TrayMenuPlanner.DefaultItems));
    }

    [Fact]
    public void PlacementAnchorsAboveLeftOfCursor()
    {
        var (left, top) = TrayMenuPlacement.Compute(
            cursorX: 2500, cursorY: 1400, width: 288, height: 300,
            new WindowRect(0, 0, 2560, 1440));

        Assert.Equal(2232, left);
        Assert.Equal(1090, top);
    }

    [Fact]
    public void PlacementClampsInsideWorkArea()
    {
        var (left, top) = TrayMenuPlacement.Compute(
            cursorX: 5, cursorY: 5, width: 288, height: 300,
            new WindowRect(0, 0, 1920, 1080));

        Assert.Equal(10, left);
        Assert.Equal(15, top);   // 上方放不下 → 翻到光标下方
    }

    [Fact]
    public void PlacementHonoursSecondaryMonitorOrigin()
    {
        var (left, top) = TrayMenuPlacement.Compute(
            cursorX: -100, cursorY: 600, width: 288, height: 300,
            new WindowRect(-1920, 0, 1920, 1080));

        Assert.Equal(-368, left);
        Assert.Equal(290, top);
    }
}
