namespace QTools.Host.Shell;

/// <summary>
/// 主窗口尺寸记忆（纯函数）：手动调整后的 DIP 尺寸存入 config.json，下次启动恢复；
/// 缺失或越界（旧配置、异常显示器）按维度回退默认值。
/// </summary>
public static class MainWindowSize
{
    public const int DefaultWidth = 340;
    public const int DefaultHeight = 720;
    public const int MinWidth = 280;
    public const int MinHeight = 480;
    public const int MaxWidth = 8000;
    public const int MaxHeight = 6000;

    public static (int Width, int Height) Resolve(int? savedWidth, int? savedHeight) => (
        savedWidth is >= MinWidth and <= MaxWidth ? savedWidth.Value : DefaultWidth,
        savedHeight is >= MinHeight and <= MaxHeight ? savedHeight.Value : DefaultHeight);

    public static bool ShouldPersist(int width, int height) =>
        width is >= MinWidth and <= MaxWidth && height is >= MinHeight and <= MaxHeight;
}
