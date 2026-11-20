namespace QTools.Host.Configuration;

/// <summary>
/// 界面主题取值（config.json 的 shell.theme）：
/// <c>system</c> 跟随操作系统浅色/深色；<c>light</c> / <c>dark</c> 强制指定。
/// 宿主页面首屏由服务端注入 <c>&lt;html data-theme&gt;</c>，运行期切换经桥接与 host-event 广播。
/// </summary>
public static class ThemeSetting
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>把任意配置值规整为合法主题（未知值一律按 system）。</summary>
    public static string Normalize(string? value) => value switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };

    /// <summary>需要注入到 HTML 的显式主题；system 返回 null（交给 CSS prefers-color-scheme）。</summary>
    public static string? Explicit(string? value)
    {
        var theme = Normalize(value);
        return theme == System ? null : theme;
    }

    /// <summary>
    /// 当前是否应使用深色：显式 light/dark 直接决定；system 读取 Windows 的应用主题设置
    /// （非 Windows 没有探测实现，按浅色处理）。
    /// </summary>
    public static bool IsDarkNow(string? value)
    {
        return Normalize(value) switch
        {
            Dark => true,
            Light => false,
            _ => IsSystemDark(),
        };
    }

    /// <summary>Windows 系统应用主题是否为深色（AppsUseLightTheme=0）；读取失败按浅色。</summary>
    public static bool IsSystemDark()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0;
        }
        catch
        {
            return false;
        }
    }
}