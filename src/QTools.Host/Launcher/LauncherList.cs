using QTools.Host.Tools;

namespace QTools.Host.Launcher;

/// <summary>
/// 统一启动列表项：小工具（tool）、手动启动项（app）、自动索引程序（program）、在线工具（web）混排。
/// Kind 决定前端点击行为：tool → 内容窗口；app/program → 直接启动；web → 独立窗口在线打开。
/// Target 供右键菜单「属性/详情」与「打开所在位置」使用：app/program → 本机目标路径，web → 网址，tool → 空。
/// Description 供列表悬停提示：小工具取 manifest.description，在线工具取 web.json 的 description。
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
    string? Glyph,
    string? Target = null,
    string? Description = null);

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
    public const string KindWeb = "web";

    public static LauncherListView Build(
        ToolRegistry tools,
        AppRegistry apps,
        ProgramIndex? index,
        UsageStore? usage,
        string iconCacheDirectory,
        WebToolRegistry? webTools = null,
        HiddenProgramStore? hiddenPrograms = null)
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
                tool.Manifest.Category,
                tool.Manifest.Glyph,
                null,
                tool.Manifest.Description), used?.LastUsedUtc ?? ""));
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
                app.Entry.Glyph,
                app.Entry.Target,
                null), used?.LastUsedUtc ?? ""));
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

                // 用户右键隐藏过的索引程序不再出现在列表里（记录持久化，跨重新索引保留）
                if (hiddenPrograms is not null && hiddenPrograms.IsHidden(entry.Id))
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
                    null,
                    entry.Target,
                    null), used?.LastUsedUtc ?? ""));
            }
        }

        if (webTools is not null)
        {
            foreach (var web in webTools.GetAll())
            {
                var used = usage?.Get(web.Id);
                rows.Add((new LauncherItemView(
                    web.Id,
                    KindWeb,
                    web.Name,
                    null,
                    null,
                    Text.PinyinInitials.BuildSearchKey(
                        web.Name, web.Id, web.Entry.Category, web.Entry.Url, web.Entry.Description),
                    web.IsValid,
                    true,
                    used?.Count ?? 0,
                    web.Entry.Category,
                    web.Entry.Glyph,
                    web.Entry.Url,
                    web.Entry.Description), used?.LastUsedUtc ?? ""));
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