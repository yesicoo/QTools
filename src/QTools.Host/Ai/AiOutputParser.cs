using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Logging;

namespace QTools.Host.Ai;

/// <summary>
/// AI 输出容错解析：把模型返回的文本解析成 <see cref="GeneratedTool"/>。
/// <para>
/// 现实中的模型输出并不总是干净 JSON：常带 ```json 围栏、前后缀解释文字。
/// 解析策略（按序）：
/// 1. 剥掉 markdown 代码围栏后直接整体解析——这是最可靠的路径；
/// 2. 失败则退到「括号配对」提取第一个完整 JSON 对象（应对前后缀噪音）。
/// </para>
/// <para>
/// 注意：括号配对只能作为兜底。当 indexHtml 内含大量引号/反斜杠时，
/// 配对状态的奇偶性可能错位而导致深度无法归零，所以优先用整体解析。
/// </para>
/// </summary>
public static class AiOutputParser
{
    /// <summary>解析 AI 文本；失败抛出 <see cref="BridgeException"/>（AI_OUTPUT_UNPARSEABLE）。</summary>
    public static GeneratedTool Parse(string aiText)
    {
        if (string.IsNullOrWhiteSpace(aiText))
        {
            throw new BridgeException("AI_OUTPUT_UNPARSEABLE", "AI 返回了空内容");
        }

        var json = ExtractJsonCandidate(aiText);
        if (json is null)
        {
            Log.Warn($"AI 输出中找不到 JSON 对象（长度 {aiText.Length}）");
            throw new BridgeException("AI_OUTPUT_UNPARSEABLE", "AI 输出中找不到 JSON 对象");
        }

        GeneratedTool? generated;
        try
        {
            generated = JsonSerializer.Deserialize(json, QTools.Host.Serialization.AppJsonContext.Default.GeneratedTool);
        }
        catch (JsonException ex)
        {
            throw new BridgeException("AI_OUTPUT_UNPARSEABLE", $"AI 输出的 JSON 无法解析：{ex.Message}");
        }

        if (generated is null)
        {
            throw new BridgeException("AI_OUTPUT_UNPARSEABLE", "AI 输出解析结果为空");
        }

        var manifestJson = generated.Manifest.ValueKind == JsonValueKind.Object
            ? generated.Manifest.GetRawText()
            : string.Empty;
        var indexHtml = generated.IndexHtml ?? string.Empty;

        if (manifestJson.Length == 0)
        {
            throw new BridgeException("AI_OUTPUT_UNPARSEABLE", "AI 输出缺少 manifest 字段");
        }

        if (indexHtml.Trim().Length == 0)
        {
            throw new BridgeException("AI_OUTPUT_UNPARSEABLE", "AI 输出缺少 indexHtml 字段");
        }

        return new GeneratedTool(generated.Manifest, indexHtml);
    }

    /// <summary>
    /// 取出最可能的 JSON 候选文本：先尝试剥围栏后整体解析，失败再用括号配对兜底。
    /// </summary>
    internal static string? ExtractJsonCandidate(string text)
    {
        // 1) 剥掉 ```json ... ``` 围栏（允许语言标签缺失）
        var unfenced = StripCodeFence(text);
        if (TryParse(unfenced, out var direct))
        {
            return direct;
        }

        // 2) 括号配对兜底：取第一个完整 JSON 对象
        var balanced = ExtractBalancedObject(text);
        if (balanced is not null && TryParse(balanced, out var parsed))
        {
            return parsed;
        }

        // 3) 围栏内也可能有前后缀噪音：对剥过围栏的文本再做括号配对
        if (!ReferenceEquals(unfenced, text))
        {
            var inner = ExtractBalancedObject(unfenced);
            if (inner is not null && TryParse(inner, out var innerParsed))
            {
                return innerParsed;
            }
        }

        // 4) 都失败：返回括号配对结果（可能为 null），让上层报错时带上原文长度
        return balanced ?? (TryParse(unfenced, out var last) ? last : null);
    }

    /// <summary>验证文本能否作为 GeneratedTool 的 JSON 载体解析出 manifest 与 indexHtml。</summary>
    private static bool TryParse(string candidate, out string normalized)
    {
        normalized = candidate;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("manifest", out var manifest) ||
                manifest.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("indexHtml", out var html) ||
                html.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(html.GetString()))
            {
                return false;
            }

            normalized = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>剥掉首尾 markdown 代码围栏；没有围栏则原样返回。</summary>
    internal static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return text;
        }

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstLineEnd)
        {
            return text;
        }

        return trimmed[(firstLineEnd + 1)..lastFence].Trim();
    }

    /// <summary>从任意文本中取出第一个括号配对的 JSON 对象（兜底用）。</summary>
    internal static string? ExtractBalancedObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return null;
    }
}
