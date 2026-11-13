using System.Text.Json;
using Photino.NET;
using QTools.Host.Cli;
using QTools.Host.Configuration;
using QTools.Host.Launcher;
using QTools.Host.Logging;
using QTools.Host.Native;
using QTools.Host.Serialization;
using QTools.Host.Server;
using QTools.Host.Shell;

namespace QTools.Host;

/// <summary>
/// 组合根：解析 CLI → 初始化路径/日志 → 自举原生库 → 启动 Kestrel → PhotinoX 消息循环。
/// 错误绝不静默：任何启动失败都会写日志文件 + 标准错误 + 非零退出码。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CommandLineOptions options;
        try
        {
            options = CommandLine.Parse(args);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(CommandLine.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Out.WriteLine(CommandLine.Usage);
            return 0;
        }

        AppPaths.Initialize(options.RootDirectory);
        Log.Initialize(AppPaths.LogsDirectory, options.DevMode);

        Log.Info($"数据根目录：{AppPaths.RootDirectory}");

        // 旧版数据存放在可执行文件同级：给出迁移提示（不自动搬迁，避免误判）
        var legacy = AppPaths.DetectLegacyRoot();
        if (legacy is not null)
        {
            Log.Warn($"检测到旧版数据目录 {legacy}；自本版本起数据存放在系统推荐位置 {AppPaths.RootDirectory}，如需迁移请手动复制。");
        }

        // 必须在任何 PhotinoX API 之前执行：解压内嵌原生库并注册 DllImportResolver
        NativeLibraries.Bootstrap(AppPaths.DataDirectory);

        Log.Info($"QTools v{AppInfo.Version} 启动 | {PlatformInfo.PlatformName} / {PlatformInfo.WebViewName} | OS={Environment.OSVersion} | 64位={Environment.Is64BitProcess} | PID={Environment.ProcessId} | 参数=[{string.Join(' ', args)}]");

        // 单实例保护：避免两个宿主同时运行造成端口/窗口/存储混乱
        using var singleInstance = new Mutex(initiallyOwned: true, "QTools.Host.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 桌面快捷方式：把「打开工具」转交给已运行实例（托盘里的 QTools 可能没有主窗口）
            if (!string.IsNullOrWhiteSpace(options.OpenToolId))
            {
                var forwarded = SingleInstanceOpenTool.TryForward(options.OpenToolId, TimeSpan.FromSeconds(3));
                Log.Warn($"QTools 已在运行，转发打开工具 {options.OpenToolId}：{(forwarded ? "成功" : "失败")}");
                if (!forwarded)
                {
                    SingleInstanceOpenTool.ShowForwardFailure(options.OpenToolId);
                }

                return forwarded ? 0 : 4;
            }

            const string message = "QTools 已在运行中（单实例模式），本次启动被忽略。";
            Log.Warn(message);
            Console.Error.WriteLine(message);
            return 0;
        }

        try
        {
            // ASPNETCORE_URLS 可能覆盖我们传入的监听地址；"只绑定 127.0.0.1" 的策略不允许被环境变量绕过
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);

            using var server = LocalServer.Start(options.FixedPort);
            Log.Info($"本地静态服务已监听 http://127.0.0.1:{server.Port}/（仅回环，随机端口）；已加载 {server.Tools.GetAll().Count} 个工具");

            // 自动化/CI 通道：从环境变量注入 AI Key（不进命令行参数，避免进程列表泄露）
            SeedAiKeyFromEnvironment(server);

            if (options.SelfTest)
            {
                return SelfTest.Run(server.Port);
            }

            // 无头生成模式：打印审查报告，--yes 时保存并打开（供自动化验收）
            if (!string.IsNullOrWhiteSpace(options.GeneratePrompt))
            {
                return GenerateHeadless(server, options.GeneratePrompt, options.AcceptHighSeverity);
            }

            // 无头删除模式（供自动化验收）
            if (!string.IsNullOrWhiteSpace(options.DeleteToolId))
            {
                return DeleteHeadless(server, options.DeleteToolId, options.DeleteStorage);
            }

            var shell = new ShellHost(new PhotinoApplication(), server.Tools, server.Storage, server.Dispatcher);
            ConfigureTray(shell, server);

            // 后台索引完成 → 主窗口统一列表自动刷新（旧索引在扫描期间持续可用）
            server.ProgramIndex.Completed += shell.NotifyLauncherChanged;

            if (options.OpenSettings)
            {
                // 在消息循环启动前登记：ShellHost.Run 里开完主窗口立即打开设置窗口
                shell.QueueSettingsToOpen();
            }

            if (!string.IsNullOrWhiteSpace(options.OpenToolId))
            {
                var tool = server.Tools.Find(options.OpenToolId);
                if (tool is null || !tool.IsValid)
                {
                    Console.Error.WriteLine($"QTools: 工具不存在或 manifest 无效：{options.OpenToolId}");
                    return 3;
                }

                // 在消息循环启动前登记待打开工具：ShellHost.Run 里开完主窗口立即打开它
                shell.QueueToolToOpen(tool);
            }

            // 桌面快捷方式二次启动 → 命名管道转发；监听必须在消息循环外启动（UI 线程只负责开窗）
            using var openToolListener = SingleInstanceOpenTool.StartListener(async toolId =>
            {
                var tool = server.Tools.Find(toolId);
                if (tool is null || !tool.IsValid)
                {
                    Log.Warn($"单实例转发：工具不存在或无效：{toolId}");
                    return false;
                }

                Log.Info($"单实例转发：打开工具窗口 {toolId}");
                await shell.OpenToolAsync(tool).ConfigureAwait(false);
                return true;
            });

            var exitCode = shell.Run(server.Port, options.DevMode);
            Log.Info("宿主正常退出");
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Error("宿主启动或运行失败", ex);
            Console.Error.WriteLine($"QTools 启动失败：{ex.Message}");
            Console.Error.WriteLine($"详细日志：{Log.CurrentFilePath}");
            return 1;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    /// <summary>
    /// 托盘菜单：前 N 项（默认 10，可配置 1–20）取自统一列表（与界面同一排序/过滤），由 HTML 弹层渲染；
    /// 点击条目走与界面相同的 manager.openContent / openWebTool / launchApp，「退出 QTools」走 manager.exitApp。
    /// </summary>
    private static void ConfigureTray(ShellHost shell, Server.LocalServer server)
    {
        shell.TrayEntriesProvider = () =>
        {
            // 每次右键实时读配置：设置页改数量后无需重开应用
            var config = QTools.Host.Ai.ConfigStore.Load(
                QTools.Host.Configuration.AppPaths.ConfigFilePath);
            var count = TrayMenuPlanner.ResolveCount(config.Shell?.TrayMenuItems);
            return TrayMenuPlanner.FromLauncherItems(
                LauncherListBuilder.Build(
                    server.Tools,
                    server.Apps,
                    server.ProgramIndex,
                    server.Usage,
                    QTools.Host.Configuration.AppPaths.IconCacheDirectory,
                    server.WebTools,
                    server.HiddenPrograms).Items,
                count);
        };
    }

    /// <summary>
    /// 从环境变量 QTOOLS_AI_KEY 注入 AI Key（供自动化验收/CI；日常请在宿主界面填写）。
    /// Key 只写入系统保护存储，日志中绝不出现。
    /// </summary>
    private static void SeedAiKeyFromEnvironment(Server.LocalServer server)
    {
        var key = Environment.GetEnvironmentVariable("QTOOLS_AI_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        server.Secrets.Set(
            Security.SecretStoreFactory.AiServiceName,
            Security.SecretStoreFactory.AiAccountName,
            key);

        Log.Info("已从环境变量注入 AI Key 到系统保护存储");
    }

    /// <summary>
    /// 无头删除模式：删除工具目录，按需删除其存储，并报告前后状态。
    /// 退出码：0=成功；7=工具不存在。
    /// </summary>
    private static int DeleteHeadless(Server.LocalServer server, string toolId, bool deleteStorage)
    {
        var tool = server.Tools.Find(toolId);
        if (tool is null)
        {
            Console.Error.WriteLine($"工具不存在：{toolId}");
            return 7;
        }

        var storagePath = Path.Combine(
            QTools.Host.Configuration.AppPaths.StorageDirectory, toolId + ".json");
        var hadStorage = File.Exists(storagePath);
        var storageBytes = hadStorage ? new FileInfo(storagePath).Length : 0;

        Console.Out.WriteLine($"删除工具 {toolId}（目录 {tool.Directory}）");
        Console.Out.WriteLine($"  删除存储：{(deleteStorage ? "是" : "否")}；删除前存在存储文件：{(hadStorage ? $"是（{storageBytes} 字节）" : "否")}");

        try
        {
            var shell = new ShellHost(new PhotinoApplication(), server.Tools, server.Storage, server.Dispatcher);
            // 无头模式没有消息循环：只做纯 I/O 的删除（关窗口在无头模式下无意义）
            shell.DeleteToolFilesAsync(tool, deleteStorage).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("无头删除未处理异常", ex);
            Console.Error.WriteLine($"删除失败：{ex.Message}");
            return 6;
        }

        var directoryGone = !Directory.Exists(tool.Directory);
        var storageGone = !File.Exists(storagePath);
        var stillListed = server.Tools.Find(toolId) is not null;

        Console.Out.WriteLine($"  工具目录已删除：{directoryGone}");
        Console.Out.WriteLine($"  存储文件已删除：{(deleteStorage ? storageGone.ToString() : "未要求删除")}");
        Console.Out.WriteLine($"  列表中已移除：{!stillListed}");
        Console.Out.WriteLine($"  剩余工具：{string.Join(", ", server.Tools.GetAll().Select(t => t.Id))}");

        var ok = directoryGone && !stillListed && (!deleteStorage || storageGone);
        Console.Out.WriteLine(ok ? "删除完成" : "删除结果不符合预期");
        return ok ? 0 : 6;
    }

    /// <summary>
    /// 无头生成模式：调用 AI 生成 → 宿主安全审查 → 打印报告；--yes 时保存并打开预览。
    /// 退出码：0=成功；4=AI 未配置或调用失败；5=内容被安全审查拒绝；6=保存失败。
    /// </summary>
    private static int GenerateHeadless(Server.LocalServer server, string prompt, bool acceptHighSeverity)
    {
        Creator.GeneratedToolResult result;
        try
        {
            result = server.Creator.GenerateAsync(prompt, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Bridge.BridgeException ex)
        {
            Console.Error.WriteLine($"生成失败：{ex.Code} {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            Log.Error("无头生成未处理异常", ex);
            Console.Error.WriteLine($"生成失败：{ex.Message}");
            return 4;
        }

        Console.Out.WriteLine("=== 安全审查结果 ===");
        foreach (var finding in result.Findings)
        {
            var level = finding.Severity switch
            {
                Creator.SecuritySeverity.Fatal => "致命",
                Creator.SecuritySeverity.High => "高危",
                _ => "提示",
            };
            Console.Out.WriteLine($"  [{level}] {finding.Code}: {finding.Message}");
        }

        if (result.Findings.Count == 0)
        {
            Console.Out.WriteLine("  （未发现安全问题）");
        }

        Console.Out.WriteLine("=== manifest.json ===");
        Console.Out.WriteLine(result.Manifest.Id + " / " + result.Manifest.Name + " v" + result.Manifest.Version);
        Console.Out.WriteLine("  permissions: " + string.Join(",", result.Manifest.Permissions));
        Console.Out.WriteLine($"  indexHtml: {result.IndexHtml.Length} 字符");

        if (result.HasFatal)
        {
            Console.Error.WriteLine("存在致命问题，拒绝保存");
            return 5;
        }

        if (!acceptHighSeverity)
        {
            Console.Out.WriteLine("未指定 --yes，仅打印审查报告，不保存");
            return 0;
        }

        try
        {
            var toolId = server.Creator.CreateAsync(result, acceptHighSeverity: true, CancellationToken.None)
                .GetAwaiter().GetResult();

            Console.Out.WriteLine($"已保存工具 {toolId}");

            var shell = new ShellHost(new PhotinoApplication(), server.Tools, server.Storage, server.Dispatcher);
            var tool = server.Tools.Find(toolId);
            if (tool is not null)
            {
                shell.QueueToolToOpen(tool);
            }

            shell.Run(server.Port, devMode: false, enableTray: false);
            return 0;
        }
        catch (Bridge.BridgeException ex)
        {
            Console.Error.WriteLine($"保存失败：{ex.Code} {ex.Message}");
            return ex.Code == "CONTENT_SECURITY_VIOLATION" ? (char)5 : (char)6;
        }
        catch (Exception ex)
        {
            Log.Error("无头保存未处理异常", ex);
            Console.Error.WriteLine($"保存失败：{ex.Message}");
            return 6;
        }
    }
}
