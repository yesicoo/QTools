using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using QTools.Host.Backup;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 本地备份/恢复：打包范围（含 .versions、排除 secrets/logs）、恢复往返、
/// 恢复前安全快照、白名单防 ZipSlip、导入校验、文件名约束。
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-bak-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        _service = new BackupService(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private void Seed(string toolHtml = "<html>original</html>")
    {
        Write("tools/demo/manifest.json", """{"id":"demo"}""");
        Write("tools/demo/index.html", toolHtml);
        Write("tools/demo/.versions/1.0.0/index.html", "<html>v1</html>");
        Write("apps/my-app/app.json", """{"name":"我的程序"}""");
        Write("data/storage/demo/kv.json", """{"k":1}""");
        Write("data/config.json", "{}");
        Write("secrets/ai.key", "top-secret");
        Write("logs/app.log", "log-line");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relative) =>
        File.ReadAllText(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void CreatePacksPayloadIncludingVersionsAndExcludesSecrets()
    {
        Seed();
        var result = _service.Create("第一份备份");

        Assert.Matches("^[a-z0-9][a-z0-9._-]{0,127}\\.zip$", result.Name);
        Assert.True(result.SizeBytes > 0);

        using var archive = ZipFile.OpenRead(Path.Combine(_service.BackupsDirectory, result.Name));
        var names = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("backup.json", names);
        Assert.Contains("tools/demo/index.html", names);
        Assert.Contains("tools/demo/.versions/1.0.0/index.html", names);
        Assert.Contains("apps/my-app/app.json", names);
        Assert.Contains("data/storage/demo/kv.json", names);
        Assert.Contains("data/config.json", names);
        Assert.DoesNotContain(names, n => n.StartsWith("secrets/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("logs/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("backups/", StringComparison.Ordinal));

        var list = _service.List();
        var entry = Assert.Single(list);
        Assert.Equal(result.Name, entry.Name);
        Assert.Equal("第一份备份", entry.Note);
    }

    [Fact]
    public void RestoreRoundTripCreatesSafetySnapshotAndCanUndo()
    {
        Seed();
        var backup = _service.Create(null);

        // 破坏当前状态：改文件 + 删文件
        Write("tools/demo/index.html", "<html>modified</html>");
        File.Delete(Path.Combine(_root, "apps", "my-app", "app.json"));

        var restored = _service.Restore(backup.Name);
        Assert.True(restored.Ok);
        Assert.False(string.IsNullOrEmpty(restored.SafetyBackupName));
        Assert.Equal("<html>original</html>", Read("tools/demo/index.html"));
        Assert.True(File.Exists(Path.Combine(_root, "apps", "my-app", "app.json")));
        Assert.Equal(2, _service.List().Count);   // 原备份 + 恢复前安全快照

        // 用安全快照回滚到「恢复前」的状态
        var undo = _service.Restore(restored.SafetyBackupName);
        Assert.True(undo.Ok);
        Assert.Equal("<html>modified</html>", Read("tools/demo/index.html"));
    }

    [Fact]
    public void DeleteRemovesBackupAndReportsMissing()
    {
        Seed();
        var backup = _service.Create(null);
        Assert.True(_service.Delete(backup.Name));
        Assert.Empty(_service.List());
        Assert.False(_service.Delete(backup.Name));
    }

    [Theory]
    [InlineData("tools/a/index.html", true)]
    [InlineData("apps/x/app.json", true)]
    [InlineData("data/storage/t/k.json", true)]
    [InlineData("data/config.json", true)]
    [InlineData("backup.json", true)]
    [InlineData("tools/demo/.versions/1.0.0/index.html", true)]
    [InlineData("../evil.txt", false)]
    [InlineData("tools/../../evil.txt", false)]
    [InlineData("tools\\..\\evil.txt", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/windows/evil.txt", false)]
    [InlineData("secrets/ai.key", false)]
    [InlineData("data/native/x.dll", false)]
    [InlineData("tools//a.html", false)]
    [InlineData("tools/./a.html", false)]
    [InlineData("", false)]
    public void EntryWhitelistBlocksZipSlip(string entry, bool allowed)
    {
        Assert.Equal(allowed, BackupService.IsAllowedEntry(entry));
    }

    [Fact]
    public void RestoreRejectsArchiveWithTraversalEntry()
    {
        Directory.CreateDirectory(_service.BackupsDirectory);
        var malicious = Path.Combine(_service.BackupsDirectory, "evil.zip");
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
        {
            archive.CreateEntry("backup.json");
            archive.CreateEntry("../evil.txt");
        }

        var ex = Assert.Throws<BridgeException>(() => _service.Restore("evil.zip"));
        Assert.Equal("BACKUP_INVALID", ex.Code);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.txt")));
    }

    [Fact]
    public void RestoreRejectsArchiveWithoutManifest()
    {
        Directory.CreateDirectory(_service.BackupsDirectory);
        var path = Path.Combine(_service.BackupsDirectory, "nomanifest.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("tools/a/index.html");
        }

        var ex = Assert.Throws<BridgeException>(() => _service.Restore("nomanifest.zip"));
        Assert.Equal("BACKUP_INVALID", ex.Code);
    }

    [Theory]
    [InlineData("../evil.zip")]
    [InlineData("UPPER.ZIP")]
    [InlineData("a b.zip")]
    [InlineData("")]
    public void RestoreRejectsInvalidNames(string name)
    {
        var ex = Assert.Throws<BridgeException>(() => _service.Restore(name));
        Assert.Equal("BACKUP_NAME_INVALID", ex.Code);
    }

    [Fact]
    public void ImportFileValidatesAndRegistersExternalBackup()
    {
        Seed();
        var created = _service.Create(null);
        var external = Path.Combine(_root, "external.zip");
        File.Copy(Path.Combine(_service.BackupsDirectory, created.Name), external);
        _service.Delete(created.Name);

        var name = _service.ImportFile(external);
        Assert.Equal("external.zip", name);
        Assert.Single(_service.List());

        var ex = Assert.Throws<BridgeException>(() => _service.ImportFile(Path.Combine(_root, "missing.zip")));
        Assert.Equal("BACKUP_FILE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void ImportRejectsMaliciousArchive()
    {
        var malicious = Path.Combine(_root, "malicious.zip");
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
        {
            archive.CreateEntry("backup.json");
            archive.CreateEntry("../../evil.txt");
        }

        var ex = Assert.Throws<BridgeException>(() => _service.ImportFile(malicious));
        Assert.Equal("BACKUP_INVALID", ex.Code);
        Assert.Empty(_service.List());
    }
}

/// <summary>
/// Git 远程备份：地址校验（防参数注入）、输出脱敏（凭证不进日志/页面）、
/// 真实 git 推送/恢复往返（本机无 git 时自动跳过）、取消语义。
/// </summary>
public sealed class GitBackupTests : IDisposable
{
    private readonly string _root;
    private readonly BackupService _backup;
    private readonly GitBackup _git;

    public GitBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-git-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        _backup = new BackupService(_root);
        _git = new GitBackup(_backup, _root, Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRequiresUrl(string? url)
    {
        var ex = Assert.Throws<BridgeException>(() => GitBackup.ValidateRemoteUrl(url));
        Assert.Equal("GIT_URL_REQUIRED", ex.Code);
    }

    [Theory]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("--upload-pack=evil")]
    [InlineData("https://example.com/\nrepo.git")]
    public void ValidateRejectsInjectionShapes(string url)
    {
        var ex = Assert.Throws<BridgeException>(() => GitBackup.ValidateRemoteUrl(url));
        Assert.Equal("GIT_URL_INVALID", ex.Code);
    }

    [Fact]
    public void ValidateRejectsOverlongUrl()
    {
        var ex = Assert.Throws<BridgeException>(() => GitBackup.ValidateRemoteUrl(new string('a', 501)));
        Assert.Equal("GIT_URL_INVALID", ex.Code);
    }

    [Theory]
    [InlineData("https://github.com/user/repo.git")]
    [InlineData("git@github.com:user/repo.git")]
    [InlineData("ssh://git@host/repo.git")]
    [InlineData("C:\\backups\\repo.git")]
    public void ValidateAcceptsCommonForms(string url)
    {
        GitBackup.ValidateRemoteUrl(url);
    }

    [Fact]
    public void SanitizeStripsCredentialUserinfo()
    {
        var input = "fatal: unable to access 'https://user:glpat-secret123@github.com/x/repo.git/': error";
        var cleaned = GitBackup.Sanitize(input);
        Assert.DoesNotContain("glpat-secret123", cleaned);
        Assert.DoesNotContain("user:", cleaned);
        Assert.Contains("https://***@github.com", cleaned);
    }

    [Fact]
    public void SanitizeTruncatesLongOutput()
    {
        var cleaned = GitBackup.Sanitize(new string('x', 900));
        Assert.True(cleaned.Length <= 501);
        Assert.EndsWith("…", cleaned);
    }

    private static bool GitAvailable()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start);
            process!.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start);
        process!.WaitForExit(30000);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} 失败：{process.StandardError.ReadToEnd()}");
    }

    private void Seed()
    {
        var toolDir = Path.Combine(_root, "tools", "demo");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "index.html"), "<html>git-original</html>");
        var storageDir = Path.Combine(_root, "data", "storage", "demo");
        Directory.CreateDirectory(storageDir);
        File.WriteAllText(Path.Combine(storageDir, "kv.json"), """{"k":1}""");
    }

    [Fact]
    public async Task PushAndRestoreRoundTripAgainstLocalBareRepo()
    {
        if (!GitAvailable()) { return; }   // 无 git 的环境跳过（真机验收覆盖）
        Seed();
        var bare = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(bare);
        RunGit(bare, "init", "--bare");

        var progress = new List<string>();
        var pushed = await _git.PushAsync(bare, CancellationToken.None, progress.Add);
        Assert.True(pushed.Pushed);
        Assert.True(pushed.Committed);
        Assert.NotEmpty(progress);

        // 无变化时不重复提交
        var again = await _git.PushAsync(bare, CancellationToken.None);
        Assert.False(again.Pushed);
        Assert.False(again.Committed);

        // 破坏当前状态后从远程恢复
        File.WriteAllText(Path.Combine(_root, "tools", "demo", "index.html"), "<html>broken</html>");
        File.Delete(Path.Combine(_root, "data", "storage", "demo", "kv.json"));

        var restored = await _git.RestoreAsync(bare, CancellationToken.None, progress.Add);
        Assert.True(restored.Ok);
        Assert.False(string.IsNullOrEmpty(restored.SafetyBackupName));
        Assert.Equal("<html>git-original</html>",
            File.ReadAllText(Path.Combine(_root, "tools", "demo", "index.html")));
        Assert.True(File.Exists(Path.Combine(_root, "data", "storage", "demo", "kv.json")));
        Assert.NotEmpty(_backup.List());   // Git 恢复快照 + 恢复前安全快照
    }

    [Fact]
    public async Task RestoreRejectsRepoWithoutPayload()
    {
        if (!GitAvailable()) { return; }
        var bare = Path.Combine(_root, "empty.git");
        Directory.CreateDirectory(bare);
        RunGit(bare, "init", "--bare");
        // 空仓库克隆会失败（无 HEAD）；改为建一个只有 README 的仓库
        var work = Path.Combine(_root, "empty-work");
        Directory.CreateDirectory(work);
        RunGit(work, "init");
        File.WriteAllText(Path.Combine(work, "README.md"), "not a backup");
        RunGit(work, "add", "-A");
        RunGit(work, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        RunGit(work, "push", bare, "HEAD");

        var ex = await Assert.ThrowsAsync<BridgeException>(() => _git.RestoreAsync(bare, CancellationToken.None));
        Assert.Equal("GIT_RESTORE_INVALID", ex.Code);
    }

    [Fact]
    public async Task PushHonorsCancellation()
    {
        if (!GitAvailable()) { return; }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Assert.ThrowsAsync<BridgeException>(() => _git.PushAsync("https://example.com/repo.git", cts.Token));
        Assert.Equal("REQUEST_CANCELLED", ex.Code);
    }
}

/// <summary>
/// 分发器流式通道与取消：gitPush 的进度信封与最终结果共享请求 id；
/// manager.cancelRequest 对未知 id 返回 ok=false；hostOnly 边界不被工具越过。
/// AppPaths 是进程全局状态：与 AI 配置测试同集合串行。
/// </summary>
[Collection("AI 配置串行")]
public sealed class StreamAndCancelTests : IDisposable
{
    private readonly string _root;
    private readonly string _toolsDir;
    private readonly ToolRegistry _registry;
    private readonly BridgeDispatcher _dispatcher;
    private readonly BackupService _backup;
    private readonly GitBackup _git;

    public StreamAndCancelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-stream-" + Guid.NewGuid().ToString("n")[..8]);
        _toolsDir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(_toolsDir);
        AppPaths.Initialize(_root);
        _registry = new ToolRegistry(_toolsDir);
        _backup = new BackupService(_root);
        _git = new GitBackup(_backup, _root, Path.Combine(_root, "data"));
        _dispatcher = new BridgeDispatcher(
            _registry, new ToolStorage(Path.Combine(_root, "data", "storage")),
            backup: _backup, gitBackup: _git);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private static string Envelope(string id, string method, object payload, string toolId = "__host__")
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["toolId"] = toolId,
            ["method"] = method,
            ["payload"] = payload,
        });
    }

    private async Task<JsonElement> DispatchAsync(string raw, Action<string>? push = null)
    {
        var response = await _dispatcher.DispatchAsync(BridgeDispatcher.HostToolId, raw, push);
        using var document = JsonDocument.Parse(response);
        return document.RootElement.Clone();
    }

    private static bool GitAvailable()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start);
            process!.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task GitPushStreamsEnvelopesSharingRequestId()
    {
        if (!GitAvailable()) { return; }
        var toolDir = Path.Combine(_toolsDir, "demo");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "index.html"), "<html>stream</html>");

        var bare = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(bare);
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = bare,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("init");
        start.ArgumentList.Add("--bare");
        using (var process = Process.Start(start)) { process!.WaitForExit(30000); }

        var envelopes = new List<string>();
        var reply = await DispatchAsync(
            Envelope("req-stream-1", "manager.gitPush", new { gitRemoteUrl = bare }), envelopes.Add);

        Assert.True(reply.TryGetProperty("result", out var result), reply.GetRawText());
        Assert.True(result.GetProperty("pushed").GetBoolean());
        Assert.NotEmpty(envelopes);

        foreach (var raw in envelopes)
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            Assert.Equal("req-stream-1", root.GetProperty("id").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("stream").GetProperty("delta").GetString()));
        }
    }

    [Fact]
    public async Task CancelRequestReportsFalseForUnknownId()
    {
        Assert.False(_dispatcher.CancelPendingRequest("no-such-request"));
        var reply = await DispatchAsync(Envelope("req-cancel-1", "manager.cancelRequest", new { id = "no-such-request" }));
        Assert.True(reply.TryGetProperty("result", out var result), reply.GetRawText());
        Assert.False(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ToolsCannotCallBackupOrCancelMethods()
    {
        var toolDir = Path.Combine(_toolsDir, "nosy");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "manifest.json"),
            """{"id":"nosy","name":"nosy","version":"1.0.0","description":"d","entry":"index.html","permissions":["storage"],"window":{"width":400,"height":400,"resizable":true},"createdAt":"2026-11-04T00:00:00+08:00","allowEval":false,"csp":null}""");
        File.WriteAllText(Path.Combine(toolDir, "index.html"), "<html></html>");
        _registry.Rescan();

        foreach (var method in new[] { "manager.listBackups", "manager.createBackup", "manager.gitPush", "manager.cancelRequest", "manager.setHotkeyEnabled" })
        {
            var response = await _dispatcher.DispatchAsync("nosy", Envelope("req-x", method, new { }, "nosy"));
            using var document = JsonDocument.Parse(response);
            Assert.True(document.RootElement.TryGetProperty("error", out var error), response);
            Assert.Equal("PERMISSION_DENIED", error.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task BackupConfigViewExposesNoCredentials()
    {
        var reply = await DispatchAsync(Envelope("req-cfg", "manager.getBackupConfig", new { }));
        Assert.True(reply.TryGetProperty("result", out var result), reply.GetRawText());
        Assert.True(result.TryGetProperty("hotkeySupported", out _));
        Assert.True(result.TryGetProperty("gitRemoteUrl", out _));
        Assert.DoesNotContain("apiKey", reply.GetRawText());
        Assert.DoesNotContain("secret", reply.GetRawText());
    }
}