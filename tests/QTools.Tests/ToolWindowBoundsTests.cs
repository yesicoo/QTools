using QTools.Host.Shell;
using Xunit;

namespace QTools.Tests;

/// <summary>独立工具窗口尺寸记忆（纯函数）：宽度/高度按 DIP 存储，越界按维度回退 manifest 默认尺寸。</summary>
public sealed class ToolWindowBoundsTests
{
    [Fact]
    public void ResolveSizeFallsBackToManifestDefaults()
    {
        Assert.Equal((720, 760), ToolWindowBounds.ResolveSize(720, 760, null, null));
    }

    [Fact]
    public void ResolveSizeKeepsValidSavedSize()
    {
        Assert.Equal((1024, 700), ToolWindowBounds.ResolveSize(720, 760, 1024, 700));
    }

    [Fact]
    public void ResolveSizeRejectsOutOfRangeByDimension()
    {
        Assert.Equal((720, 700), ToolWindowBounds.ResolveSize(720, 760, 100, 700));
        Assert.Equal((1024, 760), ToolWindowBounds.ResolveSize(720, 760, 1024, 99999));
    }

    [Theory]
    [InlineData(720, 760, true)]
    [InlineData(280, 220, true)]
    [InlineData(279, 760, false)]
    [InlineData(720, 219, false)]
    [InlineData(8001, 760, false)]
    public void ShouldPersistChecksRange(int width, int height, bool expected)
    {
        Assert.Equal(expected, ToolWindowBounds.ShouldPersist(width, height));
    }
}
