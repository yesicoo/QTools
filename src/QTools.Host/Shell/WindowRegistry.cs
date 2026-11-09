namespace QTools.Host.Shell;

/// <summary>
/// 窗口 → toolId 绑定表。这是桥接安全的关键：toolId 的权威来源是宿主侧绑定，
/// 客户端消息里的 toolId 不可信（恶意工具无法伪称别的 toolId 越权）。
/// </summary>
public sealed class WindowRegistry
{
    private readonly Dictionary<Guid, string> _bindings = new();
    private readonly object _gate = new();

    public void Register(Guid windowId, string toolId)
    {
        lock (_gate)
        {
            _bindings[windowId] = toolId;
        }
    }

    public string? GetToolId(Guid windowId)
    {
        lock (_gate)
        {
            return _bindings.TryGetValue(windowId, out var toolId) ? toolId : null;
        }
    }

    public void Unregister(Guid windowId)
    {
        lock (_gate)
        {
            _bindings.Remove(windowId);
        }
    }

    public bool IsKnown(Guid windowId)
    {
        lock (_gate)
        {
            return _bindings.ContainsKey(windowId);
        }
    }
}
