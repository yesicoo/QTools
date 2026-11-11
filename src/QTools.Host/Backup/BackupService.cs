using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Serialization;

namespace QTools.Host.Backup;

/// <summary>备份包内的清单文件（backup.json）。</summary>
public sealed record BackupManifest(int SchemaVersion, string CreatedAtUtc, string AppVersion, string? Note);

/// <summary>备份列表项。</summary>
public sealed record BackupEntryView(string Name, string CreatedAtUtc, long SizeBytes, string AppVersion, string? Note);

/// <summary>manager.createBackup 结果。</summary>
public sealed record BackupCreateResult(string Name, long SizeBytes, string CreatedAtUtc);

/// <summary>manager.restoreBackup / gitRestore 结果：恢复前自动快照的文件名。</summary>
public sealed record BackupRestoreResult(bool Ok, string SafetyBackupName);

/// <summary>manager.listBackups 结果。</summary>
public sealed record BackupListView(IReadOnlyList<BackupEntryView> Backups);

/// <summary>manager.getBackupConfig 结果：备份 + 壳（热键）配置视图，不含任何凭证。</summary>
public sealed record BackupConfigView(
    string? GitRemoteUrl,
    string? LastPushUtc,
    string? LastPushStatus,
    bool HotkeyEnabled,
    bool HotkeySupported,
    bool HotkeyRegistered,
    int TrayMenuItems);

/// <summary>manager.importBackupFile 结果（用户取消对话框时 name 为 null）。</summary>
public sealed record ImportBackupResult(string? Name);

/// <summary>
/// 本地备份/恢复：把用户数据（tools/、apps/、data/storage/、data/config.json）打包为 zip。
/// 绝不包含 secrets/（API Key）、webview/、logs/、native/、backups/ 自身。
/// 恢复前先对当前状态做一份安全快照；解压全程白名单校验，防 ZipSlip。
/// </summary>
public sealed partial class BackupService
{
    private const int CurrentSchemaVersion = 1;
    private const long MaxImportBytes = 1024L * 1024 * 1024;

    /// <summary>参与备份的目录（相对数据根目录）。</summary>
    private static readonly string[] PayloadDirectories =
    [
        "tools",
        "apps",
        Path.Combine("data", "storage"),
    ];

    private readonly string _root;

    public BackupService(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
    }

    public string BackupsDirectory => Path.Combine(_root, "backups");

    /// <summary>创建一个备份包，返回文件名与大小。</summary>
    public BackupCreateResult Create(string? note) => CreateFromDirectory(_root, note);

    /// <summary>把任意目录按备份范围打包（Git 恢复时先把克隆内容固化成快照包，复用同一套校验/安全快照逻辑）。</summary>
    internal BackupCreateResult CreateFromDirectory(string sourceRoot, string? note)
    {
        Directory.CreateDirectory(BackupsDirectory);

        var createdAt = DateTimeOffset.UtcNow;
        var name = UniqueName($"qtools-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var targetPath = Path.Combine(BackupsDirectory, name);
        var tempPath = targetPath + ".tmp";

        var manifest = new BackupManifest(
            CurrentSchemaVersion,
            createdAt.ToString("o"),
            AppInfo.Version,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim());

        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("backup.json", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open(), new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(JsonSerializer.Serialize(manifest, AppJsonContext.Default.BackupManifest));
                }

                foreach (var relative in EnumeratePayloadFiles(sourceRoot))
                {
                    var sourcePath = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                        using var source = File.OpenRead(sourcePath);
                        using var destination = entry.Open();
                        source.CopyTo(destination);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件被占用/无权限不中断整个备份，但必须留痕
                        Log.Warn($"备份跳过无法读取的文件 {relative}：{ex.Message}");
                    }
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        var size = new FileInfo(targetPath).Length;
        Log.Info($"已创建备份 {name}（{size} 字节）");
        return new BackupCreateResult(name, size, createdAt.ToString("o"));
    }

    /// <summary>列出全部备份（新→旧）。清单损坏的包仍列出，标记为未知版本。</summary>
    public IReadOnlyList<BackupEntryView> List()
    {
        if (!Directory.Exists(BackupsDirectory))
        {
            return [];
        }

        var entries = new List<BackupEntryView>();
        foreach (var path in Directory.EnumerateFiles(BackupsDirectory, "*.zip")
                     .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            BackupManifest? manifest = null;
            try
            {
                using var archive = ZipFile.OpenRead(path);
                var manifestEntry = archive.GetEntry("backup.json");
                if (manifestEntry is not null)
                {
                    using var reader = new StreamReader(manifestEntry.Open());
                    manifest = JsonSerializer.Deserialize(reader.ReadToEnd(), AppJsonContext.Default.BackupManifest);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                Log.Warn($"备份包清单读取失败 {info.Name}：{ex.Message}");
            }

            entries.Add(new BackupEntryView(
                info.Name,
                manifest?.CreatedAtUtc ?? info.LastWriteTimeUtc.ToString("o"),
                info.Length,
                manifest?.AppVersion ?? "未知",
                manifest?.Note));
        }

        return entries;
    }

    /// <summary>
    /// 恢复指定备份：先校验包内容（白名单 + 防穿越），再做恢复前安全快照，
    /// 然后「解压到临时目录 → 整目录替换」，避免中途失败留下半新半旧状态。
    /// </summary>
    public BackupRestoreResult Restore(string name)
    {
        var path = ResolveBackupPath(name);
        ValidateArchive(path);

        var safety = Create("自动快照：恢复前");
        var staging = Path.Combine(BackupsDirectory, ".restore-" + Guid.NewGuid().ToString("N")[..12]);

        try
        {
            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(path))
            {
                foreach (var entry in archive.Entries)
                {
                    var relative = entry.FullName.Replace('\\', '/');
                    if (relative == "backup.json" || string.IsNullOrEmpty(entry.Name) && relative.EndsWith('/'))
                    {
                        continue;
                    }

                    var destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            foreach (var directory in PayloadDirectories)
            {
                var livePath = Path.Combine(_root, directory);
                var restoredPath = Path.Combine(staging, directory);

                if (Directory.Exists(livePath))
                {
                    Directory.Delete(livePath, recursive: true);
                }

                if (Directory.Exists(restoredPath))
                {
                    Directory.Move(restoredPath, livePath);
                }
                else
                {
                    Directory.CreateDirectory(livePath);
                }
            }

            var restoredConfig = Path.Combine(staging, "data", "config.json");
            if (File.Exists(restoredConfig))
            {
                var liveConfig = Path.Combine(_root, "data", "config.json");
                Directory.CreateDirectory(Path.GetDirectoryName(liveConfig)!);
                File.Move(restoredConfig, liveConfig, overwrite: true);
            }

            Log.Info($"已从备份 {name} 恢复用户数据（恢复前快照：{safety.Name}）");
            return new BackupRestoreResult(true, safety.Name);
        }
        catch (Exception ex)
        {
            Log.Error($"恢复备份 {name} 失败", ex);
            throw new BridgeException("BACKUP_RESTORE_FAILED", $"恢复失败：{ex.Message}（恢复前快照已保存为 {safety.Name}）");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>删除一个备份包。</summary>
    public bool Delete(string name)
    {
        var path = ResolveBackupPath(name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        Log.Info($"已删除备份 {name}");
        return true;
    }

    /// <summary>把外部 zip 校验后复制进备份目录，返回登记的文件名。</summary>
    public string ImportFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new BridgeException("BACKUP_FILE_NOT_FOUND", "备份文件不存在");
        }

        var info = new FileInfo(sourcePath);
        if (info.Length > MaxImportBytes)
        {
            throw new BridgeException("BACKUP_TOO_LARGE", "备份文件不超过 1 GB");
        }

        ValidateArchive(sourcePath);
        Directory.CreateDirectory(BackupsDirectory);

        var baseName = Path.GetFileName(sourcePath);
        var name = BackupNamePattern().IsMatch(baseName)
            ? UniqueName(baseName)
            : UniqueName($"imported-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        File.Copy(sourcePath, Path.Combine(BackupsDirectory, name));
        Log.Info($"已导入外部备份 {name}（来源 {info.Length} 字节）");
        return name;
    }

    /// <summary>在系统文件管理器中打开备份目录。</summary>
    public void OpenDirectory()
    {
        Directory.CreateDirectory(BackupsDirectory);
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo { FileName = BackupsDirectory, UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                    ArgumentList = { BackupsDirectory },
                    UseShellExecute = false,
                };
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            throw new BridgeException("BACKUP_OPEN_FAILED", $"无法打开备份目录：{ex.Message}");
        }
    }

    /// <summary>枚举参与备份的全部文件（相对源根目录、'/' 分隔；含 .versions 与工具 files/）。</summary>
    internal IEnumerable<string> EnumeratePayloadFiles() => EnumeratePayloadFiles(_root);

    internal static IEnumerable<string> EnumeratePayloadFiles(string sourceRoot)
    {
        foreach (var directory in PayloadDirectories)
        {
            var full = Path.Combine(sourceRoot, directory);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                yield return Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        var config = Path.Combine(sourceRoot, "data", "config.json");
        if (File.Exists(config))
        {
            yield return "data/config.json";
        }
    }

    /// <summary>校验 zip 是否为合法 QTools 备份：必须含 backup.json，且所有条目都在白名单内（防 ZipSlip）。</summary>
    private static void ValidateArchive(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry("backup.json") is null)
            {
                throw new BridgeException("BACKUP_INVALID", "不是 QTools 备份包（缺少 backup.json 清单）");
            }

            foreach (var entry in archive.Entries)
            {
                if (!IsAllowedEntry(entry.FullName))
                {
                    throw new BridgeException("BACKUP_INVALID", $"备份包含非法路径条目：{entry.FullName}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new BridgeException("BACKUP_INVALID", $"备份包无法读取：{ex.Message}");
        }
    }

    /// <summary>条目白名单：只允许备份范围内的相对路径，拒绝绝对路径/盘符/.. 穿越。</summary>
    internal static bool IsAllowedEntry(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return normalized == "backup.json"
            || normalized == "data/config.json"
            || normalized.StartsWith("tools/", StringComparison.Ordinal)
            || normalized.StartsWith("apps/", StringComparison.Ordinal)
            || normalized.StartsWith("data/storage/", StringComparison.Ordinal);
    }

    private string ResolveBackupPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !BackupNamePattern().IsMatch(name))
        {
            throw new BridgeException("BACKUP_NAME_INVALID", "备份文件名不合法");
        }

        return Path.Combine(BackupsDirectory, name);
    }

    private string UniqueName(string candidate)
    {
        if (!File.Exists(Path.Combine(BackupsDirectory, candidate)))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(candidate);
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var next = $"{stem}-{suffix}.zip";
            if (!File.Exists(Path.Combine(BackupsDirectory, next)))
            {
                return next;
            }
        }

        throw new BridgeException("BACKUP_CREATE_FAILED", "同名备份过多，请清理后重试");
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}\\.zip$")]
    private static partial Regex BackupNamePattern();
}