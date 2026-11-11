using QTools.Host.Shell;
using Xunit;

namespace QTools.Tests;

/// <summary>主窗口尺寸记忆：有效值原样恢复，缺失/越界按维度回退默认，异常尺寸不落盘。</summary>
public sealed class MainWindowSizeTests
{
    [Fact]
    public void MissingSavedSizeUsesDefaults()
    {
        Assert.Equal((340, 720), MainWindowSize.Resolve(null, null));
    }

    [Fact]
    public void ValidSavedSizeIsRestored()
    {
        Assert.Equal((1280, 900), MainWindowSize.Resolve(1280, 900));
    }

    [Fact]
    public void OutOfRangeDimensionFallsBackIndependently()
    {
        Assert.Equal((340, 900), MainWindowSize.Resolve(100, 900));
        Assert.Equal((1280, 720), MainWindowSize.Resolve(1280, 99999));
    }

    [Theory]
    [InlineData(340, 720, true)]
    [InlineData(280, 480, true)]
    [InlineData(279, 720, false)]
    [InlineData(340, 479, false)]
    [InlineData(8001, 720, false)]
    public void ShouldPersistOnlyAcceptsUsableSizes(int width, int height, bool expected)
    {
        Assert.Equal(expected, MainWindowSize.ShouldPersist(width, height));
    }
}
