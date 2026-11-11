namespace QTools.Host.Shell;

/// <summary>
/// 内容窗口（打开工具 / AI 创建 / 导入）的尺寸记忆（纯函数）：宽度与高度按 DIP 存储，
/// 缺失或越界按维度回退默认 920×680；位置由 ShellHost 按物理像素单独校验。
/// </summary>
public static class ContentWindowBounds
{
    public const int DefaultWidth = 920;
    public const int DefaultHeight = 680;
    public const int MinWidth = 560;
    public const int MinHeight = 420;
    public const int MaxWidth = 8000;
    public const int MaxHeight = 6000;

    public static (int Width, int Height) ResolveSize(int? savedWidth, int? savedHeight) => (
        savedWidth is >= MinWidth and <= MaxWidth ? savedWidth.Value : DefaultWidth,
        savedHeight is >= MinHeight and <= MaxHeight ? savedHeight.Value : DefaultHeight);

    public static bool ShouldPersist(int width, int height) =>
        width is >= MinWidth and <= MaxWidth && height is >= MinHeight and <= MaxHeight;
}
