using System.Text.Json;

namespace QTools.Host.Bridge;

/// <summary>
/// 桥接载荷读取与参数校验。所有失败都以 <see cref="BridgeException"/> 抛出，
/// 由分发器转成明确的错误码——绝不静默失败。
/// </summary>
internal static class PayloadReader{
    private const int MaxMessages = 50;
    private const int MaxContentBytes = 32 * 1024;
    private const int MaxTotalContentBytes = 512 * 1024;
    private const double MinTemperature = 0d;
    private const double MaxTemperature = 2d;

    internal static JsonElement RequireObject(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "payload 必须是 JSON 对象");
        }

        return payload.Value;
    }

    public static string ReadKey(JsonElement? payload)
    {
        var root = RequireObject(payload);
        if (!root.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
        {
            throw new BridgeException("STORAGE_KEY_INVALID", "payload.key 必填且为字符串");
        }

        return keyElement.GetString() ?? throw new BridgeException("STORAGE_KEY_INVALID", "payload.key 不能为空");
    }

    /// <summary>读取字符串载荷（如 prompt / error）。</summary>
    internal static string ReadStringPayload(JsonElement? payload, string propertyName, bool required, int maxLength)
    {
        var root = RequireObject(payload);
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            if (required)
            {
                throw new BridgeException("BRIDGE_BAD_REQUEST", $"payload.{propertyName} 必填且为字符串");
            }

            return string.Empty;
        }

        var value = element.GetString() ?? string.Empty;
        if (required && value.Trim().Length == 0)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", $"payload.{propertyName} 不能为空");
        }

        if (value.Length > maxLength)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", $"payload.{propertyName} 不超过 {maxLength} 字符");
        }

        return value;
    }

    public static JsonElement ReadValue(JsonElement? payload)
    {
        var root = RequireObject(payload);
        if (!root.TryGetProperty("value", out var valueElement))
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "payload.value 必填");
        }

        if (valueElement.ValueKind is JsonValueKind.Undefined)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "payload.value 不能为 undefined");
        }

        return valueElement;
    }

    public static string ReadToolId(JsonElement? payload)
    {
        var root = RequireObject(payload);
        if (!root.TryGetProperty("toolId", out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "payload.toolId 必填且为字符串");
        }

        var toolId = element.GetString() ?? "";
        if (toolId.Length == 0)
        {
            throw new BridgeException("TOOL_ID_INVALID", "payload.toolId 不能为空");
        }

        return toolId;
    }

    public static bool ReadDeleteStorage(JsonElement? payload)
    {
        var root = RequireObject(payload);
        return root.TryGetProperty("deleteStorage", out var element) &&
               element.ValueKind == JsonValueKind.True;
    }

    /// <summary>读取 { enabled: bool }（窗口置顶/钉住开关）。</summary>
    public static bool ReadEnabled(JsonElement? payload)
    {
        var root = RequireObject(payload);
        if (!root.TryGetProperty("enabled", out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "payload.enabled 必填且为布尔值");
        }

        return element.ValueKind == JsonValueKind.True;
    }

    /// <summary>读取有限数值字段（「⋯」按钮位置）；缺失或非数值返回 null，由调用方决定是否报错。</summary>
    public static double? ReadNumber(JsonElement? payload, string name)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.Value.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var value = element.GetDouble();
        return double.IsFinite(value) ? value : null;
    }

    /// <summary>读取 ai.chat 的 { messages, temperature } 并做边界校验。</summary>
    public static (IReadOnlyList<(string Role, string Content)> Messages, double? Temperature) ReadChatOptions(JsonElement? payload)
    {
        var root = RequireObject(payload);

        if (!root.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Array)
        {
            throw new BridgeException("AI_BAD_REQUEST", "payload.messages 必填且为数组");
        }

        var messages = new List<(string Role, string Content)>();
        long total = 0;

        foreach (var message in messagesElement.EnumerateArray())
        {
            if (messages.Count >= MaxMessages)
            {
                throw new BridgeException("AI_BAD_REQUEST", $"messages 最多 {MaxMessages} 条");
            }

            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("role", out var roleElement) ||
                roleElement.ValueKind != JsonValueKind.String ||
                !message.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.String)
            {
                throw new BridgeException("AI_BAD_REQUEST", "messages[] 必须包含字符串字段 role 与 content");
            }

            var role = roleElement.GetString() ?? "";
            var content = contentElement.GetString() ?? "";

            if (role is not ("system" or "user" or "assistant"))
            {
                throw new BridgeException("AI_BAD_REQUEST", "role 只支持 system / user / assistant");
            }

            var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            if (bytes > MaxContentBytes)
            {
                throw new BridgeException("AI_BAD_REQUEST", $"单条 content 不超过 {MaxContentBytes / 1024} KB");
            }

            total += bytes;
            if (total > MaxTotalContentBytes)
            {
                throw new BridgeException("AI_BAD_REQUEST", $"messages 总内容不超过 {MaxTotalContentBytes / 1024} KB");
            }

            messages.Add((role, content));
        }

        if (messages.Count == 0)
        {
            throw new BridgeException("AI_BAD_REQUEST", "messages 不能为空");
        }

        double? temperature = null;
        if (root.TryGetProperty("temperature", out var temperatureElement) &&
            temperatureElement.ValueKind == JsonValueKind.Number)
        {
            temperature = temperatureElement.GetDouble();
            if (temperature < MinTemperature || temperature > MaxTemperature)
            {
                throw new BridgeException("AI_BAD_REQUEST", $"temperature 必须在 {MinTemperature}~{MaxTemperature} 之间");
            }
        }

        return (messages, temperature);
    }
}
