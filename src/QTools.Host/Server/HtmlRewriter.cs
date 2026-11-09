namespace QTools.Host.Server;

/// <summary>
/// HTML 响应重写：注入 window.host 桥接垫层。
/// 之所以在 HTTP 层注入，是因为 PhotinoX 未提供托管侧 JS 注入/执行 API（设计文档 §5.3）。
/// </summary>
internal static class HtmlRewriter
{
    /// <summary>
    /// 注入桥接垫层脚本。
    /// <para>
    /// 关键：必须 <b>不带 defer</b> 且紧贴 <c>&lt;head&gt;</c> 之后插入。
    /// 工具页面的内联脚本通常位于 body 末尾且非 defer，若桥接脚本带 defer 就会晚于页面脚本执行，
    /// 导致页面拿到 undefined 的 window.host（曾因此出现「桥接不可用」假象）。
    /// </para>
    /// </summary>
    public static string InjectBridgeScript(string html, string toolId, string version)
    {
        var script =
            $"<script src=\"/__host__/bridge.js?tool={Uri.EscapeDataString(toolId)}&v={Uri.EscapeDataString(version)}\"></script>";

        // 优先：<head> 开标签之后（保证早于页面任何脚本）
        var headOpen = IndexOfHeadOpenTag(html);
        if (headOpen >= 0)
        {
            return html.Insert(headOpen, script);
        }

        // 退化：</head> 前 → </body> 前 → 文档最前
        var insertAt = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (insertAt < 0)
        {
            insertAt = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        }

        return insertAt < 0 ? script + html : html.Insert(insertAt, script);
    }

    /// <summary>定位 <c>&lt;head&gt;</c> 或 <c>&lt;head ...&gt;</c> 开标签的结束位置（其后即为插入点）；未找到返回 -1。</summary>
    private static int IndexOfHeadOpenTag(string html)
    {
        var index = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var close = html.IndexOf('>', index);
            if (close < 0)
            {
                return -1;
            }

            // 排除 <!-- <head ... --> 之类的注释误匹配
            var lineStart = html.LastIndexOf('\n', index);
            var prefix = lineStart < 0 ? html[..index] : html[(lineStart + 1)..index];
            if (!prefix.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
            {
                return close + 1;
            }

            index = html.IndexOf("<head", close, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }
}
