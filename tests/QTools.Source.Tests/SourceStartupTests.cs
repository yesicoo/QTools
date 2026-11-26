using QTools.Source;
using Xunit;

namespace QTools.Source.Tests;

public sealed class SourceStartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "qtools-source-startup-" + Guid.NewGuid().ToString("n"));

    public SourceStartupTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void 配置文件提供监听地址数据目录首次密码和安全Cookie()
    {
        var configPath = WriteConfig("""
            {
              "addr": "127.0.0.1:9099",
              "data": "./store",
              "secureCookie": true,
              "adminPassword": "234567"
            }
            """);

        var settings = Resolve([], configPath: configPath);

        Assert.Equal("127.0.0.1:9099", settings.Address);
        Assert.Equal(Path.Combine(_directory, "store"), settings.DataDirectory);
        Assert.True(settings.SecureCookie);
        Assert.Equal("234567", settings.AdminPassword);
        Assert.Equal(configPath, settings.ConfigPath);
    }

    [Fact]
    public void 配置文件中的相对数据目录按配置文件目录解析()
    {
        var configDirectory = Path.Combine(_directory, "config");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, SourceStartup.DefaultConfigFileName);
        File.WriteAllText(configPath, """
            {
              "data": "./store",
              "adminPassword": "234567"
            }
            """);

        var settings = SourceStartup.Resolve(["--config", configPath], _ => null, _directory);

        Assert.Equal(Path.Combine(configDirectory, "store"), settings.DataDirectory);
    }

    [Fact]
    public void 命令行和环境变量优先于配置文件()
    {
        var configPath = WriteConfig("""
            {
              "addr": "127.0.0.1:9099",
              "data": "./config-data",
              "secureCookie": false,
              "adminPassword": "config-password"
            }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["QTOOLS_SOURCE_ADDR"] = "127.0.0.1:9191",
            ["QTOOLS_SOURCE_DATA"] = "./env-data",
            ["QTOOLS_SOURCE_SECURE_COOKIE"] = "true"
        };

        var settings = SourceStartup.Resolve(
            ["--config", configPath, "--addr", "127.0.0.1:9292", "--data", "./cli-data"],
            key => environment.TryGetValue(key, out var value) ? value : null,
            _directory);

        Assert.Equal("127.0.0.1:9292", settings.Address);
        Assert.Equal(Path.Combine(_directory, "cli-data"), settings.DataDirectory);
        Assert.True(settings.SecureCookie);
    }

    [Fact]
    public void 默认监听所有网卡的58088端口()
    {
        var settings = Resolve([]);
        Assert.Equal("0.0.0.0:58088", settings.Address);
    }

    [Fact]
    public void 已有管理员凭据后重启不再要求密码()
    {
        var dataDirectory = Path.Combine(_directory, "data");
        Directory.CreateDirectory(dataDirectory);
        _ = new SourceAuth(dataDirectory, "first-password");
        var configPath = WriteConfig("""
            {
              "data": "./data",
              "adminPassword": ""
            }
            """);

        var settings = Resolve([], configPath: configPath);

        Assert.Null(settings.AdminPassword);
    }

    [Fact]
    public void 首次启动未提供密码时使用默认密码()
    {
        var settings = Resolve([]);
        Assert.Equal(SourceStartup.DefaultAdminPassword, settings.AdminPassword);
        Assert.Equal("234567", settings.AdminPassword);
    }

    [Fact]
    public void 首次启动拒绝过短密码()
    {
        var configPath = WriteConfig("""
            {
              "adminPassword": "12345"
            }
            """);

        var exception = Assert.Throws<ArgumentException>(() => Resolve([], configPath: configPath));
        Assert.Contains("6~256", exception.Message);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_directory, SourceStartup.DefaultConfigFileName);
        File.WriteAllText(path, json);
        return path;
    }

    private SourceStartupSettings Resolve(string[] args, string? configPath = null)
    {
        var allArgs = configPath is null ? args : ["--config", configPath, .. args];
        return SourceStartup.Resolve(allArgs, _ => null, _directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
