using System.Runtime.InteropServices;
using QTools.Host.Logging;

namespace QTools.Host.Shell;

/// <summary>
/// 原生窗口标题栏跟随应用主题（仅 Windows）：DWM 沉浸式深色模式。
/// 非 Windows 平台为空操作——标题栏由系统主题决定，PhotinoX 也没有对应开关。
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModePre1903 = 19;
    private const uint WmNcActivate = 0x0086;

    /// <summary>
    /// 应用标题栏深浅色。
    /// <para>
    /// <paramref name="refreshFrame"/> 用于新建窗口首次显示后和运行期主题切换：Win10 只改 DWM 属性
    /// 不会立即重绘标题栏（SWP_FRAMECHANGED / RedrawWindow / WM_THEMECHANGED 实测都无效）。
    /// 这里手动发一对 WM_NCACTIVATE，先切到相反的激活态再切回真实状态，强制 DWM 重画非客户区。
    /// 两条消息同步连发，窗口不移动、不隐藏、不改尺寸、不抢焦点，也不会触发页面的 resize；
    /// 历史上用「隐藏再显示」会导致打开子窗口时整窗闪一下，不要改回去。
    /// </para>
    /// </summary>
    public static void Apply(Photino.NET.PhotinoWindow window, bool dark, bool refreshFrame = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Apply(window.WindowHandle, dark, refreshFrame);
        }
        catch (Exception ex)
        {
            Log.Debug($"应用标题栏主题失败：{ex.Message}");
        }
    }

    public static void Apply(IntPtr handle, bool dark, bool refreshFrame = false)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;
        var result = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        if (result != 0)
        {
            // Windows 10 1809~1903 使用 19；再失败说明系统不支持，静默保持系统默认
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModePre1903, ref value, sizeof(int));
        }

        if (!refreshFrame || !IsWindowVisible(handle))
        {
            return;
        }

        var active = handle == GetForegroundWindow();
        var opposite = active ? IntPtr.Zero : new IntPtr(1);
        var actual = active ? new IntPtr(1) : IntPtr.Zero;
        SendMessage(handle, WmNcActivate, opposite, IntPtr.Zero);
        SendMessage(handle, WmNcActivate, actual, IntPtr.Zero);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
