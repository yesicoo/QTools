using System.Reflection;

namespace QTools.Host;

/// <summary>宿主版本号（读取程序集 InformationalVersion，AOT 下可直接读特性，无需反射扫描）。</summary>
internal static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
