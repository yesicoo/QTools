using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace QTools.Host.Shell;

/// <summary>
/// 内嵌凭证注册表：主窗口以 iframe 内嵌工具页面时，桥接消息从主窗口的原生回调到来
/// （此时窗口绑定是 __host__，无法区分消息来源），必须靠「宿主签发的凭证」建立身份。
/// <para>
/// 安全语义：凭证由 manager.embedTool（hostOnly）签发，内容是随机 128 位 hex，
/// 别的工具页面既拿不到（manager.* 对非宿主页面被拒），也无法猜出。
/// 凭证 → toolId 的映射只在宿主进程内存中，不落盘、不进日志。
/// </para>
/// </summary>
public sealed class EmbedTokenRegistry
{
    /// <summary>凭证有效期：内嵌页随换页/关闭走 Revoke 即时失效，这里只是过期兜底。</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>同屏内嵌页通常只有一个；全表设上限防止意外膨胀。</summary>
    private const int MaxTokens = 64;

    private sealed record Entry(string ToolId, DateTimeOffset ExpiresUtc);

    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>为工具签发内嵌凭证；同一工具只保留最新一张，旧凭证立即作废。</summary>
    public string Issue(string toolId)
    {
        var token = RandomNumberGenerator.GetBytes(16)
            .Select(b => b.ToString("x2"))
            .Aggregate(new StringBuilder(32), (sb, hex) => sb.Append(hex))
            .ToString();

        lock (_gate)
        {
            // 同一工具只允许一张有效凭证：签发前撤销旧的
            foreach (var stale in _tokens
                         .Where(kv => kv.Value.ToolId == toolId)
                         .Select(kv => kv.Key)
                         .ToArray())
            {
                _tokens.TryRemove(stale, out _);
            }

            if (_tokens.Count >= MaxTokens)
            {
                // 上限兜底：先清过期，仍超限则移除最先到期的一张
                var now = DateTimeOffset.UtcNow;
                foreach (var expired in _tokens
                             .Where(kv => kv.Value.ExpiresUtc <= now)
                             .Select(kv => kv.Key)
                             .ToArray())
                {
                    _tokens.TryRemove(expired, out _);
                }

                if (_tokens.Count >= MaxTokens)
                {
                    var oldest = _tokens.OrderBy(kv => kv.Value.ExpiresUtc).First();
                    _tokens.TryRemove(oldest.Key, out _);
                }
            }

            _tokens[token] = new Entry(toolId, DateTimeOffset.UtcNow + Lifetime);
        }

        return token;
    }

    /// <summary>解析凭证 → toolId；无效、格式不对、被替换或过期时返回 null。</summary>
    public string? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 32)
        {
            return null;
        }

        if (!_tokens.TryGetValue(token, out var entry) ||
            entry.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        // 若该工具又领了新凭证，旧凭证必须失效（一张凭证 = 一个内嵌会话）
        lock (_gate)
        {
            var superseded = _tokens
                .Any(kv => !string.Equals(kv.Key, token, StringComparison.Ordinal) &&
                           kv.Value.ToolId == entry.ToolId);
            if (superseded)
            {
                _tokens.TryRemove(token, out _);
                return null;
            }
        }

        return entry.ToolId;
    }

    /// <summary>撤销工具的全部凭证（内嵌页关闭 / 工具删除 / 版本切换等场景）。</summary>
    public void Revoke(string toolId)
    {
        lock (_gate)
        {
            foreach (var stale in _tokens
                         .Where(kv => kv.Value.ToolId == toolId)
                         .Select(kv => kv.Key)
                         .ToArray())
            {
                _tokens.TryRemove(stale, out _);
            }
        }
    }
}
