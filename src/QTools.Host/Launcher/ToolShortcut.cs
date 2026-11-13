using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace QTools.Host.Launcher;

/// <summary>
/// 生成 Windows 桌面快捷方式（.lnk）：目标固定为 QTools.exe + --open-tool &lt;id&gt;。
/// <para>
/// 用系统自带的 IShellLinkW + IPersistFile 写文件——TargetIDList / 中文路径 / 参数编码
/// 全部交给 Shell 处理；只写 LinkInfo 的手工 .lnk 不被 ShellExecute 解析（实测）。
/// COM 走源生成接口 [GeneratedComInterface] + CoCreateInstance：Native AOT 官方支持路径，
/// 内置 COM 激活（Type.GetTypeFromCLSID / ComImport）在 AOT 下会抛 PlatformNotSupported_ComInterop。
/// </para>
/// </summary>
public static partial class ToolShortcut
{
    public const string OpenToolArgument = "--open-tool ";

    private const uint ClsctxInprocServer = 0x1;
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IShellLinkWIid = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IPersistFileIid = new("0000010B-0000-0000-C000-000000000046");

    /// <summary>在桌面创建指向本程序的工具快捷方式；返回 .lnk 的完整路径。</summary>
    public static unsafe string Create(
        string desktopDirectory,
        string toolId,
        string toolName,
        string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("创建桌面快捷方式当前仅支持 Windows");
        }

        var exePath = ResolveExecutablePath(executablePath);
        var arguments = OpenToolArgument + toolId;
        Directory.CreateDirectory(desktopDirectory);

        var baseName = BuildShortcutFileName(toolName);
        var target = ChooseTargetPath(desktopDirectory, baseName, arguments);

        var clsid = ShellLinkClsid;
        var iidLink = IShellLinkWIid;
        var hr = CoCreateInstance(in clsid, IntPtr.Zero, ClsctxInprocServer, in iidLink, out var linkPtr);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        var iidPersist = IPersistFileIid;
        var hrQuery = Marshal.QueryInterface(linkPtr, in iidPersist, out var persistPtr);
        var link = UniqueComInterfaceMarshaller<IShellLinkW>.ConvertToManaged((void*)linkPtr)
            ?? throw new InvalidOperationException("ShellLink COM 对象创建失败");
        try
        {
            link.SetPath(exePath);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? desktopDirectory);
            link.SetDescription(toolName);
            link.SetIconLocation(exePath, 0);

            if (hrQuery != 0)
            {
                Marshal.ThrowExceptionForHR(hrQuery);
            }

            var persist = UniqueComInterfaceMarshaller<IPersistFile>.ConvertToManaged((void*)persistPtr)
                ?? throw new InvalidOperationException("ShellLink 未提供 IPersistFile 接口");
            persist.Save(target, true);
            GC.KeepAlive(persist);
        }
        finally
        {
            GC.KeepAlive(link);
        }

        return target;
    }

    /// <summary>快捷方式文件名：替换 Windows 非法字符、去掉结尾点/空格，空名回退「小工具」。</summary>
    public static string BuildShortcutFileName(string toolName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(toolName.Length);
        foreach (var ch in toolName.Trim())
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
        }

        var name = builder.ToString().TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            name = "小工具";
        }

        if (name.Length > 80)
        {
            name = name[..80].TrimEnd('.', ' ');
        }

        if (IsReservedDeviceName(name))
        {
            name += "_";
        }

        return name;
    }

    private static string ResolveExecutablePath(string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return Path.GetFullPath(executablePath);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetFileNameWithoutExtension(processPath).Equals("QTools", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var bystander = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "QTools.exe" : "QTools");
        if (File.Exists(bystander))
        {
            return bystander;
        }

        return string.IsNullOrWhiteSpace(processPath)
            ? throw new InvalidOperationException("无法确定 QTools 可执行文件路径")
            : processPath;
    }

    /// <summary>
    /// 同名快捷方式已存在时：自己创建过的（参数相同）直接覆盖刷新；
    /// 用户的其它同名文件则顺延「名称 (2).lnk」，不静默覆盖别人的桌面文件。
    /// </summary>
    private static string ChooseTargetPath(string desktopDirectory, string baseName, string arguments)
    {
        for (var index = 1; index <= 100; index++)
        {
            var fileName = index == 1 ? baseName + ".lnk" : $"{baseName} ({index}).lnk";
            var fullPath = Path.Combine(desktopDirectory, fileName);
            if (!File.Exists(fullPath) || HasArguments(fullPath, arguments))
            {
                return fullPath;
            }
        }

        return Path.Combine(desktopDirectory, $"{baseName} ({DateTime.Now:yyyyMMddHHmmss}).lnk");
    }

    private static bool HasArguments(string lnkPath, string arguments)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            // Shell 写 StringData 时通常用 UTF-16；参数是纯 ASCII，ANSI/UTF-8 形态也一并匹配
            return IndexOf(bytes, Encoding.Unicode.GetBytes(arguments)) >= 0 ||
                   IndexOf(bytes, Encoding.UTF8.GetBytes(arguments)) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                {
                    continue;
                }

                match = false;
                break;
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsReservedDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ');
        return stem.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            _ => stem.Length == 4 &&
                 (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 stem[3] is >= '1' and <= '9',
        };
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    /// <summary>IShellLinkW：只声明到 SetPath，未用方法保留同序占位以维持 vtable 布局。</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(IntPtr pszName, int cch);

        void SetDescription(string pszName);

        void GetWorkingDirectory(IntPtr pszDir, int cch);

        void SetWorkingDirectory(string pszDir);

        void GetArguments(IntPtr pszArgs, int cch);

        void SetArguments(string pszArgs);

        void GetHotkey(out ushort pwHotkey);

        void SetHotkey(ushort wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);

        void SetIconLocation(string pszIconPath, int iIcon);

        void SetRelativePath(string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath(string pszFile);
    }

    /// <summary>IPersistFile：取 GetClassID/IsDirty/Load/Save 一段，Save 之外的方法只为占位。</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load(string pszFileName, uint dwMode);

        void Save(string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted(string pszFileName);

        void GetCurFile(out IntPtr ppszFileName);
    }
}
