namespace QTools.Host;

/// <summary>平台与 WebView 引擎事实——不假装全平台 WebKit。</summary>
internal static class PlatformInfo
{
    public static string PlatformName => OperatingSystem.IsWindows()
        ? "Windows"
        : OperatingSystem.IsMacOS()
            ? "macOS"
            : OperatingSystem.IsLinux()
                ? "Linux"
                : "Unknown";

    /// <summary>当前平台的 WebView 引擎（Windows = WebView2/Chromium，macOS = WKWebView，Linux = WebKitGTK 4.1）。</summary>
    public static string WebViewName => OperatingSystem.IsWindows()
        ? "WebView2 (Chromium)"
        : OperatingSystem.IsMacOS()
            ? "WKWebView (WebKit)"
            : OperatingSystem.IsLinux()
                ? "WebKitGTK 4.1 (WebKit)"
                : "Unknown";

    public static bool IsWebKit => !OperatingSystem.IsWindows();
}
