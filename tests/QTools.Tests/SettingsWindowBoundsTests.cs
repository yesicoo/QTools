using QTools.Host.Shell;
using Xunit;

namespace QTools.Tests;

public sealed class SettingsWindowBoundsTests
{
    [Fact]
    public void ResolveSizeFallsBackByDimensionWhenMissingOrOutOfRange()
    {
        Assert.Equal(
            (SettingsWindowBounds.DefaultWidth, SettingsWindowBounds.DefaultHeight),
            SettingsWindowBounds.ResolveSize(null, null));

        Assert.Equal(
            (SettingsWindowBounds.DefaultWidth, 900),
            SettingsWindowBounds.ResolveSize(SettingsWindowBounds.MinWidth - 1, 900));

        Assert.Equal(
            (640, SettingsWindowBounds.DefaultHeight),
            SettingsWindowBounds.ResolveSize(640, SettingsWindowBounds.MaxHeight + 1));
    }

    [Fact]
    public void ResolveSizeKeepsValidSavedSize()
    {
        Assert.Equal((720, 900), SettingsWindowBounds.ResolveSize(720, 900));
    }

    [Theory]
    [InlineData(559, 520, false)]
    [InlineData(560, 520, true)]
    [InlineData(8000, 6000, true)]
    [InlineData(8001, 6000, false)]
    public void ShouldPersistOnlyAcceptsNormalRange(int width, int height, bool expected)
    {
        Assert.Equal(expected, SettingsWindowBounds.ShouldPersist(width, height));
    }
}
