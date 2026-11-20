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

    /// <summary>
    /// 注入独立窗口控制条脚本（置顶 / 钉住）。脚本自身判断：
    /// 内嵌 iframe（window.top !== window.self）或桥接不可用时不会显示任何东西。
    /// </summary>
    public static string InjectWindowControlsScript(string html)
    {
        const string script = "<script src=\"/__host__/window-controls.js\"></script>";
        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyClose < 0 ? script + html : html.Insert(bodyClose, script);
    }

    /// <summary>
    /// 给宿主页面的 <c>&lt;html&gt;</c> 标签注入显式主题（light / dark），让首屏就是正确配色、避免闪白。
    /// system（或 null）不做任何修改，交给 CSS 的 prefers-color-scheme。
    /// </summary>
    public static string InjectThemeAttribute(string html, string? theme)
    {
        if (theme is not ("light" or "dark"))
        {
            return html;
        }

        var open = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return html;
        }

        var close = html.IndexOf('>', open);
        if (close < 0)
        {
            return html;
        }

        var existing = html.IndexOf("data-theme", open, close - open, StringComparison.OrdinalIgnoreCase);
        if (existing < 0)
        {
            return html.Insert(close, $" data-theme=\"{theme}\"");
        }

        var equals = html.IndexOf('=', existing + "data-theme".Length);
        if (equals < 0 || equals >= close)
        {
            return html;
        }

        var quote = html[equals + 1];
        if (quote is not ('"' or '\''))
        {
            return html;
        }

        var valueEnd = html.IndexOf(quote, equals + 2);
        if (valueEnd < 0 || valueEnd > close)
        {
            return html;
        }

        return string.Concat(html[..(equals + 2)], theme, html[valueEnd..]);
    }

    /// <summary>
    /// 工具页面主题注入：在 <c>&lt;html&gt;</c> 上标记应用主题，并注入 tool-theme.css / tool-theme.js。
    /// <para>
    /// 页面自带主题能力（出现 data-theme / prefers-color-scheme / color-scheme）→ 让页面自己换肤；
    /// 都没有的旧工具 → 深色时加 <c>data-qtools-auto-dark</c>，由 tool-theme.css 用反色滤镜兜底。
    /// 首屏状态完全由服务端决定，避免先亮后暗的闪烁；运行期切换由 tool-theme.js 订阅 host-event 处理。
    /// </para>
    /// </summary>
    public static string InjectToolTheme(string html, string theme, bool dark)
    {
        var themed = ContainsToken(html, "data-theme");
        var adaptive = themed ||
                       ContainsToken(html, "prefers-color-scheme") ||
                       ContainsToken(html, "color-scheme");
        var explicitTheme = theme is "light" or "dark" ? theme : null;

        // system 时清掉页面自带的 data-theme，交还给它的媒体查询/默认配色
        html = SetHtmlAttribute(html, "data-theme", explicitTheme);
        html = SetHtmlAttribute(html, "data-qtools-theme", theme);
        html = SetHtmlAttribute(html, "data-qtools-themed", adaptive ? "1" : "0");
        html = SetHtmlAttribute(html, "data-qtools-auto-dark", !adaptive && dark ? "1" : null);

        const string assets =
            "<link rel=\"stylesheet\" href=\"/__host__/tool-theme.css\">" +
            "<script src=\"/__host__/tool-theme.js\" defer></script>";

        var headOpen = IndexOfHeadOpenTag(html);
        if (headOpen >= 0)
        {
            return html.Insert(headOpen, assets);
        }

        var insertAt = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (insertAt < 0)
        {
            insertAt = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        }

        return insertAt < 0 ? assets + html : html.Insert(insertAt, assets);
    }

    private static bool ContainsToken(string html, string token) =>
        html.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在 <c>&lt;html&gt;</c> 开标签上设置/替换/删除一个属性（value 为 null 时删除）。
    /// 支持双引号、单引号与无引号值；找不到 &lt;html&gt; 或格式异常时原样返回。
    /// </summary>
    private static string SetHtmlAttribute(string html, string name, string? value)
    {
        var open = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return html;
        }

        var close = html.IndexOf('>', open);
        if (close < 0)
        {
            return html;
        }

        // 定位「独立单词」形式的属性名（避免匹配到 data-x 里包含的子串）
        var index = -1;
        var scan = open + 5;
        while (scan < close)
        {
            var hit = html.IndexOf(name, scan, close - scan, StringComparison.OrdinalIgnoreCase);
            if (hit < 0)
            {
                break;
            }

            var beforeOk = char.IsWhiteSpace(html[hit - 1]);
            var after = hit + name.Length;
            var afterOk = after < close &&
                          (char.IsWhiteSpace(html[after]) || html[after] == '=');
            if (beforeOk && afterOk)
            {
                index = hit;
                break;
            }

            scan = hit + name.Length;
        }

        if (index < 0)
        {
            return value is null ? html : html.Insert(close, $" {name}=\"{value}\"");
        }

        if (value is null)
        {
            var start = index;
            while (start > open && char.IsWhiteSpace(html[start - 1]))
            {
                start--;
            }

            var end = index + name.Length;
            while (end < close && char.IsWhiteSpace(html[end]))
            {
                end++;
            }

            if (end < close && html[end] == '=')
            {
                end++;
                while (end < close && char.IsWhiteSpace(html[end]))
                {
                    end++;
                }

                if (end < close && (html[end] == '"' || html[end] == '\''))
                {
                    var quote = html[end];
                    var valueEnd = html.IndexOf(quote, end + 1);
                    if (valueEnd > 0 && valueEnd < close)
                    {
                        end = valueEnd + 1;
                    }
                }
                else
                {
                    while (end < close && !char.IsWhiteSpace(html[end]))
                    {
                        end++;
                    }
                }
            }

            return html.Remove(start, end - start);
        }

        var equals = html.IndexOf('=', index + name.Length);
        if (equals < 0 || equals >= close)
        {
            return html;
        }

        var quoteIndex = equals + 1;
        while (quoteIndex < close && char.IsWhiteSpace(html[quoteIndex]))
        {
            quoteIndex++;
        }

        if (quoteIndex >= close || (html[quoteIndex] != '"' && html[quoteIndex] != '\''))
        {
            return html;
        }

        var closing = html.IndexOf(html[quoteIndex], quoteIndex + 1);
        if (closing < 0 || closing > close)
        {
            return html;
        }

        return string.Concat(html[..(quoteIndex + 1)], value, html[closing..]);
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
