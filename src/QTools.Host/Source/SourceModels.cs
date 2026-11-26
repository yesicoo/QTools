using QTools.Host.Creator;
using QTools.Host.Tools;

namespace QTools.Host.Source;

public sealed record SourceConfigView(string? BaseUrl)
{
    public bool Connected { get; init; }
    public string? ServiceVersion { get; init; }
}

public sealed record SourceToolView(
    string Id,
    string Name,
    string Version,
    string? Description,
    string? Category,
    string? Glyph,
    IReadOnlyList<string> Permissions,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UpdatedAtUtc,
    string PreviewUrl,
    string DownloadUrl,
    IReadOnlyList<string> Versions)
{
    public string Kind { get; init; } = "tool";
    public string? Url { get; init; }
}

public sealed record SourceCatalogView(
    IReadOnlyList<SourceToolView> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> Categories);

public sealed record SourceToolDetailView(SourceToolView Tool, ToolManifest Manifest);

public sealed record SourceInstallResult(
    string ToolId,
    string ToolName,
    string Version,
    string Action,
    bool Saved,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<SecurityFinding> Findings,
    bool HasFatal,
    bool RequiresExplicitAcceptance,
    IReadOnlyList<string> ExternalAssets)
{
    public string PackageSha256 { get; init; } = "";
}

public sealed record SourceSubmitResult(string Id, string Status, string ToolId, string ToolName, string Version);

public sealed record SourceDownloadResult(
    string? Path,
    string? FileName,
    long SizeBytes,
    string Version,
    string Sha256);
