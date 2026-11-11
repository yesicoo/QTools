using QTools.Host.Launcher;

namespace QTools.Host.Shell;

/// <summary>托盘菜单条目（名称 + 类型 + 图标，供 HTML 弹层渲染；Kind/Id 决定点击后的打开方式）。</summary>
public sealed record TrayEntry(
    string Kind,
    string Id,
    string Name,
    string KindLabel,
    string? Glyph,
    string? IconUrl);

/// <summary>manager.trayMenuList 结果。</summary>
public sealed record TrayMenuListView(IReadOnlyList<TrayEntry> Items);

/// <summary>
/// 托盘菜单内容规划（纯函数，便于单测）：取统一列表前五项（与界面同序），
/// 附带类型标签与图标信息；空列表由前端显示「暂无工具」。
/// </summary>
public static class TrayMenuPlanner
{
    public const int DefaultItems = 10;
    public const int MaxItems = 20;

    /// <summary>配置值（1–20）转成实际条数；缺失或越界回退默认 10。</summary>
    public static int ResolveCount(int? configured) =>
        configured is >= 1 and <= MaxItems ? configured.Value : DefaultItems;

    public static IReadOnlyList<TrayEntry> FromLauncherItems(IEnumerable<LauncherItemView> items, int count)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items
            .Take(Math.Clamp(count, 1, MaxItems))
            .Select(item => new TrayEntry(
                item.Kind,
                item.Id,
                item.Name,
                KindLabel(item.Kind),
                item.Glyph,
                item.IconUrl))
            .ToArray();
    }

    private static string KindLabel(string kind) => kind switch
    {
        LauncherListBuilder.KindTool => "小工具",
        LauncherListBuilder.KindWeb => "在线工具",
        _ => "本机程序",
    };
}

/// <summary>
/// 托盘菜单弹层定位（纯函数）：默认在光标左上方展开（贴近任务栏托盘），
/// 越界时自动翻转/夹回到当前显示器工作区内。
/// </summary>
public static class TrayMenuPlacement
{
    public static (int Left, int Top) Compute(
        int cursorX, int cursorY, int width, int height, WindowRect workArea)
    {
        const int gap = 10;

        var left = cursorX - width + gap * 2;
        var top = cursorY - height - gap;

        // 上方空间不足时改为在光标下方展开
        if (top < workArea.Top + gap)
        {
            top = cursorY + gap;
        }

        var maxLeft = workArea.Left + workArea.Width - width - gap;
        var maxTop = workArea.Top + workArea.Height - height - gap;
        left = Math.Clamp(left, workArea.Left + gap, Math.Max(workArea.Left + gap, maxLeft));
        top = Math.Clamp(top, workArea.Top + gap, Math.Max(workArea.Top + gap, maxTop));
        return (left, top);
    }
}
