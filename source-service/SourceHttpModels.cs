namespace QTools.Source;

public sealed class SourceHttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string Host { get; init; }
    public required string RemoteIp { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required Dictionary<string, string?> Query { get; init; }
    public byte[] Body { get; init; } = [];
    public bool IsHead => Method == "HEAD";
    public bool IsGet => Method == "GET";
}

public sealed record SourceHttpResponse
{
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public byte[] Body { get; init; } = [];
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static SourceHttpResponse Json<T>(int statusCode, T value)
    {
        if (statusCode is < 200 or > 299)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }
        return new SourceHttpResponse
        {
            StatusCode = statusCode,
            ContentType = "application/json; charset=utf-8",
            Body = JsonHelper.Serialize(value),
        };
    }

    public static SourceHttpResponse Error(int statusCode, string code, string message) => new()
    {
        StatusCode = statusCode,
        ContentType = "application/json; charset=utf-8",
        Body = JsonHelper.Serialize(new ApiErrorResponse(new ApiError(code, message))),
    };

    public static SourceHttpResponse Bytes(int statusCode, byte[] body, string contentType) => new()
    {
        StatusCode = statusCode,
        ContentType = contentType,
        Body = body,
    };

    public static SourceHttpResponse Redirect(string location) => new()
    {
        StatusCode = 302,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Location"] = location },
    };
}
