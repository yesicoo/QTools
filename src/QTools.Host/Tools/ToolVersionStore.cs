using System.Text.Json;
using System.Text.RegularExpressions;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Serialization;

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
///       ├── 1.0.0/{manifest.json,index.html,assets/…}
///       └── 1.1.0/{manifest.json,index.html,assets/…}
/// </code>
/// </para>
/// </summary>
public sealed partial class ToolVersionStore
{
    /// <summary>版本目录名（点号前缀 → 静态服务拒绝暴露）。</summary>
    public const string VersionsDirectoryName = ".versions";

    private const string UserDataDirectoryName = "files";
    private const string NoteFileName = "note.txt";

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
    public bool Snapshot(string toolDirectory, string version, string? note = null, bool replaceExisting = false)
    {
        if (!IsValidVersion(version))
        {
            throw new BridgeException("VERSION_INVALID", $"非法版本号 '{version}'");
        }

        var target = VersionDirectory(toolDirectory, version);
        if (Directory.Exists(target) && !replaceExisting)
        {
            return false;
        }

        var temporary = Path.Combine(VersionsRoot(toolDirectory), ".snapshot-" + Guid.NewGuid().ToString("N")[..12]);
        var backup = target + ".old-" + Guid.NewGuid().ToString("N")[..12];
        var replaced = false;
        Directory.CreateDirectory(VersionsRoot(toolDirectory));
        try
        {
            CopyVersionTree(toolDirectory, temporary, excludeNote: false);
            if (!string.IsNullOrWhiteSpace(note))
            {
                File.WriteAllText(Path.Combine(temporary, NoteFileName), note);
            }

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                replaced = true;
            }

            Directory.Move(temporary, target);
            if (replaced)
            {
                TryDeleteDirectory(backup);
            }
        }
        catch
        {
            TryDeleteDirectory(temporary);
            if (replaced && !Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }

        Log.Info($"已创建工具版本快照 {version}（{Path.GetFileName(toolDirectory)}）");
        return true;
    }

    /// <summary>
    /// 把指定版本设为激活版本：完整替换代码与静态资源，保留 files/ 和 .versions/。
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

        ValidateSnapshot(toolDirectory, source, targetVersion);
        if (!string.Equals(currentVersion, targetVersion, StringComparison.Ordinal))
        {
            Snapshot(toolDirectory, currentVersion, replaceExisting: true);
        }

        var temporary = Path.Combine(VersionsRoot(toolDirectory), ".activate-" + Guid.NewGuid().ToString("N")[..12]);
        var oldRoot = Path.Combine(temporary, "old");
        var newRoot = Path.Combine(temporary, "new");
        var movedOld = new List<(string Source, string Backup)>();
        var movedNew = new List<string>();
        var preserveTemporary = false;
        Directory.CreateDirectory(temporary);
        try
        {
            CopyVersionTree(source, newRoot, excludeNote: true);
            Directory.CreateDirectory(oldRoot);
            foreach (var entry in EnumerateVersionEntries(toolDirectory))
            {
                var backup = Path.Combine(oldRoot, Path.GetFileName(entry));
                MovePath(entry, backup);
                movedOld.Add((entry, backup));
            }

            foreach (var entry in EnumerateVersionEntries(newRoot))
            {
                var target = Path.Combine(toolDirectory, Path.GetFileName(entry));
                MovePath(entry, target);
                movedNew.Add(target);
            }
        }
        catch (Exception ex)
        {
            Exception? rollbackError = null;
            foreach (var target in movedNew.AsEnumerable().Reverse())
            {
                if (!TryDeletePath(target)) { rollbackError ??= new IOException($"无法删除新版本文件 {target}"); }
            }

            foreach (var (entry, backup) in movedOld.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(backup) || File.Exists(backup))
                    {
                        MovePath(backup, entry);
                    }
                    else
                    {
                        rollbackError ??= new FileNotFoundException("版本回滚备份缺失", backup);
                    }
                }
                catch (Exception restoreError)
                {
                    rollbackError ??= restoreError;
                }
            }

            if (rollbackError is not null)
            {
                preserveTemporary = true;
                throw new AggregateException("版本切换失败且自动回滚未完成，备份已保留", ex, rollbackError);
            }

            throw;
        }
        finally
        {
            if (!preserveTemporary) { TryDeleteDirectory(temporary); }
        }

        Log.Info($"已切换工具 {Path.GetFileName(toolDirectory)} 到版本 {targetVersion}");
        return true;
    }

    private static void ValidateSnapshot(string toolDirectory, string snapshot, string version)
    {
        var manifestPath = Path.Combine(snapshot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new BridgeException("VERSION_SWITCH_FAILED", "历史版本缺少 manifest.json，无法切换");
        }

        ToolManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), AppJsonContext.Default.ToolManifest);
        }
        catch (JsonException)
        {
            throw new BridgeException("VERSION_SWITCH_FAILED", "历史版本 manifest.json 无法解析");
        }

        var errors = manifest is null
            ? new[] { "manifest.json 无法解析" }
            : ToolManifestValidator.Validate(manifest, Path.GetFileName(toolDirectory));
        if (errors.Count > 0)
        {
            throw new BridgeException("VERSION_SWITCH_FAILED", $"历史版本校验失败：{errors[0]}");
        }

        if (manifest!.Version != version || !File.Exists(Path.Combine(snapshot, manifest.Entry.Replace('/', Path.DirectorySeparatorChar))))
        {
            throw new BridgeException("VERSION_SWITCH_FAILED", "历史版本号或入口文件与快照不一致");
        }
    }

    private static void CopyVersionTree(string source, string destination, bool excludeNote)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in EnumerateVersionEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target);
            }
            else if (!excludeNote || !string.Equals(Path.GetFileName(entry), NoteFileName, StringComparison.Ordinal))
            {
                File.Copy(entry, target);
            }
        }
    }

    private static IEnumerable<string> EnumerateVersionEntries(string directory) =>
        Directory.EnumerateFileSystemEntries(directory)
            .Where(entry =>
            {
                var name = Path.GetFileName(entry);
                return !name.StartsWith('.') &&
                    !string.Equals(name, UserDataDirectoryName, StringComparison.OrdinalIgnoreCase);
            });

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void MovePath(string source, string destination)
    {
        if (Directory.Exists(source)) { Directory.Move(source, destination); }
        else { File.Move(source, destination); }
    }

    private static bool TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            else if (File.Exists(path)) { File.Delete(path); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
