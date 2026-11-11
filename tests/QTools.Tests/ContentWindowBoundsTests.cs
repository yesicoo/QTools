using QTools.Host.Shell;
using Xunit;

namespace QTools.Tests;

/// <summary>内容窗口尺寸记忆：有效值原样恢复，缺失/越界按维度回退默认 920×680。</summary>
public sealed class ContentWindowBoundsTests
{
    [Fact]
    public void MissingSavedSizeUsesDefaults()
    {
        Assert.Equal((920, 680), ContentWindowBounds.ResolveSize(null, null));
    }

    [Fact]
    public void ValidSavedSizeIsRestored()
    {
        Assert.Equal((1100, 760), ContentWindowBounds.ResolveSize(1100, 760));
    }

    [Fact]
    public void OutOfRangeDimensionFallsBackIndependently()
    {
        Assert.Equal((920, 760), ContentWindowBounds.ResolveSize(100, 760));
        Assert.Equal((1100, 680), ContentWindowBounds.ResolveSize(1100, 99999));
    }

    [Theory]
    [InlineData(920, 680, true)]
    [InlineData(560, 420, true)]
    [InlineData(559, 680, false)]
    [InlineData(920, 419, false)]
    [InlineData(8001, 680, false)]
    public void ShouldPersistOnlyAcceptsUsableSizes(int width, int height, bool expected)
    {
        Assert.Equal(expected, ContentWindowBounds.ShouldPersist(width, height));
    }
}
