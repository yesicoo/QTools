using QTools.Host.Shell;
using Xunit;

namespace QTools.Tests;

/// <summary>内容窗口初始定位（纯函数）：打开时优先贴主窗口右侧、放不下贴左侧、垂直夹入工作区。</summary>
public sealed class WindowDockTests
{
    private static readonly WindowRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void DockPositionPrefersRightSideOfMain()
    {
        var main = new WindowRect(100, 100, 340, 720);
        var (left, top) = WindowDock.DockPosition(main, 480, 600, WorkArea);

        Assert.Equal(440, left);
        Assert.Equal(100, top);
    }

    [Fact]
    public void DockPositionFallsBackToLeftWhenRightOverflows()
    {
        var main = new WindowRect(1500, 100, 340, 720);
        var (left, top) = WindowDock.DockPosition(main, 480, 600, WorkArea);

        Assert.Equal(1020, left);
        Assert.Equal(100, top);
    }

    [Fact]
    public void DockPositionClampsIntoWorkArea()
    {
        var main = new WindowRect(1500, 900, 340, 720);
        var (left, top) = WindowDock.DockPosition(main, 480, 600, WorkArea);
        Assert.Equal(1020, left);
        Assert.Equal(440, top);

        var negative = new WindowRect(-100, -50, 340, 720);
        var (left2, top2) = WindowDock.DockPosition(negative, 480, 600, WorkArea);
        Assert.Equal(240, left2);
        Assert.Equal(0, top2);
    }

    [Fact]
    public void DockPositionWithoutWorkAreaUsesRawRightEdge()
    {
        var main = new WindowRect(100, 100, 340, 720);
        var (left, top) = WindowDock.DockPosition(main, 480, 600, null);

        Assert.Equal(440, left);
        Assert.Equal(100, top);
    }
}

