namespace QTools.Host.Shell;

/// <summary>
/// 独立工具窗口的尺寸记忆（纯函数）：宽度与高度按 DIP 存储，缺失或越界按维度回退
/// 工具 manifest 声明的窗口尺寸；位置由 ShellHost 按物理像素单独校验。
/// </summary>
public static class ToolWindowBounds
{
    public const int MinWidth = 280;
    public const int MinHeight = 220;
    public const int MaxWidth = 8000;
    public const int MaxHeight = 6000;

    public static (int Width, int Height) ResolveSize(
        int defaultWidth, int defaultHeight, int? savedWidth, int? savedHeight) => (
        savedWidth is >= MinWidth and <= MaxWidth
            ? savedWidth.Value
            : Math.Clamp(defaultWidth, MinWidth, MaxWidth),
        savedHeight is >= MinHeight and <= MaxHeight
            ? savedHeight.Value
            : Math.Clamp(defaultHeight, MinHeight, MaxHeight));

    public static bool ShouldPersist(int width, int height) =>
        width is >= MinWidth and <= MaxWidth && height is >= MinHeight and <= MaxHeight;
}
