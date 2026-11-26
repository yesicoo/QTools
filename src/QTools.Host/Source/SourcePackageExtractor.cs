using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using QTools.Host.Bridge;

namespace QTools.Host.Source;

internal static class SourcePackageExtractor
{
    private const long MaxValidatedEntryBytes = 16L * 1024 * 1024;
    private const long MaxValidatedPackageBytes = 64L * 1024 * 1024;

    public static void ValidatePackage(string packagePath)
    {
        var package = File.ReadAllBytes(packagePath);
        var layout = Parse(package);
        using var archive = ZipFile.OpenRead(packagePath);
        if (layout.Entries.Count != archive.Entries.Count)
        {
            throw new InvalidDataException("安装包中央目录与条目数量不一致");
        }

        long total = 0;
        var buffer = new byte[81920];
        foreach (var entry in layout.Entries)
        {
            using var source = OpenEntry(package, entry, layout.CentralDirectoryOffset, entry.Name);
            long actual = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                actual += read;
                total += read;
                if (actual > MaxValidatedEntryBytes || total > MaxValidatedPackageBytes)
                {
                    throw new InvalidDataException("安装包实际解压后体积过大");
                }
            }

            if (actual != entry.UncompressedLength)
            {
                throw new InvalidDataException($"安装包条目 {entry.Name} 的实际解压长度与记录不一致");
            }
        }
    }

    public static async Task ExtractAsync(
        string packagePath, string prefix, string destination, CancellationToken cancellationToken)
    {
        var package = await File.ReadAllBytesAsync(packagePath, cancellationToken).ConfigureAwait(false);
        var layout = Parse(package);
        using var archive = ZipFile.OpenRead(packagePath);
        if (layout.Entries.Count != archive.Entries.Count)
        {
            throw Invalid("安装包中央目录与条目数量不一致");
        }

        long extractedTotal = 0;
        var buffer = new byte[81920];
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var relative = entry.FullName.Replace('\\', '/');
            if (!relative.StartsWith(prefix, StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name)) { continue; }
            var target = Path.Combine(destination, relative[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, useAsync: true);
            var written = await ExtractEntryAsync(package, layout.Entries[index], layout.CentralDirectoryOffset,
                output, buffer, entry.FullName, cancellationToken).ConfigureAwait(false);
            extractedTotal += written;
            if (extractedTotal > ToolSourceService.MaxExtractedPackageBytes)
            {
                throw new BridgeException("SOURCE_PACKAGE_TOO_LARGE", "安装包实际解压后超过 24 MiB，已拒绝安装");
            }
            if (written != layout.Entries[index].UncompressedLength)
            {
                throw Invalid($"安装包条目 {entry.FullName} 的实际解压长度与记录不一致");
            }
        }
    }

    private static async Task<long> ExtractEntryAsync(
        byte[] package, ZipEntryLayout entry, int centralOffset, FileStream output,
        byte[] buffer, string name, CancellationToken cancellationToken)
    {
        try
        {
            using var source = OpenEntry(package, entry, centralOffset, name);
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > ToolSourceService.MaxExtractedPackageBytes)
                {
                    throw new BridgeException("SOURCE_PACKAGE_TOO_LARGE", "安装包实际解压后超过 24 MiB，已拒绝安装");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return written;
        }
        catch (InvalidDataException ex)
        {
            throw Invalid(ex.Message);
        }
    }

    private static Stream OpenEntry(
        byte[] package, ZipEntryLayout entry, int centralOffset, string name)
    {
        if ((entry.Flags & 1) != 0) { throw new InvalidDataException("安装包不能包含加密条目"); }
        if (entry.Method is not (0 or 8)) { throw new InvalidDataException($"不支持的压缩方法：{name}"); }
        if (entry.LocalOffset > package.Length - 30 || ReadU32(package, entry.LocalOffset) != 0x04034B50)
        {
            throw new InvalidDataException("安装包本地文件头不合法");
        }
        var flags = ReadU16(package, entry.LocalOffset + 6);
        var method = ReadU16(package, entry.LocalOffset + 8);
        var nameLength = ReadU16(package, entry.LocalOffset + 26);
        var extraLength = ReadU16(package, entry.LocalOffset + 28);
        var dataOffset = entry.LocalOffset + 30 + nameLength + extraLength;
        if (flags != entry.Flags || method != entry.Method || dataOffset > package.Length ||
            !package.AsSpan(entry.LocalOffset + 30, nameLength).SequenceEqual(entry.NameBytes))
        {
            throw new InvalidDataException("安装包本地文件头与中央目录不一致");
        }
        if (entry.CompressedLength > package.Length - dataOffset || dataOffset + entry.CompressedLength > centralOffset)
        {
            throw new InvalidDataException("安装包压缩数据范围不合法");
        }

        var compressed = new MemoryStream(package, dataOffset, entry.CompressedLength, writable: false);
        return entry.Method == 8 ? new DeflateStream(compressed, CompressionMode.Decompress) : compressed;
    }

    private static ZipPackageLayout Parse(byte[] package)
    {
        var end = -1;
        for (var i = package.Length - 22; i >= Math.Max(0, package.Length - 65557); i--)
        {
            if (ReadU32(package, i) != 0x06054B50) { continue; }
            if (ReadU16(package, i + 20) <= package.Length - i - 22) { end = i; break; }
        }
        if (end < 0 || ReadU16(package, end + 4) != 0 || ReadU16(package, end + 6) != 0)
        {
            throw Invalid("不支持多磁盘或损坏的 ZIP 目录");
        }
        var diskCount = ReadU16(package, end + 8);
        var count = ReadU16(package, end + 10);
        var centralOffset = ReadU32(package, end + 16);
        if (diskCount is not (0 or 65535) && diskCount != count)
        {
            throw Invalid("ZIP 磁盘条目数不一致");
        }
        if (count is 0 or > 2000 || centralOffset > end)
        {
            throw Invalid("ZIP 中央目录不合法");
        }

        var entries = new List<ZipEntryLayout>(count);
        var position = checked((int)centralOffset);
        for (var index = 0; index < count; index++)
        {
            if (position > package.Length - 46 || ReadU32(package, position) != 0x02014B50)
            {
                throw Invalid("ZIP 中央目录条目不合法");
            }
            var flags = ReadU16(package, position + 8);
            var method = ReadU16(package, position + 10);
            var compressed = ReadU32(package, position + 20);
            var uncompressed = ReadU32(package, position + 24);
            var nameLength = ReadU16(package, position + 28);
            var extraLength = ReadU16(package, position + 30);
            var commentLength = ReadU16(package, position + 32);
            var localOffset = ReadU32(package, position + 42);
            var next = position + 46 + nameLength + extraLength + commentLength;
            if (compressed == uint.MaxValue || uncompressed == uint.MaxValue || localOffset == uint.MaxValue ||
                compressed > package.Length || next > end || localOffset >= centralOffset)
            {
                throw Invalid("不支持 ZIP64 或非法的 ZIP 条目偏移");
            }
            var nameBytes = package.AsSpan(position + 46, nameLength).ToArray();
            entries.Add(new ZipEntryLayout(flags, method, checked((int)compressed), uncompressed,
                checked((int)localOffset), nameBytes, Encoding.UTF8.GetString(nameBytes)));
            position = next;
        }
        if (position != end) { throw Invalid("ZIP 中央目录长度不一致"); }
        return new ZipPackageLayout(checked((int)centralOffset), entries);
    }

    private static BridgeException Invalid(string message) => new("SOURCE_PACKAGE_INVALID", message);

    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private sealed record ZipEntryLayout(
        ushort Flags,
        ushort Method,
        int CompressedLength,
        long UncompressedLength,
        int LocalOffset,
        byte[] NameBytes,
        string Name);

    private sealed record ZipPackageLayout(int CentralDirectoryOffset, IReadOnlyList<ZipEntryLayout> Entries);
}
