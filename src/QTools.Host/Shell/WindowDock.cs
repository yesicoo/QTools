namespace QTools.Host.Shell;

/// <summary>屏幕坐标系下的窗口矩形（像素）。</summary>
public readonly record struct WindowRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// 内容窗口初始定位（纯函数，便于单测）：打开时优先出现在主窗口右侧，
/// 放不下再贴左侧；垂直方向夹在工作区内。
/// </summary>
public static class WindowDock
{
    /// <summary>初始定位：优先主窗口右侧，超出工作区则改贴左侧，垂直方向夹在工作区内。</summary>
    public static (int Left, int Top) DockPosition(
        WindowRect main, int contentWidth, int contentHeight, WindowRect? workArea)
    {
        var left = main.Right;
        var top = main.Top;

        if (workArea is { } area)
        {
            if (left + contentWidth > area.Right)
            {
                left = main.Left - contentWidth;
                if (left < area.Left)
                {
                    left = Math.Max(area.Left, area.Right - contentWidth);
                }
            }

            if (top + contentHeight > area.Bottom)
            {
                top = area.Bottom - contentHeight;
            }

            if (top < area.Top)
            {
                top = area.Top;
            }
        }

        return (left, top);
    }
}
