using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using QTools.Host.Bridge;
using QTools.Host.Logging;

namespace QTools.Host.Backup;

/// <summary>manager.gitPush 结果。</summary>
public sealed record GitPushResult(bool Pushed, bool Committed, string Message);

/// <summary>
/// Git 远程备份：把用户数据镜像到 data/backup-repo 工作仓库，调用本机 git 命令提交并推送；
/// 恢复时浅克隆远程仓库 → 固化成本地快照包 → 走 <see cref="BackupService.Restore"/> 的安全恢复路径。
/// 凭证完全由 git 自身管理（ credential manager / ssh-agent），宿主不接触、不存储、不记录。
/// </summary>
public sealed partial class GitBackup
{
    private const int GitTimeoutSeconds = 120;
    private const int TransferTimeoutSeconds = 300;

    private readonly BackupService _backup;
    private readonly string _root;
    private readonly string _dataDirectory;

    public GitBackup(BackupService backup, string rootDirectory, string dataDirectory)
    {
        _backup = backup;
        _root = Path.GetFullPath(rootDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    private string RepoDirectory => Path.Combine(_dataDirectory, "backup-repo");

    /// <summary>镜像当前用户数据 → 提交 → 推送到远程仓库。onProgress 收到阶段性进度文本（可流式推给页面）。</summary>
    public async Task<GitPushResult> PushAsync(string remoteUrl, CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        ValidateRemoteUrl(remoteUrl);
        remoteUrl = remoteUrl.Trim();

        Directory.CreateDirectory(_dataDirectory);
        onProgress?.Invoke("检查 git 环境…\n");
        await EnsureGitAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(RepoDirectory);

        if (!Directory.Exists(Path.Combine(RepoDirectory, ".git")))
        {
            onProgress?.Invoke("初始化本地备份仓库…\n");
            var init = await RunGitAsync(["init", "-b", "main"], RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (init.ExitCode != 0)
            {
                // 旧版 git 不支持 -b：退回普通 init，提交后再重命名分支
                init = await RunGitAsync(["init"], RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (init.ExitCode != 0)
                {
                    throw new BridgeException("GIT_INIT_FAILED", $"初始化备份仓库失败：{Sanitize(init.Stderr)}");
                }
            }
        }

        await EnsureIdentityAsync(cancellationToken).ConfigureAwait(false);
        await EnsureRemoteAsync(remoteUrl, cancellationToken).ConfigureAwait(false);

        onProgress?.Invoke("同步用户数据到备份仓库…\n");
        SyncMirror();
        await CheckedGitAsync(["add", "-A"], "暂存备份内容", cancellationToken).ConfigureAwait(false);

        var status = await RunGitAsync(["status", "--porcelain"], RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (status.ExitCode == 0 && status.Stdout.Trim().Length == 0)
        {
            Log.Info("Git 备份：数据没有变化，跳过提交");
            return new GitPushResult(Pushed: false, Committed: false, "数据没有变化，无需推送");
        }

        onProgress?.Invoke("提交变更…\n");
        await CheckedGitAsync(
            ["commit", "-m", $"QTools 备份 {DateTime.Now:yyyy-MM-dd HH:mm:ss}"],
            "提交备份", cancellationToken).ConfigureAwait(false);

        // 统一分支名为 main（兼容旧版 git 默认 master 的情况）
        var branch = await RunGitAsync(["branch", "-M", "main"], RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (branch.ExitCode != 0)
        {
            Log.Warn($"Git 备份分支重命名失败（退出码 {branch.ExitCode}），尝试按当前分支推送");
        }

        onProgress?.Invoke("推送到远程仓库（凭证由 git 自身管理）…\n");
        var push = await RunGitAsync(["push", "-u", "origin", "HEAD"], RepoDirectory, TransferTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (push.ExitCode != 0)
        {
            throw new BridgeException("GIT_PUSH_FAILED", $"推送到远程仓库失败（退出码 {push.ExitCode}）：{Sanitize(push.Stderr)}");
        }

        Log.Info("Git 备份：已提交并推送到远程仓库");
        return new GitPushResult(Pushed: true, Committed: true, "已提交并推送到远程仓库");
    }

    /// <summary>从远程仓库恢复：浅克隆 → 本地快照包 → 安全恢复（恢复前自动快照当前状态）。</summary>
    public async Task<BackupRestoreResult> RestoreAsync(string remoteUrl, CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        ValidateRemoteUrl(remoteUrl);
        remoteUrl = remoteUrl.Trim();

        Directory.CreateDirectory(_dataDirectory);
        onProgress?.Invoke("检查 git 环境…\n");
        await EnsureGitAsync(cancellationToken).ConfigureAwait(false);

        var staging = Path.Combine(_dataDirectory, ".git-restore-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            onProgress?.Invoke("克隆远程仓库（浅克隆）…\n");

            // 本地新建的 bare 仓库 HEAD 常指向未出生的默认分支（如 master），此时普通 clone 会当作空仓库、
            // 一个分支都不 fetch。先用 ls-remote 直接问远程有哪些分支，再显式 --branch 检出：
            // 优先 main，其次 master，再退而取第一个分支。
            var heads = await RunGitAsync(
                ["ls-remote", "--heads", "--", remoteUrl],
                _dataDirectory, TransferTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (heads.ExitCode != 0)
            {
                throw new BridgeException("GIT_CLONE_FAILED", $"读取远程仓库分支失败（退出码 {heads.ExitCode}）：{Sanitize(heads.Stderr)}");
            }

            var branchNames = heads.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('\t', 2)[^1].Trim())
                .Where(reference => reference.StartsWith("refs/heads/", StringComparison.Ordinal))
                .Select(reference => reference["refs/heads/".Length..])
                .ToArray();
            var branch = branchNames.FirstOrDefault(b => b == "main")
                ?? branchNames.FirstOrDefault(b => b == "master")
                ?? branchNames.FirstOrDefault();
            if (branch is null)
            {
                throw new BridgeException("GIT_RESTORE_INVALID", "远程仓库是空的（需先用「推送到 Git」建立备份）");
            }

            var clone = await RunGitAsync(
                ["clone", "--depth", "1", "--branch", branch, "--", remoteUrl, staging],
                _dataDirectory, TransferTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (clone.ExitCode != 0)
            {
                throw new BridgeException("GIT_CLONE_FAILED", $"克隆远程仓库失败（退出码 {clone.ExitCode}）：{Sanitize(clone.Stderr)}");
            }

            var hasPayload =
                Directory.Exists(Path.Combine(staging, "tools")) ||
                Directory.Exists(Path.Combine(staging, "apps")) ||
                Directory.Exists(Path.Combine(staging, "data", "storage")) ||
                File.Exists(Path.Combine(staging, "data", "config.json"));
            if (!hasPayload)
            {
                throw new BridgeException("GIT_RESTORE_INVALID", "远程仓库中没有 QTools 备份数据（需先用「推送到 Git」建立备份）");
            }

            // 先固化成本地快照包：既留档，又复用同一套白名单校验与恢复前安全快照
            onProgress?.Invoke("固化快照并恢复（恢复前自动备份当前状态）…\n");
            var snapshot = _backup.CreateFromDirectory(staging, "Git 恢复快照");
            return _backup.Restore(snapshot.Name);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>仓库地址校验：允许 https/ssh/git@/本地路径；拒绝控制字符与 '-' 开头（防参数注入）。</summary>
    internal static void ValidateRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new BridgeException("GIT_URL_REQUIRED", "请先填写 Git 仓库地址");
        }

        var trimmed = url.Trim();
        if (trimmed.Length > 500 || trimmed.StartsWith('-') || trimmed.Any(char.IsControl))
        {
            throw new BridgeException("GIT_URL_INVALID", "Git 仓库地址不合法（不超过 500 字符，不能以 - 开头或含控制字符）");
        }
    }

    private async Task EnsureGitAsync(CancellationToken cancellationToken)
    {
        var version = await RunGitAsync(["--version"], _dataDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (version.ExitCode != 0)
        {
            throw new BridgeException("GIT_NOT_FOUND", "git 命令执行失败，请确认已安装 Git 且可在命令行中使用");
        }
    }

    private async Task EnsureIdentityAsync(CancellationToken cancellationToken)
    {
        var name = await RunGitAsync(["config", "user.name"], RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (name.ExitCode != 0 || name.Stdout.Trim().Length == 0)
        {
            await CheckedGitAsync(["config", "user.name", "QTools Backup"], "设置仓库提交身份", cancellationToken).ConfigureAwait(false);
        }

        var email = await RunGitAsync(["config", "user.email"], RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (email.ExitCode != 0 || email.Stdout.Trim().Length == 0)
        {
            await CheckedGitAsync(["config", "user.email", "qtools@localhost"], "设置仓库提交邮箱", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureRemoteAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var current = await RunGitAsync(["remote", "get-url", "origin"], RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (current.ExitCode != 0)
        {
            await CheckedGitAsync(["remote", "add", "origin", remoteUrl], "登记远程仓库", cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(current.Stdout.Trim(), remoteUrl, StringComparison.Ordinal))
        {
            await CheckedGitAsync(["remote", "set-url", "origin", remoteUrl], "更新远程仓库地址", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>把当前用户数据同步进镜像仓库（保留 .git，其余整体替换）。</summary>
    private void SyncMirror()
    {
        foreach (var directory in Directory.EnumerateDirectories(RepoDirectory))
        {
            if (!string.Equals(Path.GetFileName(directory), ".git", StringComparison.Ordinal))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        foreach (var file in Directory.EnumerateFiles(RepoDirectory))
        {
            File.Delete(file);
        }

        foreach (var relative in BackupService.EnumeratePayloadFiles(_root))
        {
            var source = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            var destination = Path.Combine(RepoDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Git 备份跳过无法读取的文件 {relative}：{ex.Message}");
            }
        }
    }

    private async Task CheckedGitAsync(string[] args, string action, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(args, RepoDirectory, GitTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new BridgeException("GIT_COMMAND_FAILED", $"{action}失败（退出码 {result.ExitCode}）：{Sanitize(result.Stderr)}");
        }
    }

    private sealed record GitResult(int ExitCode, string Stdout, string Stderr);

    private static async Task<GitResult> RunGitAsync(
        string[] args, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // 禁止 git 弹交互式凭证提示（无界面进程会卡死）；凭证走 git 自身的 credential manager / ssh-agent
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new BridgeException("GIT_NOT_FOUND", "未检测到 git 命令：请先安装 Git 并确保其在 PATH 中");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new GitResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception) { }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new BridgeException("REQUEST_CANCELLED", "Git 操作已取消");
            }

            throw new BridgeException("GIT_TIMEOUT", $"Git 操作超时（{timeoutSeconds}s），请检查网络或仓库地址");
        }
    }

    /// <summary>git 输出可能回显带凭证的 URL（https://user:token@host）——统一打掉 userinfo 再进日志/页面。</summary>
    internal static string Sanitize(string text)
    {
        var cleaned = CredentialUrlPattern().Replace(text ?? string.Empty, "$1***@");
        cleaned = cleaned.Trim();
        return cleaned.Length > 500 ? cleaned[..500] + "…" : cleaned;
    }

    [GeneratedRegex(@"(https?://)[^/@\s]+@")]
    private static partial Regex CredentialUrlPattern();
}