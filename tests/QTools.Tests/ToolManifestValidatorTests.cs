using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>manifest 校验与路径安全规则（对应验收标准 #4/#9 的可重复证据）。</summary>
public sealed class ToolManifestValidatorTests
{
    private static ToolManifest CreateManifest(
        string id = "demo-tool",
        string entry = "index.html",
        IReadOnlyList<string>? permissions = null,
        int width = 520,
        int height = 720) => new(
            id,
            "Demo Tool",
            "1.0.0",
            "desc",
            entry,
            permissions ?? Array.Empty<string>(),
            new ToolWindowSettings(width, height, true),
            "2026-11-03T00:00:00+08:00",
            false,
            null);

    [Theory]
    [InlineData("hello-notes")]
    [InlineData("a")]
    [InlineData("tool-123-abc")]
    [InlineData("0abc")]
    [InlineData("ab")]
    public void ValidToolIds(string id)
    {
        Assert.Matches(ToolManifestValidator.ToolIdPattern(), id);
    }

    [Theory]
    [InlineData("Hello-Notes")]   // 大写
    [InlineData("hello_notes")]   // 下划线
    [InlineData("-lead")]         // 短横线开头
    [InlineData("trail-")]        // 短横线结尾
    [InlineData("has space")]
    [InlineData("slash/id")]
    [InlineData("dot.id")]
    [InlineData("")]
    public void InvalidToolIds(string id)
    {
        Assert.DoesNotMatch(ToolManifestValidator.ToolIdPattern(), id);
    }

    [Fact]
    public void ValidManifestHasNoErrors()
    {
        var errors = ToolManifestValidator.Validate(CreateManifest(), "demo-tool");
        Assert.Empty(errors);
    }

    [Fact]
    public void CategoryAndGlyphLengthsAreValidated()
    {
        var ok = CreateManifest() with { Category = "效率", Glyph = "🧮" };
        Assert.Empty(ToolManifestValidator.Validate(ok, "demo-tool"));

        var bad = CreateManifest() with { Category = new string('类', 31), Glyph = "12345" };
        var errors = ToolManifestValidator.Validate(bad, "demo-tool");
        Assert.Contains(errors, e => e.Contains("category"));
        Assert.Contains(errors, e => e.Contains("glyph"));
    }

    [Fact]
    public void IdMustMatchDirectoryName()
    {
        var errors = ToolManifestValidator.Validate(CreateManifest(id: "other-id"), "demo-tool");
        Assert.Contains(errors, e => e.Contains("目录名"));
    }

    [Fact]
    public void UnknownPermissionIsRejected()
    {
        var errors = ToolManifestValidator.Validate(
            CreateManifest(permissions: new[] { "storage", "filesystem" }), "demo-tool");
        Assert.Contains(errors, e => e.Contains("filesystem"));
    }

    [Theory]
    [InlineData(319, 720)]
    [InlineData(520, 19)]
    [InlineData(3841, 720)]
    public void WindowSizeOutOfRangeIsRejected(int width, int height)
    {
        var errors = ToolManifestValidator.Validate(CreateManifest(width: width, height: height), "demo-tool");
        Assert.Contains(errors, e => e.Contains("window"));
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("pages/app.html")]
    [InlineData("a/b/c.html")]
    public void SafeEntryPaths(string entry)
    {
        Assert.True(ToolManifestValidator.IsSafeRelativePath(entry));
    }

    [Theory]
    [InlineData("../index.html")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("..\\..\\data\\config.json")]
    [InlineData("pages/../../index.html")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/\0b")]
    public void UnsafeEntryPaths(string entry)
    {
        Assert.False(ToolManifestValidator.IsSafeRelativePath(entry));
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("ai")]
    public void KnownPermissionsParse(string value)
    {
        Assert.True(ToolManifestValidator.TryParsePermission(value, out _));
    }

    [Theory]
    [InlineData("net")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownPermissionsDoNotParse(string? value)
    {
        Assert.False(ToolManifestValidator.TryParsePermission(value, out _));
    }
}
