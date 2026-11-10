using System.Diagnostics;
using QTools.Host.Bridge;
using QTools.Host.Logging;

namespace QTools.Host.Launcher;

/// <summary>
/// 在系统文件管理器中定位目标（右键菜单「属性」）。
/// Windows：资源管理器 /select 选中文件；目录直接打开。
/// macOS：open -R 选中；Linux：xdg-open 打开所在目录（无原生选中语义）。
/// </summary>
public static class LauncherRevealer
{
    public static void Reveal(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Directory.Exists(target))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (Directory.Exists(target) && !target.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("open", $"\"{target}\"") { UseShellExecute = false });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("open", $"-R \"{target}\"") { UseShellExecute = false });
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
                if (string.IsNullOrEmpty(directory))
                {
                    throw new BridgeException("REVEAL_FAILED", $"无法定位所在目录：{target}");
                }

                Process.Start(new ProcessStartInfo("xdg-open", $"\"{directory}\"") { UseShellExecute = false });
            }
            else
            {
                throw new BridgeException("REVEAL_FAILED", "当前平台不支持在文件管理器中定位");
            }
        }
        catch (Exception ex) when (ex is not BridgeException)
        {
            Log.Error($"定位失败：{target}", ex);
            throw new BridgeException("REVEAL_FAILED", $"打开所在位置失败：{ex.Message}");
        }
    }
}
