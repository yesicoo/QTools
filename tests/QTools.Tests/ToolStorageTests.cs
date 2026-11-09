using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Storage;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// 工具存储：按 toolId 隔离、原子写、容错（对应验收标准 #4）。
/// </summary>
public sealed class ToolStorageTests : IDisposable
{
    private readonly string _directory;

    public ToolStorageTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qtools-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败不影响结论
        }
    }

    private static ToolStorage CreateStorage() => new(Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "test-storage-" + Guid.NewGuid().ToString("n")));

    [Fact]
    public async Task SetThenGetRoundTrip()
    {
        var storage = CreateStorage();
        try
        {
            var value = JsonDocument.Parse("{\"text\":\"hello\",\"n\":3}").RootElement;
            await storage.SetAsync("tool-a", "notes", value);

            var result = await storage.GetAsync("tool-a", "notes");
            Assert.NotNull(result);
            Assert.Equal("hello", result.Value.GetProperty("text").GetString());
            Assert.Equal(3, result.Value.GetProperty("n").GetInt32());
        }
        finally
        {
            storage.Delete("tool-a");
        }
    }

    [Fact]
    public async Task MissingKeyReturnsNull()
    {
        var storage = CreateStorage();
        try
        {
            var result = await storage.GetAsync("tool-b", "nope");
            Assert.Null(result);
        }
        finally
        {
            storage.Delete("tool-b");
        }
    }

    [Fact]
    public async Task ToolsAreIsolatedFromEachOther()
    {
        var storage = CreateStorage();
        try
        {
            await storage.SetAsync("tool-a", "shared-key", JsonDocument.Parse("\"A\"").RootElement);
            await storage.SetAsync("tool-b", "shared-key", JsonDocument.Parse("\"B\"").RootElement);

            var a = await storage.GetAsync("tool-a", "shared-key");
            var b = await storage.GetAsync("tool-b", "shared-key");

            Assert.Equal("A", a!.Value.GetString());
            Assert.Equal("B", b!.Value.GetString());

            // 删除一个工具不影响另一个
            storage.Delete("tool-a");
            Assert.Null(await storage.GetAsync("tool-a", "shared-key"));
            Assert.Equal("B", (await storage.GetAsync("tool-b", "shared-key"))!.Value.GetString());
        }
        finally
        {
            storage.Delete("tool-b");
        }
    }

    [Fact]
    public async Task ListReturnsKeysSorted()
    {
        var storage = CreateStorage();
        try
        {
            await storage.SetAsync("tool-c", "zebra", JsonDocument.Parse("1").RootElement);
            await storage.SetAsync("tool-c", "alpha", JsonDocument.Parse("2").RootElement);

            var keys = await storage.ListAsync("tool-c");
            Assert.Equal(new[] { "alpha", "zebra" }, keys);
        }
        finally
        {
            storage.Delete("tool-c");
        }
    }

    [Fact]
    public async Task RemoveDeletesOnlyThatKey()
    {
        var storage = CreateStorage();
        try
        {
            await storage.SetAsync("tool-d", "keep", JsonDocument.Parse("1").RootElement);
            await storage.SetAsync("tool-d", "drop", JsonDocument.Parse("2").RootElement);

            await storage.RemoveAsync("tool-d", "drop");

            var keys = await storage.ListAsync("tool-d");
            Assert.Equal(new[] { "keep" }, keys);
        }
        finally
        {
            storage.Delete("tool-d");
        }
    }

    [Fact]
    public async Task CorruptFileIsBackedUpAndRebuilt()
    {
        var storage = CreateStorage();
        try
        {
            await storage.SetAsync("tool-e", "k", JsonDocument.Parse("\"v\"").RootElement);

            var path = Path.Combine(storage.StorageDirectory, "tool-e.json");
            File.WriteAllText(path, "{ this is not json");

            // 读取不应抛异常，且返回空
            Assert.Null(await storage.GetAsync("tool-e", "k"));

            // 坏文件已被备份
            Assert.True(Directory.GetFiles(storage.StorageDirectory, "tool-e.json.corrupt-*").Length > 0);

            // 可继续写入
            await storage.SetAsync("tool-e", "k", JsonDocument.Parse("\"v2\"").RootElement);
            Assert.Equal("v2", (await storage.GetAsync("tool-e", "k"))!.Value.GetString());
        }
        finally
        {
            storage.Delete("tool-e");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/key")]
    [InlineData("dot.key!")]
    public void InvalidKeysAreRejected(string key)
    {
        var storage = CreateStorage();
        try
        {
            var ex = Assert.Throws<BridgeException>(() => ToolStorage.ValidateKey(key));
            Assert.Equal("STORAGE_KEY_INVALID", ex.Code);
        }
        finally
        {
            storage.Delete("unused");
        }
    }

    [Fact]
    public void ValidKeysAreAccepted()
    {
        ToolStorage.ValidateKey("abc-123_4.5");
    }

    [Fact]
    public async Task AtomicWriteLeavesNoTempFile()
    {
        var storage = CreateStorage();
        try
        {
            await storage.SetAsync("tool-f", "k", JsonDocument.Parse("\"v\"").RootElement);
            Assert.Empty(Directory.GetFiles(storage.StorageDirectory, "*.tmp"));
            Assert.True(File.Exists(Path.Combine(storage.StorageDirectory, "tool-f.json")));
        }
        finally
        {
            storage.Delete("tool-f");
        }
    }
}
