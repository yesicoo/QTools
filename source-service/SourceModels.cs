using System.Text.Json;
using System.Text.Json.Serialization;

namespace QTools.Source;

public static class SourceLimits
{
    public const int ApiVersion = 1;
    public const string ServiceVersion = "1.2.0";
    public const int MaxPackageBytes = 8 * 1024 * 1024;
    public const int MaxExtractedBytes = 24 * 1024 * 1024;
    public const int MaxPackageFiles = 500;
    public const int MaxSubmissionBodyBytes = MaxPackageBytes + 1024 * 1024;
}

public sealed record ToolWindow(int Width, int Height, bool Resizable);

public sealed record ToolManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Description { get; init; }
    public string Entry { get; init; } = "";
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public ToolWindow Window { get; init; } = new(0, 0, false);
    public string CreatedAt { get; init; } = "";
    public bool AllowEval { get; init; }
    public string? Csp { get; init; }
    public string? Category { get; init; }
    public string? Glyph { get; init; }
}

public sealed record ExportManifest
{
    public int SchemaVersion { get; init; }
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string ExportedAtUtc { get; init; } = "";
    public string AppVersion { get; init; } = "";
}

/// <summary>在线工具（添加网址）的 web.json：源站以 kind=web 的导出包接收与分发。</summary>
public sealed record WebToolInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Category { get; init; }
    public string? Glyph { get; init; }
    public string? Description { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string CreatedAt { get; init; } = "";
}

public sealed record Warning
{
    public string Level { get; init; } = "";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record Revision
{
    public string SubmissionId { get; init; } = "";
    public string Version { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset SubmittedAtUtc { get; init; }
    public DateTimeOffset ApprovedAtUtc { get; init; }
    public ToolManifest Manifest { get; init; } = new();
    public string Kind { get; init; } = "tool";
    public string? WebUrl { get; init; }
}

public sealed record CatalogTool
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Glyph { get; init; }
    public string Kind { get; init; } = "tool";
    public string? WebUrl { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public string CurrentSubmissionId { get; init; } = "";
    public IReadOnlyList<Revision> Versions { get; init; } = [];
    public bool Published { get; init; }
    public bool Pinned { get; init; }
    public long Downloads { get; init; }
    public bool DescriptionCustomized { get; init; }
    public bool CategoryCustomized { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record Submission
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ReviewedAtUtc { get; init; }
    public string? ReviewNote { get; init; }
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Kind { get; init; } = "tool";
    public string? WebUrl { get; init; }
    public ExportManifest Export { get; init; } = new();
    public ToolManifest Manifest { get; init; } = new();
    public IReadOnlyList<Warning> Warnings { get; init; } = [];

    [JsonIgnore]
    public string PackagePath { get; init; } = "";

    [JsonIgnore]
    public string ExtractPath { get; init; } = "";
}

public sealed class StoreState
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, Submission> Submissions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CatalogTool> Catalog { get; set; } = new(StringComparer.Ordinal);
}

public sealed record PublicTool
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Kind { get; init; } = "tool";
    public string? Url { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Glyph { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string PreviewUrl { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public IReadOnlyList<string> Versions { get; init; } = [];
    public bool Pinned { get; init; }
    public long Downloads { get; init; }
    public int Rank { get; init; }
    public int ToolCount { get; init; }
}

public sealed record CatalogResponse
{
    public IReadOnlyList<PublicTool> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
}

public sealed record ToolDetailResponse(PublicTool Tool, ToolManifest Manifest);

public sealed record MetaResponse
{
    public string Service { get; init; } = "";
    public string ApiVersion { get; init; } = "";
    public string Version { get; init; } = "";
    public string PackageFormat { get; init; } = "";
    public int MaxPackageBytes { get; init; }
}

public sealed record SubmitResponse
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusUrl { get; init; } = "";
    public string ToolId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string Version { get; init; } = "";
}

public sealed record PublicStatus
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ReviewedAtUtc { get; init; }
    public string? ReviewNote { get; init; }
    public string? ToolId { get; init; }
    public string? ToolName { get; init; }
    public string? Version { get; init; }
}

public sealed record AdminSession
{
    public bool Authenticated { get; init; }
    public string? CsrfToken { get; init; }
}

public sealed record AdminStats
{
    public int Pending { get; init; }
    public int Approved { get; init; }
    public int Rejected { get; init; }
    public int Tools { get; init; }
    public long Downloads { get; init; }
}

public sealed record SubmissionListResponse(IReadOnlyList<Submission> Items);
public sealed record CatalogToolListResponse(IReadOnlyList<CatalogTool> Items);
public sealed record OkResponse(bool Ok);
public sealed record HealthResponse(bool Ok, string Version);

public sealed record ApiErrorResponse(ApiError Error);
public sealed record ApiError(string Code, string Message);

public sealed record LoginRequest(string Password);
public sealed record ReviewRequest(string Action, string? Note);
public sealed record ToolMetadataRequest(string? Description, string? Category, bool? Pinned);

public sealed record PasswordRecord
{
    public string Algorithm { get; init; } = "";
    public string Salt { get; init; } = "";
    public string Hash { get; init; } = "";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreState))]
[JsonSerializable(typeof(ToolManifest))]
[JsonSerializable(typeof(ExportManifest))]
[JsonSerializable(typeof(WebToolInfo))]
[JsonSerializable(typeof(Warning))]
[JsonSerializable(typeof(Revision))]
[JsonSerializable(typeof(CatalogTool))]
[JsonSerializable(typeof(Submission))]
[JsonSerializable(typeof(PublicTool))]
[JsonSerializable(typeof(CatalogResponse))]
[JsonSerializable(typeof(ToolDetailResponse))]
[JsonSerializable(typeof(MetaResponse))]
[JsonSerializable(typeof(SubmitResponse))]
[JsonSerializable(typeof(PublicStatus))]
[JsonSerializable(typeof(AdminSession))]
[JsonSerializable(typeof(AdminStats))]
[JsonSerializable(typeof(SubmissionListResponse))]
[JsonSerializable(typeof(CatalogToolListResponse))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ReviewRequest))]
[JsonSerializable(typeof(ToolMetadataRequest))]
[JsonSerializable(typeof(PasswordRecord))]
[JsonSerializable(typeof(SourceConfigFile))]
public partial class SourceJsonContext : JsonSerializerContext;
