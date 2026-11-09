using System.IO.Compression;
using System.Text;
using QTools.Host.Native;
using Xunit;

namespace QTools.Tests;

public sealed class NativeLibrariesTests
{
    [Fact]
    public void RawResourceRoundTrips()
    {
        var payload = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
        using var source = new MemoryStream(payload);

        Assert.Equal(payload.Length, NativeLibraries.ReadExpectedLength(source, compressed: false));
        Assert.Equal(0, source.Position);

        using var destination = new MemoryStream();
        NativeLibraries.CopyPayload(source, destination, compressed: false, payload.Length);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public void CompressedResourceRoundTrips()
    {
        var payload = SamplePayload();
        var resource = CompressResource(payload);
        Assert.True(resource.Length < payload.Length);

        using var source = new MemoryStream(resource);
        Assert.Equal(payload.Length, NativeLibraries.ReadExpectedLength(source, compressed: true));
        Assert.Equal(4, source.Position);

        using var destination = new MemoryStream();
        NativeLibraries.CopyPayload(source, destination, compressed: true, payload.Length);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public void EmptyCompressedResourceRoundTrips()
    {
        var resource = CompressResource([]);
        using var source = new MemoryStream(resource);
        Assert.Equal(0, NativeLibraries.ReadExpectedLength(source, compressed: true));

        using var destination = new MemoryStream();
        NativeLibraries.CopyPayload(source, destination, compressed: true, 0);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public void DeclaredLengthMismatchThrows()
    {
        var payload = SamplePayload();
        using var source = new MemoryStream(CompressResource(payload));
        var declared = NativeLibraries.ReadExpectedLength(source, compressed: true);

        using var destination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() =>
            NativeLibraries.CopyPayload(source, destination, compressed: true, declared + 1));
    }

    [Fact]
    public void TruncatedCompressedPayloadThrows()
    {
        var payload = SamplePayload();
        var resource = CompressResource(payload);
        using var source = new MemoryStream(resource[..^5]);
        var declared = NativeLibraries.ReadExpectedLength(source, compressed: true);

        using var destination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() =>
            NativeLibraries.CopyPayload(source, destination, compressed: true, declared));
    }

    [Fact]
    public void MissingLengthHeaderThrows()
    {
        using var source = new MemoryStream(new byte[2]);
        Assert.Throws<EndOfStreamException>(() =>
            NativeLibraries.ReadExpectedLength(source, compressed: true));
    }

    private static byte[] SamplePayload() =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("QTools 原生库压缩链路测试数据。", 200)));

    /// <summary>与 csproj 内联压缩任务一致的格式：4 字节小端原始长度 + Deflate 数据。</summary>
    private static byte[] CompressResource(byte[] payload)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)payload.Length);
        output.WriteByte((byte)(payload.Length >> 8));
        output.WriteByte((byte)(payload.Length >> 16));
        output.WriteByte((byte)(payload.Length >> 24));
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload);
        }
        return output.ToArray();
    }
}