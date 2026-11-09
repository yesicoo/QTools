using QTools.Host.Launcher;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 程序索引去重：.lnk 目标解析（MS-SHLLINK 字节解析，不走 COM）+
/// 跨根目录按真实目标去重 + 非 exe 目标过滤。回归背景：同一程序的
/// 「所有用户菜单 / 用户菜单 / 桌面 / 厂商子目录」各一份 .lnk 曾被全部收录。
/// </summary>
public sealed class ProgramDedupeTests : IDisposable
{
    private readonly string _root;

    public ProgramDedupeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-lnk-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 手工构造最小合法 .lnk：76 字节头 + 可选 IDList + 可选 LinkInfo（真实 7-dword 布局，
    /// 可带 VolumeID 模拟主流安装器；编码可选 ANSI（真机实测主流）或 UTF-16）。
    /// </summary>
    private static byte[] BuildLnk(
        string target, bool withIdList = false, bool unicode = true, bool withLinkInfo = true, bool withVolumeId = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((uint)76);
        writer.Write(Guid.Parse("00021401-0000-0000-C000-000000000046").ToByteArray());
        var flags = withLinkInfo ? 0x2u : 0u;
        if (withIdList) { flags |= 0x1u; }
        if (unicode) { flags |= 0x80u; }
        writer.Write(flags);
        writer.Write((uint)0x20);
        writer.Write(0L);
        writer.Write(0L);
        writer.Write(0L);
        writer.Write((uint)0);
        writer.Write((int)0);
        writer.Write((uint)1);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((uint)0);
        writer.Write((uint)0);

        if (withIdList)
        {
            writer.Write((ushort)6);
            writer.Write(new byte[] { 0x1F, 0x00, 0x00, 0x00, 0x00, 0x00 });
        }

        if (!withLinkInfo)
        {
            return stream.ToArray();
        }

        var pathBytes = unicode
            ? System.Text.Encoding.Unicode.GetBytes(target + "\0")
            : System.Text.Encoding.Latin1.GetBytes(target + "\0");

        // LinkInfo 固定头 7 个 dword（28 字节），VolumeID 可选
        var volumeIdOffset = withVolumeId ? 28 : 0;
        var localPathOffset = withVolumeId ? 28 + 15 : 28;

        writer.Write((uint)(localPathOffset + pathBytes.Length)); // LinkInfoSize
        writer.Write((uint)0x1C);                                  // LinkInfoHeaderSize
        writer.Write((uint)0x1);                                   // LinkInfoFlags: 有 LocalBasePath
        writer.Write((uint)volumeIdOffset);                        // VolumeIDOffset
        writer.Write((uint)localPathOffset);                       // LocalBasePathOffset
        writer.Write((uint)0);                                     // CommonNetworkRelativeLinkOffset
        writer.Write((uint)0);                                     // CommonPathSuffixOffset

        if (withVolumeId)
        {
            writer.Write((uint)15);  // VolumeIDSize
            writer.Write((uint)3);   // DriveType: 固定磁盘
            writer.Write((uint)0);   // 序列号
            writer.Write(System.Text.Encoding.ASCII.GetBytes("Q:\0"));
        }

        writer.Write(pathBytes);
        return stream.ToArray();
    }

    private string WriteLnk(string relativePath, byte[] bytes)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }

    [Fact]
    public void ResolvesLocalBasePathAcrossEncodingsAndLayouts()
    {
        // 真机实测主流：ANSI 编码 + 带 VolumeID
        var ansiWithVolume = WriteLnk("ansi-vol.lnk",
            BuildLnk(@"C:\Program Files\Foo\foo.exe", unicode: false, withVolumeId: true));
        // ANSI 无 VolumeID
        var ansiPlain = WriteLnk("ansi.lnk", BuildLnk(@"C:\Program Files\Foo\foo.exe", unicode: false));
        // UTF-16 + IDList
        var unicodeWithList = WriteLnk("uni-list.lnk",
            BuildLnk(@"C:\Program Files\Bar\bar.exe", withIdList: true, unicode: true));

        Assert.Equal(@"C:\Program Files\Foo\foo.exe", LnkTargetResolver.TryResolveLocalBasePath(ansiWithVolume));
        Assert.Equal(@"C:\Program Files\Foo\foo.exe", LnkTargetResolver.TryResolveLocalBasePath(ansiPlain));
        Assert.Equal(@"C:\Program Files\Bar\bar.exe", LnkTargetResolver.TryResolveLocalBasePath(unicodeWithList));
    }

    [Fact]
    public void ReturnsNullForMissingLinkInfoOrGarbage()
    {
        var noInfo = WriteLnk("no-info.lnk", BuildLnk(@"C:\x\y.exe", withLinkInfo: false));
        File.WriteAllBytes(Path.Combine(_root, "garbage.lnk"), new byte[] { 0x4C, 0x00, 0x01, 0x02, 0xFF });
        File.WriteAllText(Path.Combine(_root, "text.lnk"), "hello");
        File.WriteAllBytes(Path.Combine(_root, "short.lnk"), new byte[] { 0x4C, 0x00, 0x00, 0x00 });

        Assert.Null(LnkTargetResolver.TryResolveLocalBasePath(Path.Combine(_root, "no-info.lnk")));
        Assert.Null(LnkTargetResolver.TryResolveLocalBasePath(Path.Combine(_root, "garbage.lnk")));
        Assert.Null(LnkTargetResolver.TryResolveLocalBasePath(Path.Combine(_root, "text.lnk")));
        Assert.Null(LnkTargetResolver.TryResolveLocalBasePath(Path.Combine(_root, "short.lnk")));
        Assert.Null(LnkTargetResolver.TryResolveLocalBasePath(Path.Combine(_root, "missing.lnk")));
    }

    [Fact]
    public void DedupesByResolvedTargetAcrossRoots()
    {
        // 同一程序：所有用户菜单 + 用户菜单 + 桌面，三份 .lnk 指向同一个 exe
        var exeLnk = BuildLnk(@"C:\Program Files\WeChat\WeChat.exe");
        var first = WriteLnk("common/微信.lnk", exeLnk);
        WriteLnk("user/微信.lnk", exeLnk);
        WriteLnk("desktop/微信.lnk", exeLnk);

        // 另一个程序只在桌面有 .lnk
        var other = WriteLnk("desktop/其他.lnk", BuildLnk(@"C:\Program Files\Other\other.exe"));

        var deduped = ProgramDiscovery.DedupePrograms(new[]
        {
            new DiscoveredProgram("微信", first, "开始菜单", null),
            new DiscoveredProgram("微信", Path.Combine(_root, "user", "微信.lnk"), "开始菜单", null),
            new DiscoveredProgram("微信", Path.Combine(_root, "desktop", "微信.lnk"), "桌面", null),
            new DiscoveredProgram("其他", other, "桌面", null),
        });

        Assert.Equal(2, deduped.Count);
        Assert.Equal(first, deduped[0].Target);
        Assert.Equal("开始菜单", deduped[0].Source);
    }

    [Fact]
    public void FallsBackToNameKeyWhenTargetUnresolvable()
    {
        // MSI 广告式快捷方式（无 LinkInfo）：同名两份合并，不同名保留
        var junkBytes = BuildLnk(@"C:\x\y.exe", withLinkInfo: false);
        var one = WriteLnk("d1/办公套件.lnk", junkBytes);
        var two = WriteLnk("d2/办公套件.lnk", junkBytes);
        var three = WriteLnk("d3/其他套件.lnk", junkBytes);

        var deduped = ProgramDiscovery.DedupePrograms(new[]
        {
            new DiscoveredProgram("办公套件", one, "开始菜单", null),
            new DiscoveredProgram("办公套件", two, "开始菜单", null),
            new DiscoveredProgram("其他套件", three, "开始菜单", null),
        });

        Assert.Equal(2, deduped.Count);
        Assert.Contains(deduped, p => p.Target == one);
        Assert.Contains(deduped, p => p.Target == three);
    }

    [Fact]
    public void DropsShortcutsWhoseTargetIsNotExecutable()
    {
        var help = WriteLnk("7-Zip Help.lnk", BuildLnk(@"C:\Program Files\7-Zip\7-zip.chm"));
        var site = WriteLnk("官网.lnk", BuildLnk(@"C:\Users\x\Documents\web.url"));
        var folder = WriteLnk("工具目录.lnk", BuildLnk(@"C:\Program Files\Tools"));
        var app = WriteLnk("app.lnk", BuildLnk(@"C:\Program Files\App\app.exe"));

        var deduped = ProgramDiscovery.DedupePrograms(new[]
        {
            new DiscoveredProgram("7-Zip Help", help, "开始菜单", null),
            new DiscoveredProgram("官网", site, "开始菜单", null),
            new DiscoveredProgram("工具目录", folder, "开始菜单", null),
            new DiscoveredProgram("App", app, "开始菜单", null),
        });

        var app_ = Assert.Single(deduped);
        Assert.Equal(app, app_.Target);
    }

    [Fact]
    public void DedupesDirectExeTargetsByPathNotName()
    {
        var deduped = ProgramDiscovery.DedupePrograms(new[]
        {
            new DiscoveredProgram("A", @"C:\tools\same.exe", "开始菜单", null),
            new DiscoveredProgram("B", @"c:\TOOLS\SAME.EXE", "桌面", null),
            new DiscoveredProgram("C", @"D:\elsewhere\same.exe", "开始菜单", null),
        });

        Assert.Equal(2, deduped.Count);
        Assert.Contains(deduped, p => p.Target == @"C:\tools\same.exe");
        Assert.Contains(deduped, p => p.Target == @"D:\elsewhere\same.exe");
    }

    [Fact]
    public void BuildEntriesFiltersJunkNamesIncludingEnglishWholeWords()
    {
        var programs = new[]
        {
            new DiscoveredProgram("正常程序", @"C:\App\a.exe", "开始菜单", null),
            new DiscoveredProgram("7-Zip Help", @"C:\App\help.lnk", "开始菜单", null),
            new DiscoveredProgram("Install Additional Tools for Node.js", @"C:\App\i.lnk", "开始菜单", null),
            new DiscoveredProgram("Python 3.12 Manuals (64-bit)", @"C:\App\m.lnk", "开始菜单", null),
            new DiscoveredProgram("Visual Studio Installer", @"C:\App\i2.lnk", "开始菜单", null),
            new DiscoveredProgram("卸载助手", @"C:\App\u.exe", "开始菜单", null),
            // 整词匹配不误伤：Helper 工具 / AppUpdate 是合法名字
            new DiscoveredProgram("Helper工具", @"C:\App\h.exe", "开始菜单", null),
            new DiscoveredProgram("AppUpdate", @"C:\App\u2.exe", "开始菜单", null),
            new DiscoveredProgram("得力打印机助手", @"C:\App\d.exe", "开始菜单", null),
        };

        var names = ProgramIndex.BuildEntries(programs).Select(e => e.Name).ToArray();

        // BuildEntries 不排序，保持输入顺序
        Assert.Equal(new[] { "正常程序", "Helper工具", "AppUpdate", "得力打印机助手" }, names);
    }
}
