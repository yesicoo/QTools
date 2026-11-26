using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QTools.Host.Backup;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Creator;
using QTools.Host.Launcher;
using QTools.Host.Logging;
using QTools.Host.Serialization;
using QTools.Host.Tools;

namespace QTools.Host.Source;

public sealed class ToolSourceService
{
    internal const long MaxExtractedPackageBytes = 24L * 1024 * 1024;

    private readonly ToolRegistry _tools;
    private readonly WebToolRegistry _webTools;
    private readonly WebToolStore _webToolStore;
    private readonly ItemPackageService _packages;
    private readonly UsageStore? _usage;
    private readonly string _configPath;
    private readonly string _toolsRoot;
    private readonly string _cacheRoot;
    private readonly string _rollbackRoot;
    private readonly SourceClient _client;
    private readonly SemaphoreSlim[] _installLocks = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public ToolSourceService(
        ToolRegistry tools,
        WebToolRegistry webTools,
        WebToolStore webToolStore,
        ItemPackageService packages,
        UsageStore? usage,
        string configPath,
        string toolsRoot,
        string cacheRoot,
        string rollbackRoot)
        : this(tools, webTools, webToolStore, packages, usage, configPath, toolsRoot, cacheRoot, rollbackRoot, null)
    {
    }

    internal ToolSourceService(
        ToolRegistry tools,
        WebToolRegistry webTools,
        WebToolStore webToolStore,
        ItemPackageService packages,
        UsageStore? usage,
        string configPath,
        string toolsRoot,
        string cacheRoot,
        string rollbackRoot,
        SourceClient? client)
    {
        _tools = tools;
        _webTools = webTools;
        _webToolStore = webToolStore;
        _packages = packages;
        _usage = usage;
        _configPath = configPath;
        _toolsRoot = Path.GetFullPath(toolsRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _rollbackRoot = Path.GetFullPath(rollbackRoot);
        _client = client ?? new SourceClient();
    }

    public SourceConfigView GetConfig()
    {
        var config = QTools.Host.Ai.ConfigStore.Load(_configPath);
        return new SourceConfigView(config.Source?.BaseUrl);
    }

    public SourceConfigView SetConfig(string? baseUrl)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!SourceClient.TryNormalizeBaseUrl(baseUrl, out normalized))
            {
                throw new BridgeException("SOURCE_URL_INVALID", "工具源地址必须是 HTTP(S)，且不能包含账号、查询参数或片段");
            }
        }

        var config = QTools.Host.Ai.ConfigStore.Load(_configPath);
        QTools.Host.Ai.ConfigStore.Save(_configPath, config with
        {
            Source = new SourceConfigSection(normalized),
        });
        return new SourceConfigView(normalized);
    }

    public async Task<SourceConfigView> TestAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (!SourceClient.TryNormalizeBaseUrl(config.BaseUrl, out var baseUrl))
        {
            throw new BridgeException("SOURCE_NOT_CONFIGURED", "请先配置工具源地址");
        }

        try
        {
            return await _client.MetaAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
    }

    public async Task<SourceCatalogView> CatalogAsync(
        string? query, string? category, int page, int pageSize, CancellationToken cancellationToken)
    {
        var baseUrl = RequireBaseUrl();
        try
        {
            return await _client.CatalogAsync(baseUrl, query, category, page, pageSize, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
    }

    public async Task<SourceToolDetailView> ToolAsync(string id, string? version, CancellationToken cancellationToken)
    {
        ValidateToolId(id);
        if (!string.IsNullOrWhiteSpace(version) && !ToolManifestValidator.VersionPattern().IsMatch(version))
        {
            throw new BridgeException("SOURCE_VERSION_INVALID", "工具版本必须是语义化版本（如 1.0.0）");
        }

        var baseUrl = RequireBaseUrl();
        try
        {
            var detail = await _client.ToolAsync(baseUrl, id, version, cancellationToken).ConfigureAwait(false);
            if (detail.Manifest is null || !string.Equals(detail.Tool.Id, id, StringComparison.Ordinal) ||
                detail.Manifest.Id != id ||
                !ToolManifestValidator.VersionPattern().IsMatch(detail.Tool.Version) ||
                !ToolManifestValidator.VersionPattern().IsMatch(detail.Manifest.Version))
            {
                throw new SourceException("SOURCE_PROTOCOL_INVALID", "工具源返回的工具身份或版本不合法");
            }

            if (!string.Equals(detail.Tool.Version, detail.Manifest.Version, StringComparison.Ordinal))
            {
                throw new SourceException("SOURCE_PROTOCOL_INVALID", "工具源详情与 manifest 版本不一致");
            }

            if (!string.IsNullOrWhiteSpace(version) && !string.Equals(detail.Tool.Version, version, StringComparison.Ordinal))
            {
                throw new SourceException("SOURCE_VERSION_MISMATCH", "工具源返回的版本与请求版本不一致");
            }

            return detail;
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
    }

    public Uri ResolvePreviewUrl(SourceToolDetailView detail)
    {
        if (detail.Tool.Kind == "web")
        {
            var url = detail.Tool.Url?.Trim() ?? "";
            if (url.Length is 0 or > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new BridgeException("SOURCE_PROTOCOL_INVALID", "源站返回的在线工具网址不合法");
            }
            return uri;
        }

        try
        {
            return SourceClient.ResolveUrl(RequireBaseUrl(), detail.Tool.PreviewUrl);
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
    }

    public async Task<Uri> PreviewUrlAsync(string id, string? version, CancellationToken cancellationToken)
    {
        var detail = await ToolAsync(id, version, cancellationToken).ConfigureAwait(false);
        return ResolvePreviewUrl(detail);
    }

    public async Task<SourceInstallResult> InstallAsync(
        string id, string? version, bool acceptHighSeverity, string? reviewedSha256,
        CancellationToken cancellationToken)
    {
        ValidateToolId(id);
        var gate = _installLocks[(uint)id.GetHashCode() % (uint)_installLocks.Length];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(id, version, acceptHighSeverity, reviewedSha256, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SourceInstallResult> InstallCoreAsync(
        string id, string? version, bool acceptHighSeverity, string? reviewedSha256,
        CancellationToken cancellationToken)
    {
        var detail = await ToolAsync(id, version, cancellationToken).ConfigureAwait(false);
        if (detail.Tool.Kind == "web")
        {
            return await InstallWebCoreAsync(detail, cancellationToken).ConfigureAwait(false);
        }

        var local = _tools.Find(id);
        if (local is not null && CompareVersion(local.Manifest.Version, detail.Tool.Version) >= 0)
        {
            return UnchangedResult(local.Manifest, []);
        }

        var work = Path.Combine(_cacheRoot, "install-" + Guid.NewGuid().ToString("N")[..12]);
        var stagedTool = Path.Combine(work, id);
        var packagePath = work + ".qtools";
        try
        {
            Directory.CreateDirectory(work);
            var baseUrl = RequireBaseUrl();
            var (size, sha256) = await _client.DownloadPackageAsync(
                baseUrl, id, detail.Tool.Version, packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha256, detail.Tool.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeException("SOURCE_HASH_MISMATCH", "安装包 SHA256 与源站记录不一致，已拒绝安装");
            }

            if (size != detail.Tool.SizeBytes)
            {
                throw new BridgeException("SOURCE_PACKAGE_SIZE_MISMATCH", "安装包大小与源站记录不一致");
            }

            ValidatePackage(packagePath, id, detail.Tool.Version, validateManifestSemantics: false);
            Directory.CreateDirectory(stagedTool);
            var prefix = "tools/" + id + "/";
            await SourcePackageExtractor.ExtractAsync(packagePath, prefix, stagedTool, cancellationToken).ConfigureAwait(false);

            var manifestPath = Path.Combine(stagedTool, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new BridgeException("SOURCE_PACKAGE_INVALID", "安装包缺少 manifest.json");
            }

            var manifest = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                AppJsonContext.Default.ToolManifest)
                ?? throw new BridgeException("SOURCE_PACKAGE_INVALID", "manifest.json 解析失败");
            if (manifest.Id != id || manifest.Version != detail.Tool.Version || !ToolManifestValidator.IsSafeRelativePath(manifest.Entry))
            {
                throw new BridgeException("SOURCE_PACKAGE_IDENTITY_MISMATCH", "安装包 manifest 与源站详情不一致");
            }

            var manifestErrors = ToolManifestValidator.Validate(manifest, id);
            if (manifestErrors.Count > 0)
            {
                throw new BridgeException("SOURCE_SECURITY_REJECTED", $"安装包 manifest 未通过本地校验：{manifestErrors[0]}");
            }

            if (local is not null)
            {
                foreach (var preserved in new[] { "files" })
                {
                    var source = Path.Combine(local.Directory, preserved);
                    if (Directory.Exists(source))
                    {
                        await CopyDirectoryAsync(source, Path.Combine(stagedTool, preserved), cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var entryPath = Path.Combine(stagedTool, manifest.Entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryPath))
            {
                throw new BridgeException("SOURCE_PACKAGE_INVALID", $"安装包缺少入口文件 {manifest.Entry}");
            }

            var html = await File.ReadAllTextAsync(entryPath, cancellationToken).ConfigureAwait(false);
            var permissions = (manifest.Permissions ?? Array.Empty<string>())
                .Where(p => ToolManifestValidator.TryParsePermission(p, out _))
                .Select(p => p.Trim().ToLowerInvariant())
                .Union(ContentSecurityScanner.DetectPermissions(html), StringComparer.Ordinal)
                .ToArray();
            manifest = manifest with { Permissions = permissions };
            var (findings, hasFatal) = ContentSecurityScanner.Scan(manifest, html);
            if (hasFatal)
            {
                throw new BridgeException("SOURCE_SECURITY_REJECTED", "安装包未通过本地安全审查");
            }

            var externalAssets = ContentSecurityScanner.DetectExternalAssetReferences(html);
            var requiresAcceptance = findings.Any(f => f.Severity == SecuritySeverity.High);
            var reviewMatches = string.Equals(reviewedSha256, sha256, StringComparison.OrdinalIgnoreCase);
            if (requiresAcceptance && (!acceptHighSeverity || !reviewMatches))
            {
                return new SourceInstallResult(id, manifest.Name, manifest.Version, "review", false,
                    permissions, findings, false, true, externalAssets) { PackageSha256 = sha256 };
            }

            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(manifest, AppJsonContext.Default.ToolManifest),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            string action;
            var liveDirectory = Path.Combine(_toolsRoot, id);
            cancellationToken.ThrowIfCancellationRequested();
            if (local is null)
            {
                action = "installed";
                Directory.CreateDirectory(_toolsRoot);
                Directory.Move(stagedTool, liveDirectory);
                try
                {
                    var loaded = _tools.Reload(id);
                    if (loaded is null || !loaded.IsValid)
                    {
                        throw new BridgeException("SOURCE_RUNTIME_VALIDATION_FAILED", "安装后 manifest 未通过宿主校验");
                    }
                }
                catch
                {
                    TryDeleteDirectory(liveDirectory);
                    _tools.Reload(id);
                    throw;
                }
            }
            else
            {
                action = "updated";
                await UpdateExistingAsync(local, stagedTool, work, manifest.Version, cancellationToken).ConfigureAwait(false);
            }

            _usage?.EnsureSeeded(new[] { id });
            Log.Info($"工具源安装 {id} v{manifest.Version}（{action}）");
            return new SourceInstallResult(id, manifest.Name, manifest.Version, action, true,
                permissions, findings, false, false, externalAssets) { PackageSha256 = sha256 };
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"工具源安装失败 {id}", ex);
            throw new BridgeException("SOURCE_INSTALL_FAILED", "安装失败，请关闭已打开的工具窗口后重试；旧版本已保留");
        }
        finally
        {
            TryDeleteFile(packagePath);
            TryDeleteDirectory(work);
        }
    }

    private async Task<SourceInstallResult> InstallWebCoreAsync(
        SourceToolDetailView detail, CancellationToken cancellationToken)
    {
        var id = detail.Tool.Id;
        var work = Path.Combine(_cacheRoot, "install-" + Guid.NewGuid().ToString("N")[..12]);
        var packagePath = work + ".qtools";
        try
        {
            Directory.CreateDirectory(work);
            var baseUrl = RequireBaseUrl();
            var (size, sha256) = await _client.DownloadPackageAsync(
                baseUrl, id, detail.Tool.Version, packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha256, detail.Tool.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeException("SOURCE_HASH_MISMATCH", "安装包 SHA256 与源站记录不一致，已拒绝安装");
            }
            if (size != detail.Tool.SizeBytes)
            {
                throw new BridgeException("SOURCE_PACKAGE_SIZE_MISMATCH", "安装包大小与源站记录不一致");
            }

            var entry = ReadWebPackage(packagePath, id, detail.Tool.Version);
            var action = _webTools.Find(id) is null ? "installed" : "updated";
            await _webToolStore.SaveFromSourceAsync(entry, cancellationToken).ConfigureAwait(false);
            _usage?.EnsureSeeded(new[] { id });
            Log.Info($"工具源在线工具 {id} v{entry.Version}（{action}）：{entry.Url}");
            return new SourceInstallResult(id, entry.Name, entry.Version, action, true,
                Array.Empty<string>(), Array.Empty<SecurityFinding>(), false, false, Array.Empty<string>())
            { PackageSha256 = sha256 };
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"工具源在线工具安装失败 {id}", ex);
            throw new BridgeException("SOURCE_INSTALL_FAILED", "安装在线工具失败，请稍后重试");
        }
        finally
        {
            TryDeleteFile(packagePath);
            TryDeleteDirectory(work);
        }
    }

    private static WebToolEntry ReadWebPackage(string packagePath, string id, string version)
    {
        var export = ItemPackageService.TryReadManifest(packagePath)
            ?? throw new BridgeException("SOURCE_PACKAGE_INVALID", "下载内容不是 QTools 导出包");
        if (export.Kind != LauncherListBuilder.KindWeb || export.Id != id || export.Version != version)
        {
            throw new BridgeException("SOURCE_PACKAGE_IDENTITY_MISMATCH", "在线工具包的身份或版本与源站详情不一致");
        }

        try { ItemPackageService.ValidateArchive(packagePath); }
        catch (BridgeException ex) when (ex.Code == "IMPORT_PACKAGE_INVALID")
        {
            throw new BridgeException("SOURCE_PACKAGE_INVALID", ex.Message);
        }

        var prefix = "webtools/" + id + "/";
        using var archive = ZipFile.OpenRead(packagePath);
        var webEntry = archive.GetEntry(prefix + "web.json")
            ?? throw new BridgeException("SOURCE_PACKAGE_INVALID", "在线工具包缺少 web.json");
        if (webEntry.Length > 64 * 1024)
        {
            throw new BridgeException("SOURCE_PACKAGE_INVALID", "在线工具 web.json 超过 64 KiB");
        }
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (relative != ItemPackageService.ManifestEntryName &&
                !relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new BridgeException("SOURCE_PACKAGE_INVALID", "源安装包只能包含当前在线工具");
            }
        }

        WebToolEntry parsed;
        try
        {
            using var reader = new StreamReader(webEntry.Open(), Encoding.UTF8);
            parsed = JsonSerializer.Deserialize(reader.ReadToEnd(), AppJsonContext.Default.WebToolEntry)
                ?? throw new BridgeException("SOURCE_PACKAGE_INVALID", "在线工具 web.json 解析失败");
        }
        catch (JsonException)
        {
            throw new BridgeException("SOURCE_PACKAGE_INVALID", "在线工具 web.json 解析失败");
        }

        var errors = WebToolValidator.Validate(parsed, id);
        if (errors.Count > 0)
        {
            throw new BridgeException("SOURCE_PACKAGE_INVALID", $"在线工具未通过校验：{errors[0]}");
        }
        if (!string.Equals(parsed.Version, version, StringComparison.Ordinal))
        {
            throw new BridgeException("SOURCE_PACKAGE_IDENTITY_MISMATCH", "在线工具版本与源站详情不一致");
        }
        return parsed;
    }

    internal async Task<SourceDownloadResult> DownloadAsync(
        SourceToolDetailView detail, string targetPath, CancellationToken cancellationToken)
    {
        var id = detail.Tool.Id;
        ValidateToolId(id);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new BridgeException("SOURCE_DOWNLOAD_PATH_REQUIRED", "未选择安装包保存位置");
        }

        targetPath = Path.GetFullPath(targetPath);
        var extension = Path.GetExtension(targetPath);
        if (!string.Equals(extension, ItemPackageService.FileExtension, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            targetPath += ItemPackageService.FileExtension;
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp";
        try
        {
            var baseUrl = RequireBaseUrl();
            var (size, sha256) = await _client.DownloadPackageAsync(
                baseUrl, id, detail.Tool.Version, temporary, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha256, detail.Tool.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeException("SOURCE_HASH_MISMATCH", "安装包 SHA256 与源站记录不一致，已拒绝保存");
            }

            if (size != detail.Tool.SizeBytes)
            {
                throw new BridgeException("SOURCE_PACKAGE_SIZE_MISMATCH", "安装包大小与源站记录不一致");
            }

            if (detail.Tool.Kind == "web") { ReadWebPackage(temporary, id, detail.Tool.Version); }
            else { ValidatePackage(temporary, id, detail.Tool.Version); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, targetPath, overwrite: true);
            Log.Info($"已保存工具源安装包 {id} v{detail.Tool.Version} → {targetPath}（{size} 字节）");
            return new SourceDownloadResult(targetPath, Path.GetFileName(targetPath), size, detail.Tool.Version, sha256);
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"工具源安装包保存失败 {id}", ex);
            throw new BridgeException("SOURCE_DOWNLOAD_FAILED", "下载失败，请检查保存位置是否可写后重试");
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void ValidatePackage(
        string packagePath, string id, string version, bool validateManifestSemantics = true)
    {
        var export = ItemPackageService.TryReadManifest(packagePath)
            ?? throw new BridgeException("SOURCE_PACKAGE_INVALID", "下载内容不是 QTools 导出包");
        if (export.Kind != LauncherListBuilder.KindTool || export.Id != id || export.Version != version)
        {
            throw new BridgeException("SOURCE_PACKAGE_IDENTITY_MISMATCH", "安装包的工具身份或版本与源站详情不一致");
        }

        try
        {
            ItemPackageService.ValidateArchive(packagePath);
        }
        catch (BridgeException ex) when (ex.Code == "IMPORT_PACKAGE_INVALID")
        {
            throw new BridgeException("SOURCE_PACKAGE_INVALID", ex.Message);
        }

        var prefix = "tools/" + id + "/";
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry(prefix + "manifest.json")
            ?? throw new BridgeException("SOURCE_PACKAGE_INVALID", "安装包缺少 manifest.json");
        if (manifestEntry.Length > 64 * 1024)
        {
            throw new BridgeException("SOURCE_PACKAGE_INVALID", "安装包 manifest.json 超过 64 KiB");
        }

        ToolManifest packageManifest;
        try
        {
            using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
            packageManifest = JsonSerializer.Deserialize(
                reader.ReadToEnd(), AppJsonContext.Default.ToolManifest)
                ?? throw new BridgeException("SOURCE_PACKAGE_INVALID", "安装包 manifest.json 解析失败");
        }
        catch (JsonException)
        {
            throw new BridgeException("SOURCE_PACKAGE_INVALID", "安装包 manifest.json 解析失败");
        }

        if (packageManifest.Id != id || packageManifest.Version != version)
        {
            throw new BridgeException("SOURCE_PACKAGE_IDENTITY_MISMATCH", "安装包 manifest 与源站详情不一致");
        }

        if (validateManifestSemantics)
        {
            var manifestErrors = ToolManifestValidator.Validate(packageManifest, id);
            if (manifestErrors.Count > 0)
            {
                throw new BridgeException("SOURCE_PACKAGE_INVALID", $"安装包 manifest 未通过校验：{manifestErrors[0]}");
            }
        }

        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (relative != ItemPackageService.ManifestEntryName &&
                !relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new BridgeException("SOURCE_PACKAGE_INVALID", "源安装包只能包含当前小工具");
            }
        }
    }

    public async Task<SourceSubmitResult> SubmitAsync(string kind, string id, CancellationToken cancellationToken)
    {
        ValidateToolId(id);
        var isWeb = kind == LauncherListBuilder.KindWeb;
        var version = isWeb
            ? (_webTools.Find(id)?.Entry.Version ?? throw new BridgeException("WEB_NOT_FOUND", $"未找到在线工具 '{id}'"))
            : (_tools.Find(id)?.Manifest.Version ?? throw new BridgeException("TOOL_NOT_FOUND", $"未找到工具 '{id}'"));
        var baseUrl = RequireBaseUrl();
        var packagePath = Path.Combine(_cacheRoot, "submit-" + Guid.NewGuid().ToString("N")[..12] + ".qtools");
        try
        {
            _packages.ExportForSource(isWeb ? LauncherListBuilder.KindWeb : LauncherListBuilder.KindTool, id, packagePath);
            if (new FileInfo(packagePath).Length > SourceClient.MaxSubmitPackageBytes)
            {
                throw new BridgeException("SOURCE_PACKAGE_TOO_LARGE", "导出包超过 8 MiB，无法提交");
            }

            var result = await _client.SubmitAsync(baseUrl, packagePath, cancellationToken).ConfigureAwait(false);
            Log.Info($"已提交{(isWeb ? "在线工具" : "工具")} {id} v{version} 到工具源（{result.Id}）");
            return result;
        }
        catch (SourceException ex)
        {
            throw new BridgeException(ex.Code, ex.Message);
        }
        finally
        {
            TryDeleteFile(packagePath);
        }
    }

    private async Task UpdateExistingAsync(
        LoadedTool local, string stagedTool, string work, string newVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tools.VersionStore.Snapshot(local.Directory, local.Manifest.Version, note: "工具源更新前自动备份", replaceExisting: true);
        cancellationToken.ThrowIfCancellationRequested();
        var versionsRoot = ToolVersionStore.VersionsRoot(local.Directory);
        if (Directory.Exists(versionsRoot))
        {
            await CopyDirectoryAsync(versionsRoot, ToolVersionStore.VersionsRoot(stagedTool), cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _tools.VersionStore.Snapshot(stagedTool, newVersion, note: "工具源安装", replaceExisting: true);

        var backup = Path.Combine(_rollbackRoot, local.Id,
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + "-v" + local.Manifest.Version);
        await CopyDirectoryAsync(local.Directory, backup, cancellationToken).ConfigureAwait(false);
        var oldMoved = Path.Combine(work, "old");
        var newMoved = false;
        var oldMovedFlag = false;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Directory.Move(local.Directory, oldMoved);
            oldMovedFlag = true;
            Directory.Move(stagedTool, local.Directory);
            newMoved = true;
            var loaded = _tools.Reload(local.Id);
            if (loaded is null || !loaded.IsValid)
            {
                throw new BridgeException("SOURCE_RUNTIME_VALIDATION_FAILED", "更新后 manifest 未通过宿主校验");
            }

            Log.Info($"工具源更新完成 {local.Id}：v{local.Manifest.Version} → v{loaded.Manifest.Version}；回滚副本 {backup}");
        }
        catch
        {
            if (newMoved && Directory.Exists(local.Directory))
            {
                TryDeleteDirectory(local.Directory);
            }

            if (oldMovedFlag && Directory.Exists(oldMoved))
            {
                Directory.Move(oldMoved, local.Directory);
            }

            _tools.Reload(local.Id);
            throw;
        }
    }

    private string RequireBaseUrl()
    {
        var configured = GetConfig().BaseUrl;
        if (!SourceClient.TryNormalizeBaseUrl(configured, out var baseUrl))
        {
            throw new BridgeException("SOURCE_NOT_CONFIGURED", "请先在设置中配置工具源地址");
        }

        return baseUrl;
    }

    private static SourceInstallResult UnchangedResult(ToolManifest manifest, IReadOnlyList<SecurityFinding> findings) =>
        new(manifest.Id, manifest.Name, manifest.Version, "unchanged", false,
            manifest.Permissions ?? Array.Empty<string>(), findings, false, false, []);

    private static void ValidateToolId(string id)
    {
        if (!ToolManifestValidator.ToolIdPattern().IsMatch(id))
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "工具 ID 不合法");
        }
    }

    private static int CompareVersion(string left, string right)
    {
        var a = left.Split('-', 2);
        var b = right.Split('-', 2);
        var aCore = a[0].Split('.');
        var bCore = b[0].Split('.');
        for (var i = 0; i < 3; i++)
        {
            var comparison = CompareNumericIdentifier(aCore[i], bCore[i]);
            if (comparison != 0) { return comparison; }
        }

        var aPreRelease = a.Length > 1 ? a[1] : string.Empty;
        var bPreRelease = b.Length > 1 ? b[1] : string.Empty;
        if (aPreRelease == bPreRelease) { return 0; }
        if (aPreRelease.Length == 0) { return 1; }
        if (bPreRelease.Length == 0) { return -1; }

        var aParts = aPreRelease.Split('.');
        var bParts = bPreRelease.Split('.');
        for (var i = 0; i < Math.Min(aParts.Length, bParts.Length); i++)
        {
            var aNumeric = aParts[i].All(char.IsAsciiDigit);
            var bNumeric = bParts[i].All(char.IsAsciiDigit);
            var comparison = aNumeric && bNumeric
                ? CompareNumericIdentifier(aParts[i], bParts[i])
                : aNumeric ? -1
                : bNumeric ? 1
                : string.CompareOrdinal(aParts[i], bParts[i]);
            if (comparison != 0) { return comparison; }
        }

        return aParts.Length.CompareTo(bParts.Length);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        if (left.Length == 0) { left = "0"; }
        if (right.Length == 0) { right = "0"; }
        if (left.Length != right.Length) { return left.Length.CompareTo(right.Length); }
        return string.CompareOrdinal(left, right);
    }

    private static async Task CopyDirectoryAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, useAsync: true);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, useAsync: true);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            await CopyDirectoryAsync(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
