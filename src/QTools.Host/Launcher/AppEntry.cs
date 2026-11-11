using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QTools.Host.Launcher;

/// <summary>一个本机程序启动项（apps/{id}/app.json）。</summary>
public sealed record AppEntry(
    string Id,
    string Name,
    string Target,
    string? Arguments,
    string? WorkingDirectory,
    string? Category,
    string? Glyph,
    string CreatedAt);

/// <summary>已加载的启动项 + 校验状态。</summary>
public sealed class LoadedApp
{
    public required AppEntry Entry { get; init; }

    public required string Directory { get; init; }

    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public string Id => Entry.Id;

    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Id : Entry.Name;

    /// <summary>目标是否存在（启动前预检，给出友好错误）。</summary>
    public bool TargetExists =>
        System.IO.File.Exists(Entry.Target) || System.IO.Directory.Exists(Entry.Target);
}

/// <summary>manager.addApp 的载荷。</summary>
public sealed record AddAppPayload(
    string Name,
    string Target,
    string? Arguments,
    string? WorkingDirectory,
    string? Category,
    string? Glyph);

/// <summary>manager.listApps 的列表项。IconUrl 为真实程序图标（提取失败为 null，前端回退字母头像）；
/// SearchKey 含拼音首字母与全拼，供前端子串匹配。</summary>
public sealed record AppListItemView(
    string Id,
    string Name,
    string Target,
    string? Category,
    string? Glyph,
    bool IsValid,
    bool TargetExists,
    string? Arguments,
    string? IconUrl = null,
    string SearchKey = "");

public sealed record AppListView(IReadOnlyList<AppListItemView> Apps);

/// <summary>manager.discoverPrograms 的候选项。</summary>
public sealed record DiscoveredProgramView(string Name, string Target, string Source, string? Category);

public sealed record DiscoveredProgramListView(IReadOnlyList<DiscoveredProgramView> Programs);

/// <summary>manager.launchApp 的结果。</summary>
public sealed record LaunchResultView(string AppId, string AppName, bool Started, int? ProcessId, string? Message);

/// <summary>manager.pickProgram 的结果（未选择时为 null）。</summary>
public sealed record PickedFileView(string? Path);

/// <summary>启动项静态校验规则（纯函数，便于单测）。</summary>
public static partial class AppEntryValidator
{
    /// <summary>与工具 ID 同一套规则：小写字母/数字/短横线，1~63，首尾非短横线。</summary>
    public const string AppIdRegexPattern = "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$";

    [GeneratedRegex(AppIdRegexPattern)]
    public static partial Regex AppIdPattern();

    public static IReadOnlyList<string> Validate(AppEntry entry, string expectedDirectoryName)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(entry.Id) || !AppIdPattern().IsMatch(entry.Id))
        {
            errors.Add("id 必须匹配 ^[a-z0-9][a-z0-9-]{0,62}$（小写字母/数字/短横线）");
        }
        else if (!string.Equals(entry.Id, expectedDirectoryName, StringComparison.Ordinal))
        {
            errors.Add($"id({entry.Id}) 必须与目录名({expectedDirectoryName}) 一致");
        }

        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Trim().Length > 60)
        {
            errors.Add("name 必填且不超过 60 字符");
        }

        if (string.IsNullOrWhiteSpace(entry.Target))
        {
            errors.Add("target 必填（可执行文件 / .app / .lnk / .desktop 的路径）");
        }
        else
        {
            var target = entry.Target.Trim();
            if (target.Contains('\0') || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                errors.Add("target 包含非法字符");
            }
        }

        if (entry.Arguments is { Length: > 2000 })
        {
            errors.Add("arguments 不超过 2000 字符");
        }

        if (entry.Category is { Length: > 30 })
        {
            errors.Add("category 不超过 30 字符");
        }

        if (entry.Glyph is { Length: > 4 })
        {
            errors.Add("glyph 不超过 4 字符");
        }

        return errors;
    }
}
