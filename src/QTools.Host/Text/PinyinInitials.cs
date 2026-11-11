using System.Text;

namespace QTools.Host.Text;

/// <summary>
/// 拼音检索键：基于构建期生成的静态表（GB2312 常用汉字 6763 个，含多音字），
/// 同时支持首字母（计算器 → jsq）与全拼（jisuanqi / ji suan qi）。
/// 纯函数 + 静态数据，不依赖运行时编码提供程序与 ICU，AOT/剪裁安全。
/// 表外字符（生僻字、扩展区汉字）不做猜测，直接跳过，避免错误匹配。
/// </summary>
public static class PinyinInitials
{
    /// <summary>单个词条最多生成的多音字全拼变体数，避免极端名字把检索键撑爆。</summary>
    private const int MaxCompactVariants = 8;

    private static readonly Dictionary<char, string> Readings = BuildReadings();

    private static Dictionary<char, string> BuildReadings()
    {
        var map = new Dictionary<char, string>(PinyinData.Entries.Length);
        foreach (var (ch, readings) in PinyinData.Entries)
        {
            map[ch] = readings;
        }

        return map;
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
            else if (Readings.TryGetValue(ch, out var readings))
            {
                builder.Append(readings[0]);
            }
        }

        return builder.ToString();
    }

    /// <summary>紧凑全拼（去声调）：汉字取主读音，ASCII 字母数字原样小写，其余跳过。</summary>
    public static string FullOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length * 4);
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
            else if (Readings.TryGetValue(ch, out var readings))
            {
                builder.Append(Primary(readings));
            }
        }

        return builder.ToString();
    }

    /// <summary>音节分隔全拼：ASCII 词原样保留，汉字按主读音逐个输出（多音字次读音见紧凑变体）。</summary>
    public static string FullSpacedOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var tokens = new List<string>(text.Length * 2);
        var word = new StringBuilder(8);
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                word.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                word.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                if (word.Length > 0)
                {
                    tokens.Add(word.ToString());
                    word.Clear();
                }

                if (Readings.TryGetValue(ch, out var readings))
                {
                    tokens.Add(Primary(readings));
                }
            }
        }

        if (word.Length > 0)
        {
            tokens.Add(word.ToString());
        }

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// 组合检索键：原文（小写）+ 各段拼音首字母 + 紧凑全拼 + 音节全拼。
    /// 前端做子串匹配即可同时命中中文、拼音首字母、全拼与英文。
    /// </summary>
    public static string BuildSearchKey(params string?[] parts)
    {
        var tokens = new List<string>(parts.Length * 4 + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? token)
        {
            if (!string.IsNullOrEmpty(token) && seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        Add(string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))).ToLowerInvariant());
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            Add(Of(part));
            Add(FullSpacedOf(part));
            foreach (var variant in CompactVariants(part))
            {
                Add(variant);
            }
        }

        return string.Join(' ', tokens);
    }

    /// <summary>紧凑全拼组合：多音字轮换次读音，最多 <see cref="MaxCompactVariants"/> 个（含主读音）。</summary>
    private static List<string> CompactVariants(string text)
    {
        var variants = new List<string> { string.Empty };
        foreach (var ch in text)
        {
            string[] options;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                options = [ch.ToString()];
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                options = [char.ToLowerInvariant(ch).ToString()];
            }
            else if (Readings.TryGetValue(ch, out var readings))
            {
                options = readings.Split(' ');
            }
            else
            {
                continue;
            }

            var next = new List<string>(Math.Min(MaxCompactVariants, variants.Count * options.Length));
            foreach (var prefix in variants)
            {
                foreach (var option in options)
                {
                    next.Add(prefix + option);
                    if (next.Count == MaxCompactVariants)
                    {
                        break;
                    }
                }

                if (next.Count == MaxCompactVariants)
                {
                    break;
                }
            }

            variants = next;
        }

        return variants;
    }

    private static string Primary(string readings)
    {
        var space = readings.IndexOf(' ');
        return space < 0 ? readings : readings[..space];
    }
}
