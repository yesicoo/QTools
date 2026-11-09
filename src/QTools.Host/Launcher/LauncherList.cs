using QTools.Host.Tools;

namespace QTools.Host.Launcher;

/// <summary>
/// 统一启动列表项：小工具（tool）、手动启动项（app）、自动索引程序（program）混排。
/// Kind 决定前端点击行为：tool → 内容窗口；app/program → 直接启动。
/// </summary>
public sealed record LauncherItemView(
    string Id,
    string Kind,
    string Name,
    string? Version,
    string? IconUrl,
    string SearchKey,
    bool IsValid,
    bool TargetExists,
    int UseCount,
    string? Category,
    string? Glyph);

/// <summary>索引状态（列表附带，前端展示「索引中…」与上次完成时间）。</summary>
public sealed record LauncherIndexStatus(bool Indexing, string? LastIndexedUtc, int ProgramCount);

/// <summary>manager.launcherList 结果。</summary>
public sealed record LauncherListView(IReadOnlyList<LauncherItemView> Items, LauncherIndexStatus Index);

/// <summary>统一列表构建（纯函数，便于单测）：合并三类来源、去重、按使用频次排序。</summary>
public static class LauncherListBuilder
{
    public const string KindTool = "tool";
    public const string KindApp = "app";
    public const string KindProgram = "program";

    public static LauncherListView Build(
        ToolRegistry tools,
        AppRegistry apps,
        ProgramIndex? index,
        UsageStore? usage,
        string iconCacheDirectory)
    {
        var rows = new List<(LauncherItemView Item, string LastUsed)>();

        foreach (var tool in tools.GetAll())
        {
            var used = usage?.Get(tool.Id);
            rows.Add((new LauncherItemView(
                tool.Id,
                KindTool,
                tool.Name,
                tool.Manifest.Version,
                null,
                Text.PinyinInitials.BuildSearchKey(tool.Name, tool.Id, tool.Manifest.Description),
                tool.IsValid,
                true,
                used?.Count ?? 0,
                null,
                null), used?.LastUsedUtc ?? ""));
        }

        var manualTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps.GetAll())
        {
            if (!string.IsNullOrWhiteSpace(app.Entry.Target))
            {
                manualTargets.Add(app.Entry.Target.Trim());
            }

            var used = usage?.Get(app.Id);
            rows.Add((new LauncherItemView(
                app.Id,
                KindApp,
                app.Name,
                null,
                WindowsAppIcons.EnsureIcon(app) ? $"/__host__/app-icon/{Uri.EscapeDataString(app.Id)}" : null,
                Text.PinyinInitials.BuildSearchKey(app.Name, app.Id, app.Entry.Category, app.Entry.Target),
                app.IsValid,
                app.TargetExists,
                used?.Count ?? 0,
                app.Entry.Category,
                app.Entry.Glyph), used?.LastUsedUtc ?? ""));
        }

        if (index is not null)
        {
            foreach (var entry in index.Entries)
            {
                // 手动启动项优先：同一目标已被手动添加时不再重复展示索引项
                if (manualTargets.Contains(entry.Target))
                {
                    continue;
                }

                var iconPath = Path.Combine(iconCacheDirectory, entry.Id + ".png");
                var used = usage?.Get(entry.Id);
                rows.Add((new LauncherItemView(
                    entry.Id,
                    KindProgram,
                    entry.Name,
                    null,
                    File.Exists(iconPath) ? $"/__host__/app-icon/{Uri.EscapeDataString(entry.Id)}" : null,
                    Text.PinyinInitials.BuildSearchKey(entry.Name, entry.Id, entry.Target),
                    true,
                    File.Exists(entry.Target) || Directory.Exists(entry.Target),
                    used?.Count ?? 0,
                    entry.Category,
                    null), used?.LastUsedUtc ?? ""));
            }
        }

        // 默认展示顺序：使用次数降序 → 最近使用降序 → 名称升序（搜索时前端自行过滤）
        var items = rows
            .OrderByDescending(r => r.Item.UseCount)
            .ThenByDescending(r => r.LastUsed, StringComparer.Ordinal)
            .ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Item)
            .ToArray();

        var status = index is null
            ? new LauncherIndexStatus(false, null, 0)
            : new LauncherIndexStatus(index.IsIndexing, index.LastIndexedUtc, index.Count);

        return new LauncherListView(items, status);
    }
}