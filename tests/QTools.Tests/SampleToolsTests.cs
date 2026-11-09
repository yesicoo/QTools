using System.Text;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

public sealed class SampleToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtools-samples-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EmptyRootReleasesSamplesOnlyOnce()
    {
        Assert.Equal(new[] { "json-formatter", "qrcode-generator" }, SampleTools.EnsureSamples(_root));
        Assert.Empty(SampleTools.EnsureSamples(_root));
        Assert.Equal(Resource("qrcode.js"), File.ReadAllText(Path.Combine(_root, "qrcode-generator", "qrcode.js")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalFilesAreRepairedWithoutChangingManifestOrSnapshots(bool windowsEncoding)
    {
        var directory = CreateExistingTool();
        var script = LegacyScript();
        var html = LegacyHtml();
        var encoding = new UTF8Encoding(windowsEncoding);
        File.WriteAllText(Path.Combine(directory, "qrcode.js"), windowsEncoding ? script.Replace("\n", "\r\n") : script, encoding);
        File.WriteAllText(Path.Combine(directory, "index.html"), windowsEncoding ? html.Replace("\n", "\r\n") : html, encoding);
        var snapshot = Path.Combine(directory, ".versions", "1.0.0");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "qrcode.js"), script);
        File.WriteAllText(Path.Combine(directory, "用户文件.txt"), "需要保留");

        Assert.Empty(SampleTools.EnsureSamples(_root));
        Assert.Equal(Resource("qrcode.js"), File.ReadAllText(Path.Combine(directory, "qrcode.js")));
        Assert.Equal(Resource("index.html"), File.ReadAllText(Path.Combine(directory, "index.html")));
        Assert.Equal("用户清单", File.ReadAllText(Path.Combine(directory, "manifest.json")));
        Assert.Equal(script, File.ReadAllText(Path.Combine(snapshot, "qrcode.js")));
        Assert.Equal("需要保留", File.ReadAllText(Path.Combine(directory, "用户文件.txt")));
        Assert.Empty(SampleTools.EnsureSamples(_root));
    }

    [Fact]
    public void UserEditsAreNotOverwritten()
    {
        var directory = CreateExistingTool();
        var script = LegacyScript() + "\nvar userValue = 1;\n";
        var html = LegacyHtml() + "\n<p>用户内容</p>\n";
        File.WriteAllText(Path.Combine(directory, "qrcode.js"), script);
        File.WriteAllText(Path.Combine(directory, "index.html"), html);

        Assert.Empty(SampleTools.EnsureSamples(_root));
        Assert.Equal(script, File.ReadAllText(Path.Combine(directory, "qrcode.js")));
        Assert.Equal(html, File.ReadAllText(Path.Combine(directory, "index.html")));
    }

    [Fact]
    public void MissingAssetIsNotRecreatedInExistingTool()
    {
        var directory = CreateExistingTool();
        File.WriteAllText(Path.Combine(directory, "index.html"), "用户页面");
        Assert.Empty(SampleTools.EnsureSamples(_root));
        Assert.False(File.Exists(Path.Combine(directory, "qrcode.js")));
        Assert.False(Directory.Exists(Path.Combine(_root, "json-formatter")));
    }

    private string CreateExistingTool()
    {
        var directory = Path.Combine(_root, "qrcode-generator");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "用户清单");
        return directory;
    }

    private static string Resource(string name)
    {
        using var stream = typeof(SampleTools).Assembly.GetManifestResourceStream("QTools.Host.Assets.samples.qrcode_generator." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    private static string LegacyScript() => Resource("qrcode.js")
        .Replace("var best = null, bestScore = Infinity;", "var best = null, bestScore = Infinity, bestMask = 0;")
        .Replace("bestScore = score; best = m;", "bestScore = score; best = m; bestMask = mask;")
        .Replace("return { version: m.version, modules: modules };", "return { version: bestMask, modules: modules };")
        .Replace("this.lastVersion = r.version;", "this.lastVersion = (m_size(canvas) - 17) / 4;")
        .Replace("\n})();", "\n  function m_size(canvas) { return canvas.width; }\n})();");

    private static string LegacyHtml() => Resource("index.html")
        .Replace("    var canvas = $(\"qr\");\n    canvas.hidden = true;\n    $(\"meta\").textContent = \"\";\n", "")
        .Replace("    var size = Math.max(120, Math.min(640, parseInt($(\"size\").value, 10) || 320));", "    var size = Math.max(120, Math.min(640, parseInt($(\"size\").value, 10) || 320));\n    var canvas = $(\"qr\");")
        .Replace("+ canvas.width + \" px\"", "+ size + \" px\"");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
