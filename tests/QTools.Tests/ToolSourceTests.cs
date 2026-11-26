using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Backup;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Creator;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Source;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

[CollectionDefinition("工具源串行", DisableParallelization = true)]
public sealed class ToolSourceCollection { }

[Collection("工具源串行")]
public sealed class ToolSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtools-source-" + Guid.NewGuid().ToString("n"));
    private readonly HttpClient _http;
    private readonly SourceTestHandler _handler = new();

    public ToolSourceTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.Initialize(_root);
        _http = new HttpClient(_handler);
        ConfigStore.Save(ConfigPath, AppConfig.Default);
    }

    private string ToolsRoot => Path.Combine(_root, "tools");
    private string CacheRoot => Path.Combine(_root, "cache");
    private string RollbackRoot => Path.Combine(_root, "rollback");
    private string ConfigPath => Path.Combine(_root, "data", "config.json");

    private SourceTestContext CreateContext()
    {
        var tools = new ToolRegistry(ToolsRoot);
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(_root, "apps"));
        apps.Rescan();
        var webTools = new WebToolRegistry(Path.Combine(_root, "webtools"));
        webTools.Rescan();
        var packages = new ItemPackageService(
            tools, new ToolImporter(tools, ToolsRoot), apps,
            new AppStore(apps, apps.AppsDirectory), webTools,
            new WebToolStore(webTools, webTools.WebToolsDirectory), _root,
            Path.Combine(_root, "staging"));
        var service = new ToolSourceService(
            tools, webTools, new WebToolStore(webTools, webTools.WebToolsDirectory), packages,
            new UsageStore(Path.Combine(_root, "data", "usage.json")),
            ConfigPath, ToolsRoot, CacheRoot, RollbackRoot, new SourceClient(_http));
        return new SourceTestContext(tools, service, new BridgeDispatcher(
            tools, new ToolStorage(Path.Combine(_root, "data", "storage")),
            itemPackages: packages, sourceService: service));
    }

    private void ConfigureSource() => CreateContext().Service.SetConfig("https://source.example/nested/base");

    [Fact]
    public async Task NewInstallSeedsUsageAndLandsFiles()
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        offer = offer with { Package = AddPackageFile(offer.Package, "tools/calc/files/data.bin", "数据") };
        offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = Hash(offer.Package), SizeBytes = offer.Package.Length } } };
        _handler.AddOffer(offer);
        var context = CreateContext();
        var result = await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);

        Assert.Equal("installed", result.Action);
        Assert.True(result.Saved);
        var tool = Assert.IsType<LoadedTool>(context.Tools.Find("calc"));
        Assert.True(tool.IsValid);
        Assert.Equal("1.0.0", tool.Manifest.Version);
        Assert.Equal("<h1>计算器</h1>", File.ReadAllText(Path.Combine(tool.Directory, "index.html")));
        Assert.Equal("数据", File.ReadAllText(Path.Combine(tool.Directory, "files", "data.bin")));
        var usage = new UsageStore(Path.Combine(_root, "data", "usage.json")).Get("calc");
        Assert.NotNull(usage);
        Assert.Equal(1, usage!.Count);
        Assert.Equal("", usage.LastUsedUtc);
        Assert.Contains(_handler.Requests, request => request.Uri.AbsolutePath == "/nested/base/api/v1/tools/calc/package");
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task ConcurrentInstallsForSameToolAreSerialized()
    {
        ConfigureSource();
        _handler.AddOffer(CreateOffer("calc", "1.0.0", "<h1>计算器</h1>"));
        var enteredPackage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePackage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.PackageGate = async token =>
        {
            enteredPackage.TrySetResult();
            await releasePackage.Task.WaitAsync(token);
        };
        var context = CreateContext();

        var first = context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);
        await enteredPackage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.False(second.IsCompleted);
            Assert.Single(_handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/package", StringComparison.Ordinal));
        }
        finally
        {
            releasePackage.TrySetResult();
        }

        var results = await Task.WhenAll(first, second);
        Assert.Equal("installed", results[0].Action);
        Assert.Equal("unchanged", results[1].Action);
        Assert.Equal("<h1>计算器</h1>", File.ReadAllText(Path.Combine(ToolsRoot, "calc", "index.html")));
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("3.0.0")]
    public async Task SameOrOlderSourceVersionIsUnchangedWithoutDownload(string remoteVersion)
    {
        ConfigureSource();
        WriteTool("calc", "2.0.0", "<h1>本地版本</h1>");
        var local = CreateContext().Tools;
        local.Rescan();
        var original = File.ReadAllBytes(Path.Combine(ToolsRoot, "calc", "index.html"));
        _handler.AddOffer(CreateOffer("calc", remoteVersion, "<h1>源站版本</h1>"));
        var context = CreateContext();
        var result = await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);

        Assert.Equal(remoteVersion == "2.0.0" ? "unchanged" : "updated", result.Action);
        Assert.Equal(remoteVersion != "2.0.0", result.Saved);
        Assert.Equal(remoteVersion == "2.0.0" ? "2.0.0" : remoteVersion, result.Version);
        var expectedHtml = remoteVersion == "2.0.0" ? original : Encoding.UTF8.GetBytes("<h1>源站版本</h1>");
        Assert.Equal(expectedHtml, File.ReadAllBytes(Path.Combine(ToolsRoot, "calc", "index.html")));
        Assert.DoesNotContain(_handler.Requests, request => remoteVersion == "2.0.0" && request.Uri.AbsolutePath.EndsWith("/package", StringComparison.Ordinal));
        Assert.Equal(remoteVersion == "2.0.0", !Directory.Exists(CacheRoot));
    }

    [Theory]
    [InlineData(true, "SOURCE_HASH_MISMATCH")]
    [InlineData(false, "SOURCE_PACKAGE_SIZE_MISMATCH")]
    public async Task PackageIntegrityMismatchIsRejected(bool hashMismatch, string expectedCode)
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        offer = hashMismatch
            ? offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = new string('0', 64) } } }
            : offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { SizeBytes = offer.Detail.Tool.SizeBytes + 1 } } };
        _handler.AddOffer(offer);
        var context = CreateContext();
        var error = await Assert.ThrowsAsync<BridgeException>(
            () => context.Service.InstallAsync("calc", null, false, null, CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.False(context.Tools.Contains("calc"));
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task DownloadSavesVerifiedPackageWithoutInstalling()
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        _handler.AddOffer(offer);
        var context = CreateContext();
        var detail = await context.Service.ToolAsync("calc", null, CancellationToken.None);
        var target = Path.Combine(_root, "saved", "计算器-v1.0.0");

        var result = await context.Service.DownloadAsync(detail, target, CancellationToken.None);

        var savedPath = target + ItemPackageService.FileExtension;
        Assert.Equal(savedPath, result.Path);
        Assert.Equal(Path.GetFileName(savedPath), result.FileName);
        Assert.Equal(offer.Package.Length, result.SizeBytes);
        Assert.Equal("1.0.0", result.Version);
        Assert.Equal(Hash(offer.Package), result.Sha256);
        Assert.Equal(offer.Package, File.ReadAllBytes(savedPath));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(savedPath)!));
        Assert.False(context.Tools.Contains("calc"));
        Assert.False(Directory.Exists(CacheRoot));
    }

    [Theory]
    [InlineData(true, "SOURCE_HASH_MISMATCH")]
    [InlineData(false, "SOURCE_PACKAGE_SIZE_MISMATCH")]
    public async Task DownloadRejectsPackageIntegrityMismatch(bool hashMismatch, string expectedCode)
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        offer = hashMismatch
            ? offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = new string('0', 64) } } }
            : offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { SizeBytes = offer.Detail.Tool.SizeBytes + 1 } } };
        _handler.AddOffer(offer);
        var context = CreateContext();
        var detail = await context.Service.ToolAsync("calc", null, CancellationToken.None);
        var target = Path.Combine(_root, "saved", "calc.qtools");

        var error = await Assert.ThrowsAsync<BridgeException>(
            () => context.Service.DownloadAsync(detail, target, CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(target)!));
        Assert.False(context.Tools.Contains("calc"));
    }

    [Theory]
    [InlineData("invalid-request", "1.0", "SOURCE_VERSION_INVALID")]
    [InlineData("wrong-response", "1.0.0", "SOURCE_VERSION_MISMATCH")]
    [InlineData("manifest-mismatch", null, "SOURCE_PROTOCOL_INVALID")]
    [InlineData("malformed-response", null, "SOURCE_PROTOCOL_INVALID")]
    public async Task DetailVersionMustBeValidAndMatchRequest(string scenario, string? requestedVersion, string expectedCode)
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "2.0.0", "<h1>计算器</h1>");
        if (scenario == "manifest-mismatch")
        {
            offer = offer with { Detail = offer.Detail with { Manifest = CreateManifest("calc", "3.0.0") } };
        }
        else if (scenario == "malformed-response")
        {
            offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Version = "2.0" } } };
        }
        _handler.AddOffer(offer);
        var context = CreateContext();

        var error = await Assert.ThrowsAsync<BridgeException>(
            () => context.Service.ToolAsync("calc", requestedVersion, CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        if (scenario == "invalid-request") { Assert.Empty(_handler.Requests); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallCancellationAfterTransferKeepsExistingToolUntouched(bool update)
    {
        ConfigureSource();
        if (update) { WriteTool("calc", "1.0.0", "<h1>旧版本</h1>"); }
        _handler.AddOffer(CreateOffer("calc", "2.0.0", "<h1>新版本</h1>"));
        using var cancellation = new CancellationTokenSource();
        _handler.PackageContentFactory = bytes => new StreamContent(new CancelOnEofStream(bytes, cancellation));
        var context = CreateContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Service.InstallAsync("calc", null, false, null, cancellation.Token));

        if (update)
        {
            Assert.Equal("<h1>旧版本</h1>", File.ReadAllText(Path.Combine(ToolsRoot, "calc", "index.html")));
            Assert.Equal("1.0.0", context.Tools.Find("calc")!.Manifest.Version);
        }
        else
        {
            Assert.False(context.Tools.Contains("calc"));
        }
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Theory]
    [InlineData("manifest", false, "SOURCE_PACKAGE_IDENTITY_MISMATCH")]
    [InlineData("export", false, "SOURCE_PACKAGE_IDENTITY_MISMATCH")]
    [InlineData("manifest", true, "SOURCE_PACKAGE_IDENTITY_MISMATCH")]
    [InlineData("export", true, "SOURCE_PACKAGE_IDENTITY_MISMATCH")]
    public async Task PackageIdentityOrVersionMismatchIsRejectedBeforeInstallOrSave(
        string mismatch, bool download, string expectedCode)
    {
        ConfigureSource();
        var packageManifest = mismatch == "manifest"
            ? CreateManifest("calc", "2.0.0")
            : CreateManifest("calc", "1.0.0");
        var exportManifest = CreateExportManifest(
            mismatch == "export" ? "other" : "calc",
            mismatch == "export" ? "1.0.0" : "0.9.0");
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        offer = offer with
        {
            Package = BuildPackage(exportManifest, packageManifest, "<h1>计算器</h1>"),
        };
        offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = Hash(offer.Package), SizeBytes = offer.Package.Length } } };
        _handler.AddOffer(offer);
        var context = CreateContext();
        var target = Path.Combine(_root, "saved", "calc.qtools");
        var error = await Assert.ThrowsAsync<BridgeException>(async () =>
        {
            if (download)
            {
                var detail = await context.Service.ToolAsync("calc", null, CancellationToken.None);
                await context.Service.DownloadAsync(detail, target, CancellationToken.None);
            }
            else
            {
                await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);
            }
        });

        Assert.Equal(expectedCode, error.Code);
        Assert.False(context.Tools.Contains("calc"));
        Assert.False(File.Exists(target));
        if (Directory.Exists(CacheRoot))
        {
            Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgedEntryLengthIsRejectedBeforeInstallOrSave(bool download)
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        var forged = ForgeEntryLength(offer.Package, "tools/calc/index.html", 1);
        offer = offer with { Package = forged };
        offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = Hash(forged), SizeBytes = forged.Length } } };
        _handler.AddOffer(offer);
        var context = CreateContext();

        var error = await Assert.ThrowsAsync<BridgeException>(async () =>
        {
            if (download)
            {
                var detail = await context.Service.ToolAsync("calc", null, CancellationToken.None);
                await context.Service.DownloadAsync(detail, Path.Combine(_root, "saved", "calc.qtools"), CancellationToken.None);
            }
            else
            {
                await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);
            }
        });

        Assert.Equal("SOURCE_PACKAGE_INVALID", error.Code);
        Assert.Contains("实际解压长度", error.Message);
        Assert.False(context.Tools.Contains("calc"));
        if (download)
        {
            Assert.False(File.Exists(Path.Combine(_root, "saved", "calc.qtools")));
        }
        if (Directory.Exists(CacheRoot))
        {
            Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizedExportManifestIsRejectedForInstallAndDownload(bool download, bool forgeLength)
    {
        ConfigureSource();
        var export = CreateExportManifest("calc", "1.0.0") with { Name = new string('x', 64 * 1024) };
        var package = BuildPackage(export, CreateManifest("calc", "1.0.0"), "<h1>计算器</h1>");
        if (forgeLength) { package = ForgeEntryLength(package, ItemPackageService.ManifestEntryName, 1); }
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>") with { Package = package };
        offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = Hash(package), SizeBytes = package.Length } } };
        _handler.AddOffer(offer);
        var context = CreateContext();

        var error = await Assert.ThrowsAsync<BridgeException>(async () =>
        {
            if (download)
            {
                var detail = await context.Service.ToolAsync("calc", null, CancellationToken.None);
                await context.Service.DownloadAsync(detail, Path.Combine(_root, "saved", "calc.qtools"), CancellationToken.None);
            }
            else
            {
                await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);
            }
        });

        Assert.Equal("SOURCE_PACKAGE_INVALID", error.Code);
        Assert.False(context.Tools.Contains("calc"));
        if (download)
        {
            Assert.False(Directory.Exists(CacheRoot));
            var saved = Path.Combine(_root, "saved", "calc.qtools");
            Assert.False(File.Exists(saved));
            Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(saved)!));
        }
        else
        {
            Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
        }
    }

    [Fact]
    public async Task DownloadCancellationAfterTransferDoesNotOverwriteTarget()
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        _handler.AddOffer(offer);
        using var cancellation = new CancellationTokenSource();
        _handler.PackageContentFactory = bytes => new StreamContent(new CancelOnEofStream(bytes, cancellation));
        var context = CreateContext();
        var detail = await context.Service.ToolAsync("calc", null, CancellationToken.None);
        var target = Path.Combine(_root, "saved", "calc.qtools");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "原有安装包");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Service.DownloadAsync(detail, target, cancellation.Token));

        Assert.Equal("原有安装包", File.ReadAllText(target));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(target)!));
        Assert.False(context.Tools.Contains("calc"));
    }

    [Theory]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", true)]
    [InlineData("1.0.0", "1.0.0-beta.10", false)]
    [InlineData("1.0.0-beta.10", "1.0.0", true)]
    public async Task PreReleaseVersionsFollowSemVer(string localVersion, string remoteVersion, bool shouldUpdate)
    {
        ConfigureSource();
        WriteTool("calc", localVersion, "<h1>本地版本</h1>");
        _handler.AddOffer(CreateOffer("calc", remoteVersion, "<h1>源站版本</h1>"));
        var context = CreateContext();

        var result = await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);

        Assert.Equal(shouldUpdate ? "updated" : "unchanged", result.Action);
        Assert.Equal(shouldUpdate, result.Saved);
        Assert.Equal(shouldUpdate ? remoteVersion : localVersion, result.Version);
    }

    [Fact]
    public async Task HighSeverityNeedsExplicitAcceptanceBeforeSaving()
    {
        ConfigureSource();
        _handler.AddOffer(CreateOffer("evaltool", "1.0.0", "<h1>动态执行</h1><script>eval('1')</script>"));
        var context = CreateContext();
        var review = await context.Service.InstallAsync("evaltool", null, false, null, CancellationToken.None);

        Assert.Equal("review", review.Action);
        Assert.False(review.Saved);
        Assert.True(review.RequiresExplicitAcceptance);
        Assert.Contains(review.Findings, finding => finding.Severity == SecuritySeverity.High);
        Assert.False(context.Tools.Contains("evaltool"));
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));

        var staleReview = await context.Service.InstallAsync("evaltool", null, true, new string('0', 64), CancellationToken.None);
        Assert.True(staleReview.RequiresExplicitAcceptance);
        Assert.False(context.Tools.Contains("evaltool"));

        var accepted = await context.Service.InstallAsync("evaltool", null, true, review.PackageSha256, CancellationToken.None);

        Assert.Equal("installed", accepted.Action);
        Assert.True(accepted.Saved);
        Assert.True(context.Tools.Contains("evaltool"));
    }

    [Fact]
    public async Task HighSeverityReviewIsBoundToPackageSha256()
    {
        ConfigureSource();
        _handler.AddOffer(CreateOffer("evaltool", "1.0.0", "<script>eval('1')</script>"));
        var context = CreateContext();
        var review = await context.Service.InstallAsync("evaltool", null, false, null, CancellationToken.None);
        var replacement = CreateOffer("evaltool", "1.0.0", "<script>eval('2')</script>");
        _handler.AddOffer(replacement);

        var stale = await context.Service.InstallAsync("evaltool", null, true, review.PackageSha256, CancellationToken.None);

        Assert.True(stale.RequiresExplicitAcceptance);
        Assert.Equal(Hash(replacement.Package), stale.PackageSha256);
        Assert.NotEqual(review.PackageSha256, stale.PackageSha256);
        Assert.False(context.Tools.Contains("evaltool"));

        var accepted = await context.Service.InstallAsync("evaltool", null, true, stale.PackageSha256, CancellationToken.None);
        Assert.True(accepted.Saved);
    }

    [Theory]
    [InlineData(false, "SOURCE_SECURITY_REJECTED")]
    [InlineData(true, "SOURCE_PACKAGE_INVALID")]
    public async Task UnknownPermissionInOriginalManifestIsRejected(bool download, string expectedCode)
    {
        ConfigureSource();
        var offer = CreateOffer("badpermission", "1.0.0", "<h1>工具</h1>");
        offer = offer with
        {
            Package = BuildPackage(
                CreateExportManifest("badpermission", "1.0.0"),
                CreateManifest("badpermission", "1.0.0", permissions: ["unknown-permission"]),
                "<h1>工具</h1>"),
        };
        offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = Hash(offer.Package), SizeBytes = offer.Package.Length } } };
        _handler.AddOffer(offer);
        var context = CreateContext();
        var target = Path.Combine(_root, "saved", "badpermission.qtools");
        var error = await Assert.ThrowsAsync<BridgeException>(async () =>
        {
            if (download)
            {
                var detail = await context.Service.ToolAsync("badpermission", null, CancellationToken.None);
                await context.Service.DownloadAsync(detail, target, CancellationToken.None);
            }
            else
            {
                await context.Service.InstallAsync("badpermission", null, false, null, CancellationToken.None);
            }
        });

        Assert.Equal(expectedCode, error.Code);
        Assert.Contains("未知权限", error.Message);
        Assert.False(context.Tools.Contains("badpermission"));
        Assert.False(File.Exists(target));
        if (Directory.Exists(CacheRoot))
        {
            Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
        }
    }

    [Theory]
    [InlineData("apps/other/app.json")]
    [InlineData("webtools/other/web.json")]
    public async Task PackageWithAppOrWebEntryIsRejected(string extraEntry)
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        offer = offer with
        {
            Package = BuildPackage(CreateExportManifest("calc", "1.0.0"), CreateManifest("calc", "1.0.0"), "<h1>计算器</h1>", extraEntry),
        };
        offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = Hash(offer.Package), SizeBytes = offer.Package.Length } } };
        _handler.AddOffer(offer);
        var context = CreateContext();
        var error = await Assert.ThrowsAsync<BridgeException>(
            () => context.Service.InstallAsync("calc", null, false, null, CancellationToken.None));

        Assert.Equal("SOURCE_PACKAGE_INVALID", error.Code);
        Assert.False(context.Tools.Contains("calc"));
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task UpdatePreservesFilesAndSnapshotsOldAndNewVersions()
    {
        ConfigureSource();
        WriteTool("calc", "1.0.0", "<h1>旧版本</h1>", withFile: true);
        var context = CreateContext();
        var tool = context.Tools.Find("calc");
        Assert.NotNull(tool);
        Directory.CreateDirectory(Path.Combine(tool.Directory, "assets"));
        File.WriteAllText(Path.Combine(tool.Directory, "assets", "app.js"), "old-script");
        File.WriteAllText(Path.Combine(tool.Directory, "assets", "old.css"), "old-style");
        context.Tools.Reload("calc");
        var versionsRoot = ToolVersionStore.VersionsRoot(tool.Directory);
        Directory.CreateDirectory(Path.Combine(versionsRoot, "0.9.0"));
        File.WriteAllText(Path.Combine(versionsRoot, "0.9.0", "index.html"), "<h1>历史版本</h1>");
        Directory.CreateDirectory(Path.Combine(versionsRoot, "2.0.0"));
        File.WriteAllText(Path.Combine(versionsRoot, "2.0.0", "index.html"), "<h1>过期快照</h1>");
        context.Tools.Reload("calc");
        var offer = CreateOffer("calc", "2.0.0", "<h1>新版本</h1>");
        var package = AddPackageFile(offer.Package, "tools/calc/assets/app.js", "new-script");
        offer = offer with { Package = package };
        offer = offer with { Detail = offer.Detail with { Tool = offer.Detail.Tool with { Sha256 = Hash(package), SizeBytes = package.Length } } };
        _handler.AddOffer(offer);

        var result = await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);

        Assert.Equal("updated", result.Action);
        Assert.True(result.Saved);
        var updated = Assert.IsType<LoadedTool>(context.Tools.Find("calc"));
        Assert.Equal("2.0.0", updated.Manifest.Version);
        Assert.Equal("<h1>新版本</h1>", File.ReadAllText(Path.Combine(updated.Directory, "index.html")));
        Assert.Equal("new-script", File.ReadAllText(Path.Combine(updated.Directory, "assets", "app.js")));
        Assert.False(File.Exists(Path.Combine(updated.Directory, "assets", "old.css")));
        Assert.Equal("数据", File.ReadAllText(Path.Combine(updated.Directory, "files", "data.bin")));
        Assert.Equal("<h1>旧版本</h1>", File.ReadAllText(Path.Combine(versionsRoot, "1.0.0", "index.html")));
        Assert.Equal("old-script", File.ReadAllText(Path.Combine(versionsRoot, "1.0.0", "assets", "app.js")));
        Assert.Equal("<h1>新版本</h1>", File.ReadAllText(Path.Combine(versionsRoot, "2.0.0", "index.html")));
        Assert.Equal("new-script", File.ReadAllText(Path.Combine(versionsRoot, "2.0.0", "assets", "app.js")));
        Assert.Equal("<h1>历史版本</h1>", File.ReadAllText(Path.Combine(versionsRoot, "0.9.0", "index.html")));
        Assert.NotEmpty(Directory.GetDirectories(RollbackRoot, "calc", SearchOption.AllDirectories));
        Assert.Equal(1, new UsageStore(Path.Combine(_root, "data", "usage.json")).Get("calc")!.Count);

        var rolledBack = context.Tools.SwitchVersion("calc", "1.0.0");
        Assert.Equal("old-script", File.ReadAllText(Path.Combine(rolledBack.Directory, "assets", "app.js")));
        Assert.True(File.Exists(Path.Combine(rolledBack.Directory, "assets", "old.css")));
        Assert.Equal("数据", File.ReadAllText(Path.Combine(rolledBack.Directory, "files", "data.bin")));
    }

    [Fact]
    public async Task SubmitUploadsCurrentVersionWithoutVersionHistory()
    {
        ConfigureSource();
        WriteTool("calc", "1.2.0", "<h1>当前版本</h1>", withFile: true);
        var context = CreateContext();
        var tool = Assert.IsType<LoadedTool>(context.Tools.Find("calc"));
        var history = Path.Combine(ToolVersionStore.VersionsRoot(tool.Directory), "0.9.0");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "index.html"), "<h1>旧快照</h1>");
        context.Tools.Reload("calc");
        _handler.Submit = _ => Task.FromResult(JsonResponse("""{"id":"submission-1","status":"pending","toolId":"calc","toolName":"计算器","version":"1.2.0"}"""));

        var result = await context.Service.SubmitAsync("tool", "calc", CancellationToken.None);

        Assert.Equal("submission-1", result.Id);
        Assert.Equal("pending", result.Status);
        var request = Assert.Single(_handler.Requests, item => item.Uri.AbsolutePath == "/nested/base/api/v1/submissions");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.NotNull(_handler.SubmittedPackage);
        using var archive = new ZipArchive(new MemoryStream(_handler.SubmittedPackage), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("tools/calc/manifest.json"));
        Assert.NotNull(archive.GetEntry("tools/calc/index.html"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains(".versions", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("tools/calc/files/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1.2.0", ReadExportVersion(archive));
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task InstallWebToolFromSourceAddsOnlineTool()
    {
        ConfigureSource();
        var web = new WebToolEntry("site", "资讯站", "https://news.example/daily", "资讯", "闻",
            "2026-01-01T00:00:00Z", "每日要闻") { Version = "1.2.0" };
        var package = BuildWebPackage(web);
        var manifest = new ToolManifest(web.Id, web.Name, web.Version, web.Description, "", [],
            new ToolWindowSettings(640, 480, true), web.CreatedAt, false, null)
        { Category = web.Category, Glyph = web.Glyph };
        var detail = new SourceToolDetailView(
            new SourceToolView(web.Id, web.Name, web.Version, web.Description, web.Category, web.Glyph, [],
                package.Length, Hash(package), DateTimeOffset.UtcNow, "", "/api/v1/tools/site/package", [web.Version])
            { Kind = "web", Url = web.Url },
            manifest);
        _handler.AddOffer(new SourceOffer(detail, package));
        var context = CreateContext();

        var result = await context.Service.InstallAsync("site", null, false, null, CancellationToken.None);

        Assert.Equal("installed", result.Action);
        Assert.True(result.Saved);
        Assert.Equal("site", result.ToolId);
        var saved = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_root, "webtools", "site", "web.json")),
            AppJsonContext.Default.WebToolEntry);
        Assert.NotNull(saved);
        Assert.Equal("https://news.example/daily", saved!.Url);
        Assert.Equal("1.2.0", saved.Version);
        Assert.Equal(1, new UsageStore(Path.Combine(_root, "data", "usage.json")).Get("site")!.Count);
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task SubmitWebToolUploadsWebPackage()
    {
        ConfigureSource();
        var web = new WebToolEntry("site", "资讯站", "https://news.example/daily", "资讯", "闻",
            "2026-01-01T00:00:00Z", "每日要闻") { Version = "1.3.0" };
        var directory = Path.Combine(_root, "webtools", "site");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "web.json"),
            JsonSerializer.Serialize(web, AppJsonContext.Default.WebToolEntry));
        var context = CreateContext();
        _handler.Submit = _ => Task.FromResult(JsonResponse("""{"id":"submission-w","status":"pending","toolId":"site","toolName":"资讯站","version":"1.3.0"}"""));

        var result = await context.Service.SubmitAsync("web", "site", CancellationToken.None);

        Assert.Equal("submission-w", result.Id);
        Assert.NotNull(_handler.SubmittedPackage);
        using var archive = new ZipArchive(new MemoryStream(_handler.SubmittedPackage), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("webtools/site/web.json"));
        Assert.Null(archive.GetEntry("tools/site/manifest.json"));
        Assert.Equal("web", ReadExportKind(archive));
        Assert.Equal("1.3.0", ReadExportVersion(archive));
        Assert.Empty(Directory.GetFileSystemEntries(CacheRoot));
    }

    [Fact]
    public async Task ConfiguredUrlKeepsSubPathForDetailDownloadAndSubmit()
    {
        ConfigureSource();
        var offer = CreateOffer("calc", "1.0.0", "<h1>计算器</h1>");
        _handler.AddOffer(offer);
        var context = CreateContext();
        await context.Service.ToolAsync("calc", "1.0.0", CancellationToken.None);
        await context.Service.InstallAsync("calc", null, false, null, CancellationToken.None);
        Assert.Contains(_handler.Requests, request => request.Uri.AbsolutePath == "/nested/base/api/v1/tools/calc" && request.Uri.Query.Contains("version=1.0.0", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request => request.Uri.AbsolutePath == "/nested/base/api/v1/tools/calc/package" && request.Uri.Query.Contains("version=1.0.0", StringComparison.Ordinal));

        _handler.Submit = _ => Task.FromResult(JsonResponse("""{"id":"submission-1","status":"pending"}"""));
        await context.Service.SubmitAsync("tool", "calc", CancellationToken.None);
        Assert.Contains(_handler.Requests, request => request.Uri.AbsolutePath == "/nested/base/api/v1/submissions");
    }

    [Fact]
    public async Task AllManagerSourceMethodsDenyOrdinaryTools()
    {
        ConfigureSource();
        WriteTool("ordinary", "1.0.0", "<h1>普通工具</h1>");
        var context = CreateContext();
        var methods = new[]
        {
            "manager.getSourceConfig", "manager.setSourceConfig", "manager.testSource",
            "manager.sourceCatalog", "manager.sourceTool", "manager.sourceInstall",
            "manager.sourceDownload", "manager.sourceSubmit", "manager.sourcePreview",
        };

        foreach (var method in methods)
        {
            var response = await context.Dispatcher.DispatchAsync("ordinary",
                $$"""{ "id": "source-{{method}}", "method": "{{method}}", "toolId": "ordinary", "payload": { "id": "calc", "toolId": "calc", "baseUrl": "https://source.example" } }""");
            using var document = JsonDocument.Parse(response);
            Assert.True(document.RootElement.TryGetProperty("error", out var error), method + " 应返回权限错误");
            Assert.Equal("PERMISSION_DENIED", error.GetProperty("code").GetString());
        }

        Assert.Empty(_handler.Requests);
    }

    private static string? ReadExportVersion(ZipArchive archive) => ReadExport(archive).Version;

    private static string? ReadExportKind(ZipArchive archive) => ReadExport(archive).Kind;

    private static ItemExportManifest ReadExport(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry(ItemPackageService.ManifestEntryName)!.Open());
        return JsonSerializer.Deserialize(reader.ReadToEnd(), AppJsonContext.Default.ItemExportManifest)!;
    }

    private SourceOffer CreateOffer(string id, string version, string html)
    {
        var package = BuildPackage(CreateExportManifest(id, version), CreateManifest(id, version), html);
        return new SourceOffer(new SourceToolDetailView(
            new SourceToolView(id, "计算器", version, "说明", "效率", "算", ["storage"],
                package.Length, Hash(package), DateTimeOffset.UtcNow, "/preview/" + id, "/download/" + id, [version]),
            CreateManifest(id, version)), package);
    }

    private void WriteTool(string id, string version, string html, bool withFile = false)
    {
        var directory = Path.Combine(ToolsRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(CreateManifest(id, version), AppJsonContext.Default.ToolManifest));
        File.WriteAllText(Path.Combine(directory, "index.html"), html);
        if (withFile)
        {
            Directory.CreateDirectory(Path.Combine(directory, "files"));
            File.WriteAllText(Path.Combine(directory, "files", "data.bin"), "数据");
        }
    }

    private static ToolManifest CreateManifest(string id, string version, IReadOnlyList<string>? permissions = null) =>
        new(id, "计算器", version, "说明", "index.html", permissions ?? ["storage"],
            new ToolWindowSettings(640, 480, true), "2026-01-01T00:00:00Z", false, null);

    private static ItemExportManifest CreateExportManifest(string id, string version) =>
        new(1, LauncherListBuilder.KindTool, id, "计算器", version, "2026-01-01T00:00:00Z", "test");

    private static byte[] BuildPackage(ItemExportManifest export, ToolManifest manifest, string html, string? extraEntry = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ItemPackageService.ManifestEntryName,
                JsonSerializer.Serialize(export, AppJsonContext.Default.ItemExportManifest));
            WriteEntry(archive, $"tools/{manifest.Id}/manifest.json",
                JsonSerializer.Serialize(manifest, AppJsonContext.Default.ToolManifest));
            WriteEntry(archive, $"tools/{manifest.Id}/index.html", html);
            if (extraEntry is not null) { WriteEntry(archive, extraEntry, "夹带条目"); }
        }

        return output.ToArray();
    }

    private static byte[] BuildWebPackage(WebToolEntry web)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var export = new ItemExportManifest(1, LauncherListBuilder.KindWeb, web.Id, web.Name, web.Version,
                "2026-01-01T00:00:00Z", "test");
            WriteEntry(archive, ItemPackageService.ManifestEntryName,
                JsonSerializer.Serialize(export, AppJsonContext.Default.ItemExportManifest));
            WriteEntry(archive, $"webtools/{web.Id}/web.json",
                JsonSerializer.Serialize(web, AppJsonContext.Default.WebToolEntry));
        }

        return output.ToArray();
    }

    private static byte[] AddPackageFile(byte[] package, string name, string content)
    {
        using var input = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open();
                using var destination = archive.CreateEntry(entry.FullName).Open();
                source.CopyTo(destination);
            }

            WriteEntry(archive, name, content);
        }

        return output.ToArray();
    }

    private static byte[] ForgeEntryLength(byte[] package, string name, int length)
    {
        for (var i = 0; i + 46 <= package.Length; i++)
        {
            if (package[i] != 0x50 || package[i + 1] != 0x4b || package[i + 2] != 0x01 || package[i + 3] != 0x02) { continue; }
            var nameLength = BitConverter.ToUInt16(package, i + 28);
            if (Encoding.UTF8.GetString(package, i + 46, nameLength) != name) { continue; }
            BitConverter.GetBytes((uint)length).CopyTo(package, i + 24);
            return package;
        }
        throw new InvalidOperationException("测试 ZIP 条目不存在");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record SourceOffer(SourceToolDetailView Detail, byte[] Package)
    {
        internal SourceToolView Tool => Detail.Tool;
    }

    private sealed class SourceTestHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, SourceOffer> _offers = new(StringComparer.Ordinal);
        private readonly List<(Uri Uri, HttpMethod Method)> _requests = [];
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Submit { get; set; } = _ => throw new InvalidOperationException("未配置提交响应");
        internal Func<byte[], HttpContent>? PackageContentFactory { get; set; }
        internal Func<CancellationToken, Task>? PackageGate { get; set; }
        internal byte[]? SubmittedPackage { get; private set; }
        internal IReadOnlyList<(Uri Uri, HttpMethod Method)> Requests => _requests;

        internal void AddOffer(SourceOffer offer) => _offers[offer.Detail.Tool.Id] = offer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            _requests.Add((uri, request.Method));
            if (uri.AbsolutePath.EndsWith("/api/v1/submissions", StringComparison.Ordinal))
            {
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                var file = Assert.Single(multipart);
                SubmittedPackage = await file.ReadAsByteArrayAsync(cancellationToken);
                return await Submit(request).WaitAsync(cancellationToken);
            }

            var offer = _offers.Values.SingleOrDefault(candidate => uri.AbsolutePath.Contains("/tools/" + candidate.Detail.Tool.Id, StringComparison.Ordinal));
            Assert.NotNull(offer);
            if (uri.AbsolutePath.EndsWith("/package", StringComparison.Ordinal))
            {
                if (PackageGate is not null) { await PackageGate(cancellationToken); }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = PackageContentFactory?.Invoke(offer.Package) ?? new ByteArrayContent(offer.Package)
                };
            }

            return JsonResponse(JsonSerializer.Serialize(offer.Detail, AppJsonContext.Default.SourceToolDetailView));
        }
    }

    private sealed class CancelOnEofStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (read == 0) { cancellation.Cancel(); }
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed record SourceTestContext(ToolRegistry Tools, ToolSourceService Service, BridgeDispatcher Dispatcher);
}
