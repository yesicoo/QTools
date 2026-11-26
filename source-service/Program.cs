using System.Net;
using System.Runtime.InteropServices;
using QTools.Source;

namespace QTools.Source;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => argument is "-h" or "--help"))
        {
            Console.WriteLine("QTools 工具源服务（.NET 10）\n\n用法：QTools.Source [选项]\n\n  -addr, --addr <地址>                 监听地址，默认 0.0.0.0:58088\n  -data, --data <目录>                 数据目录，默认 ./data\n  -config, --config <文件>             配置文件，默认 ./source-config.json\n  -h, --help                           显示帮助\n\n环境变量：\n  QTOOLS_SOURCE_ADMIN_PASSWORD  首次启动的管理员初始密码，默认 234567\n  QTOOLS_SOURCE_ADDR            默认监听地址\n  QTOOLS_SOURCE_DATA            默认数据目录\n  QTOOLS_SOURCE_CONFIG          默认配置文件\n  QTOOLS_SOURCE_SECURE_COOKIE   设为 true 时为会话 Cookie 添加 Secure");
            return 0;
        }

        try
        {
            var settings = SourceStartup.Resolve(
                args,
                Environment.GetEnvironmentVariable,
                Environment.CurrentDirectory);
            var (address, port) = ParseAddress(settings.Address);
            Directory.CreateDirectory(settings.DataDirectory);
            var store = new SourceStore(settings.DataDirectory);
            if (!File.Exists(Path.Combine(store.DataDirectory, "admin.json")) &&
                settings.AdminPassword == SourceStartup.DefaultAdminPassword)
            {
                Console.WriteLine("警告：正在使用默认管理员密码，请修改配置后删除 data/admin.json 重新初始化。");
            }

            var auth = new SourceAuth(store.DataDirectory, settings.AdminPassword);
            var application = new SourceApplication(store, auth, settings.SecureCookie);
            await using var server = new SourceHttpServer(address, port, application.HandleAsync);
            server.Start();

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            using var sigterm = OperatingSystem.IsWindows() ? null :
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    shutdown.Cancel();
                    context.Cancel = true;
                });
            try
            {
                Console.WriteLine($"QTools 工具源已启动：http://{settings.Address}");
                Console.WriteLine($"数据目录：{store.DataDirectory}");
                Console.WriteLine(settings.ConfigPath is null
                    ? "配置文件：未使用"
                    : $"配置文件：{settings.ConfigPath}");
                await Task.Delay(Timeout.Infinite, shutdown.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                Console.WriteLine("QTools 工具源正在停止…");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"启动失败：{exception.Message}");
            return 1;
        }
    }

    private static (IPAddress Address, int Port) ParseAddress(string value)
    {
        string host;
        string portText;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0 || end + 1 >= value.Length || value[end + 1] != ':') { throw new ArgumentException("监听地址格式不合法"); }
            host = value[1..end];
            portText = value[(end + 2)..];
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0) { throw new ArgumentException("监听地址格式应为 主机:端口"); }
            host = value[..colon];
            portText = value[(colon + 1)..];
        }
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535) { throw new ArgumentException("监听端口不合法"); }
        if (host == "localhost") { host = "127.0.0.1"; }
        if (host == "*") { host = IPAddress.Any.ToString(); }
        if (!IPAddress.TryParse(host, out var address)) { throw new ArgumentException("监听主机必须是 IP 地址或 localhost"); }
        return (address, port);
    }
}
