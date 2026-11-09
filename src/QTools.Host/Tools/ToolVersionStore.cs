using System.Text.Json;
using System.Text.RegularExpressions;
using QTools.Host.Bridge;
using QTools.Host.Logging;

namespace QTools.Host.Tools;

/// <summary>一个工具的一个历史版本。</summary>
public sealed record ToolVersion(
    string Version,
    string CreatedAt,
    long SizeBytes,
    bool IsActive,
    string? Note);

/// <summary>
/// 工具版本存储：每个工具目录下的 <c>.versions/{version}/</c> 快照 + 根目录即"当前运行版本"。
/// <para>
/// 布局（根目录始终是激活版本，静态服务路径不变；<c>.versions/</c> 是点号前缀目录，
/// 已被静态服务明确拒绝，不会泄露）：
/// <code>
/// tools/{id}/
///   ├── manifest.json / index.html     ← 激活版本（= 当前运行的文件）
///   ├── files/…                        ← 用户数据，跨版本保留
///   └── .versions/
///       ├── 1.0.0/{manifest.json,index.html}
///       └── 1.1.0/{manifest.json,index.html}
/// </code>
/// </para>
/// </summary>
public sealed partial class ToolVersionStore
{
    /// <summary>版本目录名（点号前缀 → 静态服务拒绝暴露）。</summary>
    public const string VersionsDirectoryName = ".versions";

    /// <summary>纳入版本管理的文件（不含 files/ 等用户数据目录）。</summary>
    private static readonly string[] VersionedEntries = ["manifest.json", "index.html"];

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    public static bool IsValidVersion(string? version)
        => !string.IsNullOrWhiteSpace(version) && VersionPattern().IsMatch(version);

    public static string VersionsRoot(string toolDirectory) => Path.Combine(toolDirectory, VersionsDirectoryName);

    public static string VersionDirectory(string toolDirectory, string version)
        => Path.Combine(VersionsRoot(toolDirectory), version);

    /// <summary>列出全部版本（新的在前），标注哪个是激活版本。</summary>
    public IReadOnlyList<ToolVersion> List(string toolDirectory, string activeVersion)
    {
        var root = VersionsRoot(toolDirectory);
        if (!Directory.Exists(root))
        {
            return Array.Empty<ToolVersion>();
        }

        var result = new List<ToolVersion>();
        foreach (var directory in Directory.GetDirectories(root))
        {
            var version = Path.GetFileName(directory);
            if (!IsValidVersion(version))
            {
                continue;
            }

            var createdAt = Directory.GetCreationTimeUtc(directory);
            var sizeBytes = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (File.Exists(manifestPath))
            {
                createdAt = File.GetCreationTimeUtc(manifestPath);
            }

            result.Add(new ToolVersion(
                version,
                new DateTimeOffset(createdAt).ToString("o"),
                sizeBytes,
                string.Equals(version, activeVersion, StringComparison.Ordinal),
                null));
        }

        return result
            .OrderByDescending(v => ParseVersion(v.Version))
            .ToArray();
    }

    /// <summary>
    /// 把工具根目录的当前文件快照成一个版本。
    /// 已存在同版本号时返回 false（不覆盖，避免回滚后重复快照相互踩踏）。
    /// </summary>
    public bool Snapshot(string toolDirectory, string version, string? note = null)
    {
        if (!IsValidVersion(version))
        {
            throw new BridgeException("VERSION_INVALID", $"非法版本号 '{version}'");
        }

        var target = VersionDirectory(toolDirectory, version);
        if (Directory.Exists(target))
        {
            return false;
        }

        Directory.CreateDirectory(target);
        foreach (var entry in VersionedEntries)
        {
            var source = Path.Combine(toolDirectory, entry);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(target, entry), overwrite: false);
            }
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            File.WriteAllText(Path.Combine(target, "note.txt"), note);
        }

        Log.Info($"已创建工具版本快照 {version}（{Path.GetFileName(toolDirectory)}）");
        return true;
    }

    /// <summary>
    /// 把指定版本设为激活版本：用快照覆盖根目录文件。
    /// 切换前会先把当前根目录内容重新快照回它自己的版本号（保证历史不丢）。
    /// </summary>
    public bool Activate(string toolDirectory, string currentVersion, string targetVersion)
    {
        if (!IsValidVersion(targetVersion))
        {
            throw new BridgeException("VERSION_INVALID", $"非法版本号 '{targetVersion}'");
        }

        var source = VersionDirectory(toolDirectory, targetVersion);
        if (!Directory.Exists(source))
        {
            throw new BridgeException("VERSION_NOT_FOUND", $"版本 {targetVersion} 不存在");
        }

        // 先把当前状态存回它自己的版本号（若根目录与快照已有差异）
        if (!string.Equals(currentVersion, targetVersion, StringComparison.Ordinal))
        {
            Snapshot(toolDirectory, currentVersion);
        }

        foreach (var entry in VersionedEntries)
        {
            var from = Path.Combine(source, entry);
            var to = Path.Combine(toolDirectory, entry);
            if (File.Exists(from))
            {
                var temp = to + ".switch-tmp";
                File.Copy(from, temp, overwrite: true);
                File.Move(temp, to, overwrite: true);
            }
        }

        Log.Info($"已切换工具 {Path.GetFileName(toolDirectory)} 到版本 {targetVersion}");
        return true;
    }

    /// <summary>删除一个历史版本（不能删激活版本）。</summary>
    public bool Delete(string toolDirectory, string version, string activeVersion)
    {
        if (string.Equals(version, activeVersion, StringComparison.Ordinal))
        {
            throw new BridgeException("VERSION_ACTIVE", "不能删除正在运行的版本，请先切换到其他版本");
        }

        if (!IsValidVersion(version))
        {
            throw new BridgeException("VERSION_INVALID", $"非法版本号 '{version}'");
        }

        var target = VersionDirectory(toolDirectory, version);
        if (!Directory.Exists(target))
        {
            return false;
        }

        Directory.Delete(target, recursive: true);
        Log.Info($"已删除工具 {Path.GetFileName(toolDirectory)} 的版本 {version}");
        return true;
    }

    /// <summary>版本号比较（用于排序）：按 major.minor.patch 数字比较。</summary>
    internal static (int Major, int Minor, int Patch, string Suffix) ParseVersion(string version)
    {
        var core = version.Split('-', 2)[0].Split('.');
        var major = core.Length > 0 && int.TryParse(core[0], out var ma) ? ma : 0;
        var minor = core.Length > 1 && int.TryParse(core[1], out var mi) ? mi : 0;
        var patch = core.Length > 2 && int.TryParse(core[2], out var pa) ? pa : 0;
        var suffix = version.Contains('-') ? version[(version.IndexOf('-') + 1)..] : string.Empty;
        return (major, minor, patch, suffix);
    }

    /// <summary>在现有版本基础上递增，得到下一个未占用的版本号。</summary>
    public static string NextVersion(IReadOnlyList<ToolVersion> existing, VersionBump bump)
    {
        var latest = existing
            .Select(v => ParseVersion(v.Version))
            .OrderBy(p => p.Major)
            .ThenBy(p => p.Minor)
            .ThenBy(p => p.Patch)
            .ThenBy(p => p.Suffix, StringComparer.Ordinal)
            .LastOrDefault();

        var (major, minor, patch, _) = latest == default ? (1, 0, 0, string.Empty) : latest;

        var candidate = bump switch
        {
            VersionBump.Major => $"{major + 1}.0.0",
            VersionBump.Minor => $"{major}.{minor + 1}.0",
            _ => $"{major}.{minor}.{patch + 1}",
        };

        // 极端情况下（快照被手工删过）撞号：继续递增补丁位
        var guard = 0;
        while (existing.Any(v => string.Equals(v.Version, candidate, StringComparison.Ordinal)) && guard++ < 1000)
        {
            var p = ParseVersion(candidate);
            candidate = $"{p.Major}.{p.Minor}.{p.Patch + 1}";
        }

        return candidate;
    }
}

/// <summary>版本递增粒度。</summary>
public enum VersionBump
{
    /// <summary>补丁：1.0.0 → 1.0.1（默认，修 bug / 小调整）</summary>
    Patch,

    /// <summary>次版本：1.0.0 → 1.1.0（新功能）</summary>
    Minor,

    /// <summary>主版本：1.0.0 → 2.0.0（不兼容的大改）</summary>
    Major,
}
