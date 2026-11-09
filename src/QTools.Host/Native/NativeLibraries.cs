using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using QTools.Host.Logging;

namespace QTools.Host.Native;

/// <summary>
/// PhotinoX 原生库自举。
/// <para>
/// 背景（已实测）：.NET 10 Native AOT 的单文件发布<b>不会</b>把原生库打进 exe——
/// 无论是否设置 <c>IncludeNativeLibrariesForSelfExtract</c>，PhotinoX.Native.dll /
/// WebView2Loader.dll 都只会作为 sidecar 存在。Avalonia 同理（缺 libSkiaSharp 即崩）。
/// </para>
/// <para>
/// 做法：把当前 RID 的原生库作为内嵌资源打进程序集（csproj 中以 LogicalName 固定命名），
/// 启动时解压到 <c>data/native/{rid}/</c>，并在 PhotinoX 程序集上注册
/// <see cref="NativeLibrary.SetDllImportResolver"/>，让 <c>DllImport("PhotinoX.Native")</c>
/// 能解析到解压出来的文件。全程无反射、无动态代码生成，Native AOT 兼容。
/// </para>
/// <para>
/// 资源有两种形态：Debug 内嵌原始文件（<c>QTools.native.*</c>）；Release 内嵌
/// Deflate 压缩版（<c>QTools.nativez.*</c>，前 4 字节为小端原始长度），缩小单文件体积。
/// </para>
/// </summary>
internal static class NativeLibraries
{
    private const string ResourcePrefix = "QTools.native.";
    private const string CompressedResourcePrefix = "QTools.nativez.";

    /// <summary>P/Invoke 的库名（PhotinoX 在三个平台都用这个名字，只是扩展名不同）。</summary>
    private static readonly string[] LibraryNames = ["PhotinoX.Native", "WebView2Loader"];

    private static readonly Assembly Assembly = typeof(NativeLibraries).Assembly;

    /// <summary>
    /// 解压内嵌原生库并注册 DllImport 解析器。必须在任何 PhotinoX API 调用之前执行。
    /// </summary>
    /// <returns>原生库解压目录；失败时返回 null（此时回退到默认解析，即 exe 同目录的 sidecar）。</returns>
    public static string? Bootstrap(string dataDirectory)
    {
        string? targetDirectory;
        try
        {
            targetDirectory = Extract(dataDirectory);
        }
        catch (Exception ex)
        {
            // 解压失败不致命：若发布目录里带着 sidecar 原生库，默认解析仍可工作
            Log.Warn($"解压内嵌原生库失败（{ex.Message}），将回退到默认原生库解析");
            targetDirectory = null;
        }

        RegisterResolver(targetDirectory);
        return targetDirectory;
    }

    private static string? Extract(string dataDirectory)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var targetDirectory = Path.Combine(dataDirectory, "native", rid);
        Directory.CreateDirectory(targetDirectory);

        var names = Assembly.GetManifestResourceNames();
        var extracted = 0;
        var skipped = 0;

        foreach (var resourceName in names)
        {
            string fileName;
            bool compressed;
            if (resourceName.StartsWith(CompressedResourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                fileName = resourceName[CompressedResourcePrefix.Length..];
                compressed = true;
            }
            else if (resourceName.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                fileName = resourceName[ResourcePrefix.Length..];
                compressed = false;
            }
            else
            {
                continue;
            }

            if (fileName.Length == 0 || fileName.Contains(".."))
            {
                continue;
            }

            var targetPath = Path.Combine(targetDirectory, fileName);

            using var source = Assembly.GetManifestResourceStream(resourceName);
            if (source is null)
            {
                continue;
            }

            var expectedLength = ReadExpectedLength(source, compressed);

            // 已存在且大小一致则跳过：避免每次启动都重写 0.7MB
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == expectedLength)
            {
                skipped++;
                continue;
            }

            using var destination = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            CopyPayload(source, destination, compressed, expectedLength);
            extracted++;
        }

        if (extracted > 0)
        {
            Log.Info($"已解压 {extracted} 个内嵌原生库到 {targetDirectory}（跳过 {skipped} 个已存在）");
        }
        else if (skipped > 0)
        {
            Log.Debug($"内嵌原生库已是最新（{skipped} 个），目录 {targetDirectory}");
        }
        else
        {
            Log.Warn("程序集中没有内嵌原生库（QTools.native.* / QTools.nativez.*），单文件自包含可能不完整");
        }

        return targetDirectory;
    }

    /// <summary>读取资源原始长度：压缩资源前 4 字节为小端长度头，原始资源直接取流长度。</summary>
    internal static long ReadExpectedLength(Stream source, bool compressed)
    {
        if (!compressed)
        {
            return source.Length;
        }

        Span<byte> header = stackalloc byte[4];
        source.ReadExactly(header);
        return BinaryPrimitives.ReadUInt32LittleEndian(header);
    }

    /// <summary>把资源内容写入目标流并校验长度，压缩数据被截断或损坏时立即抛出。</summary>
    internal static void CopyPayload(Stream source, Stream destination, bool compressed, long expectedLength)
    {
        if (compressed)
        {
            using var deflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
            deflate.CopyTo(destination);
        }
        else
        {
            source.CopyTo(destination);
        }

        if (destination.Length != expectedLength)
        {
            throw new InvalidDataException($"原生库长度校验失败：期望 {expectedLength} 字节，实际 {destination.Length} 字节");
        }
    }

    private static void RegisterResolver(string? directory)
    {
        var photinoAssembly = typeof(Photino.NET.PhotinoApplication).Assembly;

        NativeLibrary.SetDllImportResolver(photinoAssembly, (libraryName, _, _) =>
        {
            if (directory is null ||
                !LibraryNames.Contains(libraryName, StringComparer.OrdinalIgnoreCase))
            {
                // 返回 Zero 表示交回运行时默认解析（exe 同目录 / PATH / 系统目录）
                return IntPtr.Zero;
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(directory, libraryName + ".dll"),
                         Path.Combine(directory, libraryName + ".so"),
                         Path.Combine(directory, libraryName + ".dylib"),
                     })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }

            Log.Warn($"DllImportResolver 未能加载原生库 {libraryName}（目录 {directory}），回退默认解析");
            return IntPtr.Zero;
        });
    }
}
