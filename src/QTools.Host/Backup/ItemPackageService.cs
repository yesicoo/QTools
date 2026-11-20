using System.IO.Compression;
using System.Text;
using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Creator;
using QTools.Host.Launcher;
using QTools.Host.Logging;
using QTools.Host.Serialization;
using QTools.Host.Tools;

namespace QTools.Host.Backup;

/// <summary>导出包清单（qtools-export.json）：记录条目类型与导出时的运行版本。</summary>
public sealed record ItemExportManifest(
    int SchemaVersion,
    string Kind,
    string Id,
    string Name,
    string? Version,
    string ExportedAtUtc,
    string AppVersion);

/// <summary>manager.exportItem 结果（用户取消保存对话框时 Path 为 null）。</summary>
public sealed record ItemExportResult(string? Path, string? FileName, long SizeBytes);

/// <summary>
/// manager.pickImportFile 结果：Kind 为 "html"（附带文件内容，走既有导入审查流程）
/// 或 "package"（附带包内元数据，走 manager.importItemPackage）；取消时 Path 为 null、Kind 为空。
/// </summary>
public sealed record PickedImportFileView(
    string? Path,
    string? FileName,
    string Kind,
    string? Html,
    string? Name,
    string? Version,
    string? ItemKind);

/// <summary>manager.importItemPackage 结果（小工具复用导入审查字段；启动项/在线工具直接 Saved=true）。</summary>
public sealed record ImportItemResult(
    string Kind,
    string ItemId,
    string ItemName,
    bool Saved,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<SecurityFinding> Findings,
    bool HasFatal,
    bool RequiresExplicitAcceptance,
    IReadOnlyList<string> ExternalAssets);

/// <summary>
/// 单条目导出/导入包（.qtools）：小工具导出**当前运行版本**（不含 .versions 历史），
/// 手动启动项导出 app.json，在线工具导出 web.json。
/// <para>
/// 与整机备份（<see cref="BackupService"/>）是两套包：本包清单为 qtools-export.json，
/// 导入时按清单自动识别类型；整机备份包（backup.json）会被明确拒绝并指引到设置页。
/// 解压全程白名单校验 + 防 ZipSlip + 体积上限；小工具导入重跑安全审查，高危项需显式确认。
/// </para>
/// </summary>
public sealed class ItemPackageService
{
    public const string ManifestEntryName = "qtools-export.json";
    public const string FileExtension = ".qtools";

    private const int CurrentSchemaVersion = 1;
    private const long MaxPackageBytes = 64L * 1024 * 1024;
    private const long MaxEntryBytes = 16L * 1024 * 1024;
    private const int MaxEntries = 2000;

    private readonly ToolRegistry _tools;
    private readonly ToolImporter _importer;
    private readonly AppRegistry _apps;
    private readonly AppStore _appStore;
    private readonly WebToolRegistry _webTools;
    private readonly WebToolStore _webToolStore;
    private readonly string _root;
    private readonly string _stagingRoot;

    public ItemPackageService(
        ToolRegistry tools,
        ToolImporter importer,
        AppRegistry apps,
        AppStore appStore,
        WebToolRegistry webTools,
        WebToolStore webToolStore,
        string rootDirectory,
        string stagingDirectory)
    {
        _tools = tools;
        _importer = importer;
        _apps = apps;
        _appStore = appStore;
        _webTools = webTools;
        _webToolStore = webToolStore;
        _root = Path.GetFullPath(rootDirectory);
        _stagingRoot = Path.GetFullPath(stagingDirectory);
    }

    /// <summary>保存对话框的默认文件名：{条目名}.qtools（替换非法字符）。</summary>
    public string GetDefaultFileName(string kind, string id)
    {
        var name = ResolveItem(kind, id).Name;
        var builder = new StringBuilder();
        foreach (var ch in name)
        {
            builder.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(ch)
                ? '_'
                : ch);
        }

        var stem = builder.ToString().Trim().TrimEnd('.', ' ');
        if (stem.Length > 80) { stem = stem[..80].TrimEnd('.', ' '); }
        if (stem.Length == 0) { stem = "qtools-item"; }
        return stem + FileExtension;
    }

    /// <summary>把一个条目导出到用户选定的路径（只含当前运行版本）。</summary>
    public ItemExportResult Export(string kind, string id, string targetPath)
    {
        var item = ResolveItem(kind, id);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new BridgeException("EXPORT_PATH_REQUIRED", "未选择导出位置");
        }

        targetPath = Path.GetFullPath(targetPath);
        if (string.IsNullOrEmpty(Path.GetExtension(targetPath)))
        {
            targetPath += FileExtension;
        }

        var prefix = kind switch
        {
            LauncherListBuilder.KindTool => "tools/" + id + "/",
            LauncherListBuilder.KindApp => "apps/" + id + "/",
            _ => "webtools/" + id + "/"
        };

        IEnumerable<(string Relative, string Full)> files = kind == LauncherListBuilder.KindTool
            ? EnumerateToolFiles(item.Directory).Select(f => (prefix + f, Path.Combine(item.Directory, f)))
            : new[] { (prefix + item.FileName, Path.Combine(item.Directory, item.FileName)) };

        var manifest = new ItemExportManifest(
            CurrentSchemaVersion, kind, id, item.Name, item.Version,
            DateTimeOffset.UtcNow.ToString("o"), AppInfo.Version);

        var temporary = targetPath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteJsonEntry(archive, ManifestEntryName, manifest, AppJsonContext.Default.ItemExportManifest);
                foreach (var (relative, full) in files)
                {
                    var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    using var source = File.OpenRead(full);
                    using var destination = entry.Open();
                    source.CopyTo(destination);
                }
            }

            File.Move(temporary, targetPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(temporary);
            Log.Error($"导出条目失败 {kind}:{id}", ex);
            throw new BridgeException("EXPORT_FAILED", $"导出失败：{ex.Message}");
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }

        var size = new FileInfo(targetPath).Length;
        Log.Info($"已导出 {kind}:{id}（{item.Name}）→ {targetPath}（{size} 字节）");
        return new ItemExportResult(targetPath, Path.GetFileName(targetPath), size);
    }

    /// <summary>
    /// 识别用户选中的导入文件：HTML 直接读回内容（复用既有审查流程），
    /// 导出包读清单返回元数据；两者都不是则明确报错。
    /// </summary>
    public PickedImportFileView Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new BridgeException("IMPORT_FILE_NOT_FOUND", "文件不存在");
        }

        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".html" or ".htm")
        {
            var html = File.ReadAllText(path, Encoding.UTF8);
            if (Encoding.UTF8.GetByteCount(html) > ToolImporter.MaxHtmlBytes)
            {
                throw new BridgeException(
                    "IMPORT_HTML_TOO_LARGE",
                    $"HTML 文件过大，上限 {ToolImporter.MaxHtmlBytes / 1024} KB");
            }

            return new PickedImportFileView(
                path, fileName, "html", html,
                Path.GetFileNameWithoutExtension(path), null, null);
        }

        var manifest = TryReadManifest(path);
        if (manifest is null)
        {
            throw new BridgeException("IMPORT_FILE_INVALID", "请选择 HTML 文件或 QTools 导出包（.qtools）");
        }

        ValidateArchive(path);
        return new PickedImportFileView(
            path, fileName, "package", null, manifest.Name, manifest.Version, manifest.Kind);
    }

    /// <summary>读取导出包清单；不是导出包（含整机备份包）返回 null。</summary>
    public static ItemExportManifest? TryReadManifest(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(ManifestEntryName);
            if (entry is null)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return JsonSerializer.Deserialize(reader.ReadToEnd(), AppJsonContext.Default.ItemExportManifest);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            Log.Warn($"导出包清单读取失败 {Path.GetFileName(path)}：{ex.Message}");
            return null;
        }
    }

    /// <summary>导入导出包：按清单类型分派；小工具重跑安全审查，高危项需 acceptHighSeverity。</summary>
    public async System.Threading.Tasks.Task<ImportItemResult> ImportPackageAsync(
        string path, bool acceptHighSeverity, System.Threading.CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new BridgeException("IMPORT_FILE_NOT_FOUND", "导出包不存在");
        }

        var info = new FileInfo(path);
        if (info.Length > MaxPackageBytes)
        {
            throw new BridgeException("IMPORT_PACKAGE_TOO_LARGE", $"导出包不超过 {MaxPackageBytes / 1024 / 1024} MB");
        }

        if (IsWholeBackup(path))
        {
            throw new BridgeException("IMPORT_PACKAGE_INVALID", "这是整机备份包，请到「设置 → 备份与恢复」导入");
        }

        var manifest = TryReadManifest(path)
            ?? throw new BridgeException("IMPORT_PACKAGE_INVALID", "不是 QTools 导出包（缺少 qtools-export.json 清单）");
        ValidateArchive(path);

        return manifest.Kind switch
        {
            LauncherListBuilder.KindTool => await ImportToolAsync(path, manifest, acceptHighSeverity, cancellationToken).ConfigureAwait(false),
            LauncherListBuilder.KindApp => await ImportAppAsync(path, manifest, cancellationToken).ConfigureAwait(false),
            LauncherListBuilder.KindWeb => await ImportWebAsync(path, manifest, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeException("IMPORT_PACKAGE_INVALID", $"导出包类型不支持：{manifest.Kind}")
        };
    }

    private async System.Threading.Tasks.Task<ImportItemResult> ImportToolAsync(
        string path, ItemExportManifest manifest, bool acceptHighSeverity,
        System.Threading.CancellationToken cancellationToken)
    {
        var prefix = "tools/" + manifest.Id + "/";
        var newId = ToolManifestValidator.ToolIdPattern().IsMatch(manifest.Id) && !_tools.Contains(manifest.Id)
            ? manifest.Id
            : _importer.GenerateToolId(manifest.Name);

        var staging = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N")[..12]);
        var stagedTool = Path.Combine(staging, newId);
        try
        {
            Directory.CreateDirectory(stagedTool);
            var extracted = 0;
            using (var archive = ZipFile.OpenRead(path))
            {
                foreach (var entry in archive.Entries)
                {
                    var relative = entry.FullName.Replace('\\', '/');
                    if (!relative.StartsWith(prefix, StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    var destination = Path.Combine(stagedTool, relative[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                    extracted += 1;
                }
            }

            if (extracted == 0)
            {
                throw new BridgeException("IMPORT_PACKAGE_INVALID", "导出包里没有工具文件");
            }

            var manifestPath = Path.Combine(stagedTool, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new BridgeException("IMPORT_PACKAGE_INVALID", "导出包缺少 manifest.json");
            }

            var toolManifest = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                AppJsonContext.Default.ToolManifest)
                ?? throw new BridgeException("IMPORT_PACKAGE_INVALID", "manifest.json 解析失败");

            var entryRelative = string.IsNullOrWhiteSpace(toolManifest.Entry) ? "index.html" : toolManifest.Entry;
            if (!ToolManifestValidator.IsSafeRelativePath(entryRelative))
            {
                throw new BridgeException("IMPORT_PACKAGE_INVALID", "导出包的入口路径不合法");
            }

            var entryPath = Path.Combine(stagedTool, entryRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryPath))
            {
                throw new BridgeException("IMPORT_PACKAGE_INVALID", $"导出包缺少入口文件 {entryRelative}");
            }

            var html = await File.ReadAllTextAsync(entryPath, cancellationToken).ConfigureAwait(false);

            // 权限取「包内声明 ∪ 代码实际用法」：既保留原工具能力，也补齐静态检测到的用法
            var permissions = (toolManifest.Permissions ?? Array.Empty<string>())
                .Where(p => ToolManifestValidator.TryParsePermission(p, out _))
                .Select(p => p.Trim().ToLowerInvariant())
                .Union(ContentSecurityScanner.DetectPermissions(html), StringComparer.Ordinal)
                .ToArray();

            toolManifest = toolManifest with { Id = newId, Permissions = permissions };
            var (findings, hasFatal) = ContentSecurityScanner.Scan(toolManifest, html);
            var externalAssets = ContentSecurityScanner.DetectExternalAssetReferences(html);

            if (hasFatal)
            {
                var fatal = string.Join("；", findings
                    .Where(f => f.Severity == SecuritySeverity.Fatal)
                    .Select(f => f.Message));
                Log.Warn($"导出包导入被拒绝（{newId}）：{fatal}");
                throw new BridgeException("IMPORT_REJECTED", $"内容未通过安全审查：{fatal}");
            }

            var requiresAcceptance = findings.Any(f => f.Severity == SecuritySeverity.High);
            if (requiresAcceptance && !acceptHighSeverity)
            {
                return new ImportItemResult(
                    LauncherListBuilder.KindTool, newId, toolManifest.Name, false,
                    permissions, findings, false, true, externalAssets);
            }

            // 校验通过才整体移入 tools/，失败不留半成品
            var liveDirectory = Path.Combine(_root, "tools", newId);
            if (Directory.Exists(liveDirectory))
            {
                throw new BridgeException("IMPORT_REJECTED", $"工具目录已存在：{newId}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(liveDirectory)!);
            Directory.Move(stagedTool, liveDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(liveDirectory, "manifest.json"),
                JsonSerializer.Serialize(toolManifest, AppJsonContext.Default.ToolManifest),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            var loaded = _tools.Reload(newId);
            if (loaded is null || !loaded.IsValid)
            {
                var errors = loaded?.Errors is { Count: > 0 } ? string.Join("；", loaded.Errors) : "未知原因";
                TryDeleteDirectory(liveDirectory);
                Log.Warn($"导出包导入未通过运行时校验（{newId}）：{errors}");
                throw new BridgeException("IMPORT_REJECTED", $"工具未通过运行时校验：{errors}");
            }

            Log.Info($"已从导出包导入工具 {newId}（{toolManifest.Name} v{toolManifest.Version}）");
            return new ImportItemResult(
                LauncherListBuilder.KindTool, newId, toolManifest.Name, true,
                permissions, findings, false, false, externalAssets);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async System.Threading.Tasks.Task<ImportItemResult> ImportAppAsync(
        string path, ItemExportManifest manifest, System.Threading.CancellationToken cancellationToken)
    {
        var entry = await ReadEntryAsync<AppEntry>(
            path, "apps/" + manifest.Id + "/app.json", AppJsonContext.Default.AppEntry, cancellationToken).ConfigureAwait(false);

        var loaded = await _appStore.AddAsync(
            entry.Name, entry.Target, entry.Arguments, entry.WorkingDirectory,
            entry.Category, entry.Glyph, cancellationToken).ConfigureAwait(false);

        Log.Info($"已从导出包导入启动项 {loaded.Id}（{loaded.Name}）");
        return SavedResult(LauncherListBuilder.KindApp, loaded.Id, loaded.Name);
    }

    private async System.Threading.Tasks.Task<ImportItemResult> ImportWebAsync(
        string path, ItemExportManifest manifest, System.Threading.CancellationToken cancellationToken)
    {
        var entry = await ReadEntryAsync<WebToolEntry>(
            path, "webtools/" + manifest.Id + "/web.json", AppJsonContext.Default.WebToolEntry, cancellationToken).ConfigureAwait(false);

        var loaded = await _webToolStore.AddAsync(
            entry.Name, entry.Url, entry.Category, entry.Glyph,
            cancellationToken, entry.Description).ConfigureAwait(false);

        Log.Info($"已从导出包导入在线工具 {loaded.Id}（{loaded.Name}）");
        return SavedResult(LauncherListBuilder.KindWeb, loaded.Id, loaded.Name);
    }

    private static ImportItemResult SavedResult(string kind, string id, string name) =>
        new(kind, id, name, true, [], [], false, false, []);

    private static async System.Threading.Tasks.Task<T> ReadEntryAsync<T>(
        string path, string entryName, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        System.Threading.CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName)
            ?? throw new BridgeException("IMPORT_PACKAGE_INVALID", $"导出包缺少 {entryName}");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new BridgeException("IMPORT_PACKAGE_INVALID", $"{entryName} 解析失败");
    }

    private (string Name, string? Version, string Directory, string FileName) ResolveItem(string kind, string id)
    {
        switch (kind)
        {
            case LauncherListBuilder.KindTool:
                var tool = _tools.Find(id)
                    ?? throw new BridgeException("TOOL_NOT_FOUND", $"未找到工具 '{id}'");
                return (tool.Name, tool.Manifest.Version, tool.Directory, "manifest.json");

            case LauncherListBuilder.KindApp:
                var app = _apps.Find(id)
                    ?? throw new BridgeException("APP_NOT_FOUND", $"未找到启动项 '{id}'");
                return (app.Name, null, app.Directory, "app.json");

            case LauncherListBuilder.KindWeb:
                var web = _webTools.Find(id)
                    ?? throw new BridgeException("WEB_NOT_FOUND", $"未找到在线工具 '{id}'");
                return (web.Name, null, web.Directory, "web.json");

            default:
                throw new BridgeException("EXPORT_KIND_INVALID", "本机程序不支持导出（请导出小工具 / 启动项 / 在线工具）");
        }
    }

    /// <summary>工具目录内的全部文件（相对路径，'/' 分隔）；跳过点号前缀目录，绝不带上 .versions 历史。</summary>
    private static IEnumerable<string> EnumerateToolFiles(string toolDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(toolDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(toolDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.')))
            {
                continue;
            }

            yield return relative;
        }
    }

    private static bool IsWholeBackup(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.GetEntry("backup.json") is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>白名单 + 防穿越 + 体积上限：只允许清单与三类条目目录，拒绝点号前缀段（.versions）。</summary>
    internal static void ValidateArchive(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry(ManifestEntryName) is null)
            {
                throw new BridgeException("IMPORT_PACKAGE_INVALID", "不是 QTools 导出包（缺少 qtools-export.json 清单）");
            }

            if (archive.Entries.Count > MaxEntries)
            {
                throw new BridgeException("IMPORT_PACKAGE_INVALID", $"导出包条目过多（上限 {MaxEntries}）");
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (!IsAllowedEntry(entry.FullName))
                {
                    throw new BridgeException("IMPORT_PACKAGE_INVALID", $"导出包含非法路径条目：{entry.FullName}");
                }

                if (entry.Length > MaxEntryBytes)
                {
                    throw new BridgeException("IMPORT_PACKAGE_INVALID", $"导出包内单个文件过大：{entry.FullName}");
                }

                total += entry.Length;
                if (total > MaxPackageBytes)
                {
                    throw new BridgeException("IMPORT_PACKAGE_INVALID", "导出包解压后体积过大");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new BridgeException("IMPORT_PACKAGE_INVALID", $"导出包无法读取：{ex.Message}");
        }
    }

    internal static bool IsAllowedEntry(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.StartsWith('.'))
            {
                return false;
            }
        }

        return normalized == ManifestEntryName
            || normalized.StartsWith("tools/", StringComparison.Ordinal)
            || normalized.StartsWith("apps/", StringComparison.Ordinal)
            || normalized.StartsWith("webtools/", StringComparison.Ordinal);
    }

    private static void WriteJsonEntry<T>(ZipArchive archive, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, typeInfo));
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}