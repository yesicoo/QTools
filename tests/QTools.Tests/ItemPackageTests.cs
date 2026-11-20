using System.IO.Compression;
using System.Text.Json;
using QTools.Host.Backup;
using QTools.Host.Creator;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>单条目导出包（.qtools）：导出当前运行版本、导出包自动识别、导入还原与安全边界。</summary>
public sealed class ItemPackageTests : IDisposable
{
    private readonly string _sourceRoot;
    private readonly string _destRoot;
    private readonly string _exportDir;

    public ItemPackageTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "qtools-pkg-" + Guid.NewGuid().ToString("n")[..8]);
        _sourceRoot = Path.Combine(baseDir, "source");
        _destRoot = Path.Combine(baseDir, "dest");
        _exportDir = Path.Combine(baseDir, "export");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_destRoot);
        Directory.CreateDirectory(_exportDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_sourceRoot)!, recursive: true); } catch { /* 忽略 */ }
    }

    private static ItemPackageService CreateService(string root)
    {
        var tools = new ToolRegistry(Path.Combine(root, "tools"));
        tools.Rescan();
        var apps = new AppRegistry(Path.Combine(root, "apps"));
        apps.Rescan();
        var webTools = new WebToolRegistry(Path.Combine(root, "webtools"));
        webTools.Rescan();

        return new ItemPackageService(
            tools,
            new ToolImporter(tools, Path.Combine(root, "tools")),
            apps,
            new AppStore(apps, apps.AppsDirectory),
            webTools,
            new WebToolStore(webTools, webTools.WebToolsDirectory),
            root,
            Path.Combine(root, "data", "import-staging"));
    }

    private static void WriteTool(
        string root, string id, string name, string version, string html,
        string? glyph = "🧮", bool allowEval = false, bool withFile = false)
    {
        var directory = Path.Combine(root, "tools", id);
        Directory.CreateDirectory(directory);
        var manifest = new ToolManifest(
            id, name, version, "说明", "index.html",
            new[] { "storage" },
            new ToolWindowSettings(640, 480, true),
            "2026-01-01T00:00:00.0000000+00:00",
            allowEval, Csp: null)
        {
            Category = "效率",
            Glyph = glyph,
        };
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, AppJsonContext.Default.ToolManifest));
        File.WriteAllText(Path.Combine(directory, "index.html"), html);

        if (withFile)
        {
            Directory.CreateDirectory(Path.Combine(directory, "files"));
            File.WriteAllText(Path.Combine(directory, "files", "data.bin"), "BINARY");
        }
    }

    private static void WriteApp(string root, string id, string name)
    {
        var directory = Path.Combine(root, "apps", id);
        Directory.CreateDirectory(directory);
        var entry = new AppEntry(
            id, name, @"C:\Tools\demo.exe", "--flag", @"C:\Tools", "开发", "</>",
            "2026-01-01T00:00:00.0000000+00:00");
        File.WriteAllText(
            Path.Combine(directory, "app.json"),
            JsonSerializer.Serialize(entry, AppJsonContext.Default.AppEntry));
    }

    private static void WriteWeb(string root, string id, string name)
    {
        var directory = Path.Combine(root, "webtools", id);
        Directory.CreateDirectory(directory);
        var entry = new WebToolEntry(
            id, name, "https://example.com/news", "资讯", "🌐",
            "2026-01-01T00:00:00.0000000+00:00", "行业资讯站");
        File.WriteAllText(
            Path.Combine(directory, "web.json"),
            JsonSerializer.Serialize(entry, AppJsonContext.Default.WebToolEntry));
    }

    /* ================= 导出 ================= */

    [Fact]
    public void ExportToolContainsCurrentVersionAndSkipsVersionsDirectory()
    {
        WriteTool(_sourceRoot, "calc", "计算器", "1.2.0", "<h1>calc</h1>", withFile: true);
        var service = CreateService(_sourceRoot);
        var target = Path.Combine(_exportDir, "calc.qtools");

        var result = service.Export("tool", "calc", target);

        Assert.Equal("calc.qtools", result.FileName);
        Assert.True(result.SizeBytes > 0);
        using var archive = ZipFile.OpenRead(target);
        Assert.NotNull(archive.GetEntry(ItemPackageService.ManifestEntryName));
        Assert.NotNull(archive.GetEntry("tools/calc/manifest.json"));
        Assert.NotNull(archive.GetEntry("tools/calc/index.html"));
        Assert.NotNull(archive.GetEntry("tools/calc/files/data.bin"));
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains(".versions"));

        var manifest = ItemPackageService.TryReadManifest(target);
        Assert.NotNull(manifest);
        Assert.Equal("tool", manifest!.Kind);
        Assert.Equal("计算器", manifest.Name);
        Assert.Equal("1.2.0", manifest.Version);
    }

    [Fact]
    public void ExportAddsDefaultExtensionAndRejectsPrograms()
    {
        WriteTool(_sourceRoot, "calc", "计算器", "1.0.0", "<h1>calc</h1>");
        var service = CreateService(_sourceRoot);

        var target = Path.Combine(_exportDir, "noext");
        service.Export("tool", "calc", target);
        Assert.True(File.Exists(target + ItemPackageService.FileExtension));

        Assert.Throws<QTools.Host.Bridge.BridgeException>(() => service.Export("program", "idx-1", Path.Combine(_exportDir, "x.qtools")));
        Assert.Throws<QTools.Host.Bridge.BridgeException>(() => service.GetDefaultFileName("program", "idx-1"));
    }

    [Fact]
    public void ExportFileNameSanitizesIllegalCharacters()
    {
        WriteTool(_sourceRoot, "calc", "计算/器:测试", "1.0.0", "<h1>calc</h1>");
        var service = CreateService(_sourceRoot);
        var name = service.GetDefaultFileName("tool", "calc");
        Assert.Equal("计算_器_测试.qtools", name);
    }

    /* ================= 导入 ================= */

    [Fact]
    public async Task ImportToolRoundTripPreservesMetadataAndFiles()
    {
        WriteTool(_sourceRoot, "calc", "计算器", "1.2.0", "<h1>calc</h1>", withFile: true);
        var source = CreateService(_sourceRoot);
        var package = Path.Combine(_exportDir, "calc.qtools");
        source.Export("tool", "calc", package);

        var dest = CreateService(_destRoot);
        var result = await dest.ImportPackageAsync(package, acceptHighSeverity: false, CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Equal("tool", result.Kind);
        Assert.Equal("calc", result.ItemId);

        var tools = new ToolRegistry(Path.Combine(_destRoot, "tools"));
        tools.Rescan();
        var loaded = tools.Find("calc");
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsValid);
        Assert.Equal("1.2.0", loaded.Manifest.Version);
        Assert.Equal("计算器", loaded.Manifest.Name);
        Assert.Equal("效率", loaded.Manifest.Category);
        Assert.Equal("🧮", loaded.Manifest.Glyph);
        Assert.Equal(640, loaded.Manifest.Window.Width);
        Assert.Contains("storage", loaded.Manifest.Permissions);
        Assert.True(File.Exists(Path.Combine(loaded.Directory, "files", "data.bin")));
        // 导出包不含源端 .versions 历史；导入后只可能有本机自动生成的首个版本快照
        Assert.False(File.Exists(Path.Combine(loaded.Directory, ".versions", "0.9.0", "index.html")));
    }

    [Fact]
    public async Task ImportToolTwiceGetsUniqueId()
    {
        WriteTool(_sourceRoot, "calc", "计算器", "1.2.0", "<h1>calc</h1>");
        var source = CreateService(_sourceRoot);
        var package = Path.Combine(_exportDir, "calc.qtools");
        source.Export("tool", "calc", package);

        var dest = CreateService(_destRoot);
        var first = await dest.ImportPackageAsync(package, false, CancellationToken.None);
        var second = await dest.ImportPackageAsync(package, false, CancellationToken.None);

        Assert.Equal("calc", first.ItemId);
        Assert.NotEqual("calc", second.ItemId);

        var tools = new ToolRegistry(Path.Combine(_destRoot, "tools"));
        tools.Rescan();
        Assert.NotNull(tools.Find(first.ItemId));
        Assert.NotNull(tools.Find(second.ItemId));
    }

    [Fact]
    public async Task ImportAppAndWebPackagesRestoreEntries()
    {
        WriteApp(_sourceRoot, "demo", "演示程序");
        WriteWeb(_sourceRoot, "news", "资讯站");
        var source = CreateService(_sourceRoot);

        var appPackage = Path.Combine(_exportDir, "demo.qtools");
        var webPackage = Path.Combine(_exportDir, "news.qtools");
        source.Export("app", "demo", appPackage);
        source.Export("web", "news", webPackage);

        var dest = CreateService(_destRoot);
        var appResult = await dest.ImportPackageAsync(appPackage, false, CancellationToken.None);
        var webResult = await dest.ImportPackageAsync(webPackage, false, CancellationToken.None);

        Assert.True(appResult.Saved);
        Assert.Equal("app", appResult.Kind);
        var apps = new AppRegistry(Path.Combine(_destRoot, "apps"));
        apps.Rescan();
        var app = apps.Find(appResult.ItemId);
        Assert.NotNull(app);
        Assert.Equal("演示程序", app!.Name);
        Assert.Equal(@"C:\Tools\demo.exe", app.Entry.Target);
        Assert.Equal("--flag", app.Entry.Arguments);
        Assert.Equal("开发", app.Entry.Category);
        Assert.Equal("</>", app.Entry.Glyph);

        Assert.True(webResult.Saved);
        Assert.Equal("web", webResult.Kind);
        var webTools = new WebToolRegistry(Path.Combine(_destRoot, "webtools"));
        webTools.Rescan();
        var web = webTools.Find(webResult.ItemId);
        Assert.NotNull(web);
        Assert.Equal("资讯站", web!.Name);
        Assert.Equal("https://example.com/news", web.Entry.Url);
        Assert.Equal("行业资讯站", web.Entry.Description);
        Assert.Equal("资讯", web.Entry.Category);
    }

    [Fact]
    public async Task ImportHighSeverityPackageRequiresExplicitAcceptance()
    {
        WriteTool(_sourceRoot, "evaltool", "动态工具", "1.0.0", "<h1>eval</h1>", allowEval: true);
        var source = CreateService(_sourceRoot);
        var package = Path.Combine(_exportDir, "eval.qtools");
        source.Export("tool", "evaltool", package);

        var dest = CreateService(_destRoot);
        var pending = await dest.ImportPackageAsync(package, false, CancellationToken.None);
        Assert.False(pending.Saved);
        Assert.True(pending.RequiresExplicitAcceptance);
        Assert.Contains(pending.Findings, f => f.Severity == SecuritySeverity.High);
        var beforeAccept = new ToolRegistry(Path.Combine(_destRoot, "tools"));
        beforeAccept.Rescan();
        Assert.Null(beforeAccept.Find("evaltool"));

        var accepted = await dest.ImportPackageAsync(package, true, CancellationToken.None);
        Assert.True(accepted.Saved);
        var tools = new ToolRegistry(Path.Combine(_destRoot, "tools"));
        tools.Rescan();
        Assert.NotNull(tools.Find("evaltool"));
    }

    /* ================= 识别与安全边界 ================= */

    [Fact]
    public void InspectDetectsHtmlAndPackage()
    {
        WriteTool(_sourceRoot, "calc", "计算器", "2.0.0", "<h1>calc</h1>");
        var service = CreateService(_sourceRoot);
        var package = Path.Combine(_exportDir, "calc.qtools");
        service.Export("tool", "calc", package);

        var htmlPath = Path.Combine(_exportDir, "页面.html");
        File.WriteAllText(htmlPath, "<h1>页面</h1>");
        var html = service.Inspect(htmlPath);
        Assert.Equal("html", html.Kind);
        Assert.Equal("页面", html.Name);
        Assert.Equal("<h1>页面</h1>", html.Html);

        var picked = service.Inspect(package);
        Assert.Equal("package", picked.Kind);
        Assert.Equal("tool", picked.ItemKind);
        Assert.Equal("计算器", picked.Name);
        Assert.Equal("2.0.0", picked.Version);
        Assert.Null(picked.Html);
    }

    [Fact]
    public void InspectRejectsUnknownFiles()
    {
        var service = CreateService(_sourceRoot);
        var unknown = Path.Combine(_exportDir, "unknown.txt");
        File.WriteAllText(unknown, "hello");
        var error = Assert.Throws<QTools.Host.Bridge.BridgeException>(() => service.Inspect(unknown));
        Assert.Equal("IMPORT_FILE_INVALID", error.Code);
    }

    [Fact]
    public async Task WholeBackupPackageIsRejectedWithGuidance()
    {
        var backup = Path.Combine(_exportDir, "backup.zip");
        using (var archive = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("backup.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{}");
        }

        var error = await Assert.ThrowsAsync<QTools.Host.Bridge.BridgeException>(
            () => CreateService(_destRoot).ImportPackageAsync(backup, false, CancellationToken.None));
        Assert.Equal("IMPORT_PACKAGE_INVALID", error.Code);
        Assert.Contains("备份", error.Message);
    }

    [Fact]
    public async Task TraversalAndDotEntriesAreRejected()
    {
        foreach (var badEntry in new[] { "tools/x/../../evil.txt", "tools/x/.versions/1.0.0/index.html" })
        {
            var package = Path.Combine(_exportDir, "bad-" + Guid.NewGuid().ToString("n")[..6] + ".qtools");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry(ItemPackageService.ManifestEntryName);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonSerializer.Serialize(
                        new ItemExportManifest(1, "tool", "x", "X", "1.0.0", "2026-01-01T00:00:00Z", "test"),
                        AppJsonContext.Default.ItemExportManifest));
                }

                archive.CreateEntry(badEntry);
            }

            var error = await Assert.ThrowsAsync<QTools.Host.Bridge.BridgeException>(
                () => CreateService(_destRoot).ImportPackageAsync(package, false, CancellationToken.None));
            Assert.Equal("IMPORT_PACKAGE_INVALID", error.Code);
        }
    }

    [Fact]
    public void ValidateArchiveRejectsMissingManifest()
    {
        var package = Path.Combine(_exportDir, "nomanifest.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            archive.CreateEntry("tools/x/index.html");
        }

        var error = Assert.Throws<QTools.Host.Bridge.BridgeException>(
            () => ItemPackageService.ValidateArchive(package));
        Assert.Equal("IMPORT_PACKAGE_INVALID", error.Code);
    }
}