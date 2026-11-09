using QTools.Host.Logging;

namespace QTools.Host.Launcher;

/// <summary>扫描到的本机程序候选（供用户挑选后添加）。</summary>
public sealed record DiscoveredProgram(
    string Name,
    string Target,
    string Source,
    string? Category);

/// <summary>
/// 扫描本机已安装程序，供「添加程序」时挑选。
/// <para>
/// Windows：开始菜单（含所有用户）+ 常见安装目录的 .exe/.lnk。
/// macOS：/Applications 与 ~/Applications 的 .app。
/// Linux：/usr/share/applications 与 ~/.local/share/applications 的 .desktop。
/// </para>
/// </summary>
public sealed class ProgramDiscovery
{
    private const int MaxResults = 400;

    public IReadOnlyList<DiscoveredProgram> Scan()
    {
        var candidates = new List<DiscoveredProgram>();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                ScanWindows(candidates);
            }
            else if (OperatingSystem.IsMacOS())
            {
                ScanMacOs(candidates);
            }
            else if (OperatingSystem.IsLinux())
            {
                ScanLinux(candidates);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"扫描本机程序失败：{ex.Message}");
        }

        return DedupePrograms(candidates)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToArray();
    }

    /// <summary>
    /// 跨根目录去重（纯函数，容忍乱序输入）：同一程序在「所有用户菜单 / 用户菜单 / 桌面 /
    /// 顶层+厂商子目录」常各有一份 .lnk——按解析出的真实目标路径合并，只留最先出现的
    /// （扫描顺序：所有用户菜单 → 用户菜单 → 桌面，顶层优先于子目录）。
    /// 解析失败（MSI 广告式快捷方式等）退化为按快捷方式名合并。
    /// 同时过滤掉指向非 exe 的快捷方式（帮助文档 chm、网站 url、文件夹等，对启动器没价值）。
    /// </summary>
    public static IReadOnlyList<DiscoveredProgram> DedupePrograms(IEnumerable<DiscoveredProgram> candidates)
    {
        var results = new List<DiscoveredProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var program in candidates)
        {
            var target = program.Target.Trim();
            if (target.Length == 0)
            {
                continue;
            }

            string key;
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = LnkTargetResolver.TryResolveLocalBasePath(target);
                if (resolved is not null)
                {
                    if (!resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    key = "t:" + resolved;
                }
                else
                {
                    key = "n:" + Path.GetFileNameWithoutExtension(target);
                }
            }
            else
            {
                key = "t:" + target;
            }

            if (seen.Add(key))
            {
                results.Add(program with { Target = target, Name = program.Name.Trim() });
            }
        }

        return results;
    }

    /* ---------------- Windows ---------------- */

    private static void ScanWindows(List<DiscoveredProgram> results)
    {
        var roots = new List<string>();

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            roots.Add(Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs"));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            roots.Add(Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs"));
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { continue; }
            CollectWindowsDirectory(root, results, "开始菜单", maxDepth: 3);
        }

        // 桌面快捷方式也算常用入口（排在最后：与开始菜单重复时优先留开始菜单）
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
        {
            CollectWindowsDirectory(desktop, results, "桌面", maxDepth: 1);
        }
    }

    private static void CollectWindowsDirectory(
        string root, List<DiscoveredProgram> results, string source, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { continue; }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new DiscoveredProgram(
                    Path.GetFileNameWithoutExtension(file),
                    file,
                    source,
                    null));
            }

            if (depth >= maxDepth) { continue; }

            string[] directories;
            try { directories = Directory.GetDirectories(current); }
            catch { continue; }

            foreach (var directory in directories)
            {
                stack.Push((directory, depth + 1));
            }
        }
    }

    /* ---------------- macOS ---------------- */

    private static void ScanMacOs(List<DiscoveredProgram> results)
    {
        var roots = new[]
        {
            "/Applications",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { continue; }

            foreach (var app in Directory.GetDirectories(root))
            {
                if (!app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) { continue; }
                results.Add(new DiscoveredProgram(
                    Path.GetFileNameWithoutExtension(app),
                    app,
                    "Applications",
                    null));
            }
        }
    }

    /* ---------------- Linux ---------------- */

    private static void ScanLinux(List<DiscoveredProgram> results)
    {
        var roots = new[]
        {
            "/usr/share/applications",
            "/var/lib/snapd/desktop/applications",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { continue; }

            foreach (var file in Directory.GetFiles(root, "*.desktop"))
            {
                results.Add(new DiscoveredProgram(
                    Path.GetFileNameWithoutExtension(file),
                    file,
                    "应用程序",
                    null));
            }
        }
    }
}
