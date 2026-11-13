using System.Text;
using QTools.Host.Launcher;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 桌面快捷方式（ToolShortcut）：IShellLinkW 写出的 .lnk 可被解析回目标与参数、
/// 中文名安全化、同名冲突不覆盖用户文件、自己创建的快捷方式原地刷新。
/// </summary>
public sealed class ToolShortcutTests : IDisposable
{
    private readonly string _root;
    private readonly string _desktop;
    private readonly string _exe;

    public ToolShortcutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-shortcut-" + Guid.NewGuid().ToString("n")[..8]);
        _desktop = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(_desktop);
        _exe = Path.Combine(_root, "QTools.exe");
        File.WriteAllBytes(_exe, Array.Empty<byte>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理失败不影响断言
        }
    }

    [Theory]
    [InlineData("", "小工具")]
    [InlineData("   ", "小工具")]
    [InlineData("CON", "CON_")]
    [InlineData("com7.txt", "com7.txt_")]
    public void BuildShortcutFileNameFallsBackForEmptyAndReservedNames(string input, string expected)
    {
        Assert.Equal(expected, ToolShortcut.BuildShortcutFileName(input));
    }

    [Fact]
    public void BuildShortcutFileNameStripsInvalidCharacters()
    {
        var name = ToolShortcut.BuildShortcutFileName(" bad/name:*?\"<>|. ");
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            Assert.DoesNotContain(ch, name);
        }

        Assert.False(name.EndsWith('.'));
        Assert.False(name.EndsWith(' '));
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void CreateWritesShortcutPointingAtQToolsWithArguments()
    {
        var path = ToolShortcut.Create(_desktop, "todo-list", "待办清单", _exe);

        Assert.Equal(Path.Combine(_desktop, "待办清单.lnk"), path);
        Assert.True(File.Exists(path));
        Assert.Equal(_exe, LnkTargetResolver.TryResolveLocalBasePath(path));

        var bytes = File.ReadAllBytes(path);
        Assert.True(
            Contains(bytes, Encoding.Unicode.GetBytes("--open-tool todo-list")) ||
            Contains(bytes, Encoding.UTF8.GetBytes("--open-tool todo-list")),
            "快捷方式参数应包含 --open-tool todo-list");
    }

    [Fact]
    public void CreateRefreshesOwnShortcutInPlace()
    {
        var first = ToolShortcut.Create(_desktop, "todo-list", "待办清单", _exe);
        var second = ToolShortcut.Create(_desktop, "todo-list", "待办清单", _exe);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(_desktop, "*.lnk"));
    }

    [Fact]
    public void CreateDoesNotOverwriteForeignFileWithSameName()
    {
        var foreign = Path.Combine(_desktop, "待办清单.lnk");
        File.WriteAllText(foreign, "not a shortcut");

        var path = ToolShortcut.Create(_desktop, "todo-list", "待办清单", _exe);

        Assert.Equal(Path.Combine(_desktop, "待办清单 (2).lnk"), path);
        Assert.Equal("not a shortcut", File.ReadAllText(foreign));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
