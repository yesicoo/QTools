using System.Text;

namespace QTools.Host.Text;

/// <summary>
/// 拼音首字母提取：基于 GB2312 一级汉字区（0xB0A1–0xD7F9，按拼音排序）的边界表。
/// 覆盖常用汉字；二级汉字（按部首排序）与生僻字不做猜测，直接跳过，避免错误匹配。
/// 纯函数 + 静态编码表，AOT/剪裁安全（CodePages 提供程序为数据驱动，不依赖 ICU）。
/// </summary>
public static class PinyinInitials
{
    private static readonly Encoding? Gb2312 = LoadGb2312();

    /// <summary>GB2312 一级汉字各拼音首字母的起始区位码（升序）。</summary>
    private static readonly (int Code, char Initial)[] Boundaries =
    [
        (0xB0A1, 'a'), (0xB0C5, 'b'), (0xB2C1, 'c'), (0xB4EE, 'd'),
        (0xB6EA, 'e'), (0xB7A2, 'f'), (0xB8C1, 'g'), (0xB9FE, 'h'),
        (0xBBF7, 'j'), (0xBFA6, 'k'), (0xC0AC, 'l'), (0xC2E8, 'm'),
        (0xC4C3, 'n'), (0xC5B6, 'o'), (0xC5BE, 'p'), (0xC6DA, 'q'),
        (0xC8BB, 'r'), (0xC8F6, 's'), (0xCBFA, 't'), (0xCDDA, 'w'),
        (0xCEF4, 'x'), (0xD1B9, 'y'), (0xD4D1, 'z'),
    ];

    private const int FirstLevelEnd = 0xD7F9;

    private static Encoding? LoadGb2312()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB2312");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 编码表不可用时退化为「仅 ASCII」，搜索仍可用（只是没有拼音能力）
            return null;
        }
    }

    /// <summary>提取字符串的检索键：ASCII 字母数字原样小写，常用汉字取拼音首字母，其余跳过。</summary>
    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (ch >= 0x4E00 && ch <= 0x9FFF && Gb2312 is not null)
            {
                var initial = InitialOf(ch);
                if (initial != '\0')
                {
                    builder.Append(initial);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>组合检索键：原文（小写、空格分隔）+ 各段拼音首字母，前端做子串匹配即可同时命中中文/拼音/英文。</summary>
    public static string BuildSearchKey(params string?[] parts)
    {
        var raw = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))).ToLowerInvariant();
        var initials = string.Join(' ', parts.Select(Of).Where(s => s.Length > 0));
        return initials.Length > 0 ? raw + " " + initials : raw;
    }

    private static char InitialOf(char ch)
    {
        var bytes = Gb2312!.GetBytes(ch.ToString());
        if (bytes.Length != 2)
        {
            return '\0';
        }

        var code = (bytes[0] << 8) | bytes[1];
        if (code < Boundaries[0].Code || code > FirstLevelEnd)
        {
            return '\0';
        }

        for (var i = Boundaries.Length - 1; i >= 0; i--)
        {
            if (code >= Boundaries[i].Code)
            {
                return Boundaries[i].Initial;
            }
        }

        return '\0';
    }
}