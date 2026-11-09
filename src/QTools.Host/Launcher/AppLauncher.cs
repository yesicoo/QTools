using System.Diagnostics;
using System.Text;
using QTools.Host.Bridge;
using QTools.Host.Logging;

namespace QTools.Host.Launcher;

/// <summary>启动结果。</summary>
public sealed record LaunchResult(string AppId, string AppName, bool Started, int? ProcessId, string? Message);

/// <summary>
/// 本机程序启动器（跨平台）。
/// <para>
/// Windows：.exe / .lnk / .bat 等直接用 ShellExecute 启动。
/// macOS：.app 用 <c>open</c>；其他直接执行。
/// Linux：.desktop 解析 Exec= 后执行；其他直接执行。
/// </para>
/// </summary>
public sealed class AppLauncher
{
    /// <summary>启动一个启动项。</summary>
    public LaunchResult Launch(LoadedApp app)
    {
        if (!app.IsValid)
        {
            throw new BridgeException(
                "APP_INVALID",
                $"启动项配置无效：{string.Join("；", app.Errors)}");
        }

        var target = app.Entry.Target.Trim();
        var arguments = app.Entry.Arguments?.Trim() ?? string.Empty;
        var workingDirectory = app.Entry.WorkingDirectory?.Trim() ?? string.Empty;

        // 预检：目标不存在时给出明确错误，而不是让 Process.Start 抛难懂的 Win32 异常
        if (!app.TargetExists)
        {
            Log.Warn($"启动失败：目标不存在 {target}");
            throw new BridgeException("APP_TARGET_NOT_FOUND", $"找不到目标程序：{target}");
        }

        try
        {
            var startInfo = BuildStartInfo(target, arguments, workingDirectory);

            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            var process = Process.Start(startInfo);
            var pid = TryGetPid(process);

            Log.Info($"已启动本机程序 {app.Id}（{app.Name}）→ {target}" +
                     (pid is null ? "" : $" PID={pid}"));

            return new LaunchResult(app.Id, app.Name, true, pid, null);
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            Log.Error($"启动本机程序失败 {app.Id}", ex);
            throw new BridgeException("APP_LAUNCH_FAILED", $"启动失败：{ex.Message}");
        }
    }

    private static ProcessStartInfo BuildStartInfo(string target, string arguments, string workingDirectory)
    {
        // macOS：.app 是目录，必须用 open
        if (OperatingSystem.IsMacOS() && target.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            var args = string.IsNullOrEmpty(arguments)
                ? $"-a \"{target}\""
                : $"-a \"{target}\" --args {arguments}";
            return new ProcessStartInfo("open", args) { UseShellExecute = false };
        }

        // Linux：.desktop 需要解析 Exec=
        if (OperatingSystem.IsLinux() && target.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
        {
            return BuildFromDesktopFile(target);
        }

        return new ProcessStartInfo(target, arguments) { UseShellExecute = true };
    }

    private static ProcessStartInfo BuildFromDesktopFile(string desktopPath)
    {
        string? exec = null;
        string? name = null;

        foreach (var line in File.ReadLines(desktopPath))
        {
            if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
            {
                exec = line["Exec=".Length..].Trim();
            }
            else if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && name is null)
            {
                name = line["Name=".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(exec))
        {
            throw new BridgeException("APP_INVALID", $".desktop 文件缺少 Exec= 行：{desktopPath}");
        }

        // 去掉桌面 entry 的字段码（%f %U %u 等）
        var cleaned = new StringBuilder();
        foreach (var part in exec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith('%')) { continue; }
            if (cleaned.Length > 0) { cleaned.Append(' '); }
            cleaned.Append(part);
        }

        var final = cleaned.ToString().Trim();
        var space = final.IndexOf(' ');
        var file = space < 0 ? final : final[..space];
        var args = space < 0 ? string.Empty : final[(space + 1)..];

        return new ProcessStartInfo(file, args) { UseShellExecute = false };
    }

    private static int? TryGetPid(Process? process)
    {
        try
        {
            return process is null || process.HasExited ? null : process.Id;
        }
        catch
        {
            return null;
        }
    }
}
