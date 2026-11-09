using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using QTools.Host.Logging;

namespace QTools.Host.Tools;

/// <summary>
/// 首启引导：把内嵌的示例工具释放到 tools/ 目录。
/// <para>
/// 注意：MSBuild 会把内嵌资源目录名中的 '-' 转成 '_'（hello-notes → hello_notes），
/// 因此这里用显式映射表把资源目录名还原成合法 toolId。
/// </para>
/// </summary>
public static class SampleTools
{
    private const string ResourcePrefix = "QTools.Host.Assets.samples.";

    /// <summary>资源目录名（下划线） → 工具 ID（短横线）。</summary>
    private static readonly (string ResourceDirectory, string ToolId)[] Samples =
    [
        ("json_formatter", "json-formatter"),
        ("qrcode_generator", "qrcode-generator"),
    ];

    private static readonly Assembly Assembly = typeof(SampleTools).Assembly;

    /// <summary>若 tools/ 为空则释放示例工具；返回本次释放的工具 ID。</summary>
    public static IReadOnlyList<string> EnsureSamples(string toolsDirectory)
    {
        var released = new List<string>();

        try
        {
            Directory.CreateDirectory(toolsDirectory);
            RepairBundledQrCode(toolsDirectory);

            // 只认 tools/{id}/ 这一层的真实工具；.versions 等宿主内部目录不算
            var hasExistingTools = Directory.EnumerateDirectories(toolsDirectory)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Any(d => File.Exists(Path.Combine(d, "manifest.json")));

            if (hasExistingTools)
            {
                return released;
            }

            var resourceNames = Assembly.GetManifestResourceNames();

            foreach (var sample in Samples)
            {
                var targetDirectory = Path.Combine(toolsDirectory, sample.ToolId);
                if (Directory.Exists(targetDirectory) && File.Exists(Path.Combine(targetDirectory, "manifest.json")))
                {
                    continue;
                }

                Directory.CreateDirectory(targetDirectory);

                var prefix = ResourcePrefix + sample.ResourceDirectory + ".";
                var written = 0;

                foreach (var resourceName in resourceNames)
                {
                    if (!resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relativeName = resourceName[prefix.Length..];
                    if (relativeName.Length == 0 || relativeName.Contains(".."))
                    {
                        continue;
                    }

                    var targetPath = Path.Combine(targetDirectory, relativeName);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    using var source = Assembly.GetManifestResourceStream(resourceName);
                    if (source is null)
                    {
                        continue;
                    }

                    using var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    source.CopyTo(destination);
                    written++;
                }

                if (written > 0)
                {
                    released.Add(sample.ToolId);
                    Log.Info($"已释放示例工具 {sample.ToolId}（{written} 个文件）到 {targetDirectory}");
                }
            }
        }
        catch (Exception ex)
        {
            // 示例工具释放失败不阻塞宿主启动（用户仍可手动创建工具）
            Log.Warn($"释放示例工具失败：{ex.Message}");
        }

        return released;
    }

    private static void RepairBundledQrCode(string toolsDirectory)
    {
        var directory = Path.Combine(toolsDirectory, "qrcode-generator");
        if (!Directory.Exists(directory) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        var originals = new[]
        {
            (Name: "qrcode.js", Hash: "FDC9F694AAAD30C95002D580C40AB36C3363E223EE7A4750AF456BFDE55E2A42"),
            (Name: "index.html", Hash: "308FEB1A84C51BF88F7FE1794AE1726A082F31E47E92B8DDBD038C3888BBCE5B"),
        };
        foreach (var original in originals)
        {
            var path = Path.Combine(directory, original.Name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            using var target = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(target, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = reader.ReadToEnd().ReplaceLineEndings("\n");
            // 仅修复原始内置文件，保留用户编辑及历史快照。
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) != original.Hash)
            {
                continue;
            }

            using var source = Assembly.GetManifestResourceStream(ResourcePrefix + "qrcode_generator." + original.Name);
            if (source is null)
            {
                continue;
            }

            target.Position = 0;
            source.CopyTo(target);
            target.SetLength(target.Position);
            Log.Info($"已修复内置二维码资源：{original.Name}");
        }
    }
}
