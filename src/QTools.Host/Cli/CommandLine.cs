namespace QTools.Host.Cli;

/// <summary>命令行参数（手写解析，不引 System.CommandLine，保持依赖最小）。</summary>
internal sealed record CommandLineOptions(
    bool SelfTest,
    int? FixedPort,
    string? RootDirectory,
    bool DevMode,
    bool ShowHelp,
    string? OpenToolId,
    string? GeneratePrompt,
    bool AcceptHighSeverity,
    string? DeleteToolId,
    bool DeleteStorage,
    bool OpenSettings);

internal static class CommandLine
{
    public const string Usage = """
        QTools — 个人桌面小工具宿主（.NET 10 Native AOT + PhotinoX + Kestrel）

        用法：
          QTools [选项]

        选项：
          --self-test          启动本地服务并自检（/health、Host 头校验、路径穿越）后立即退出；退出码 0 = 通过
          --port <1-65535>     指定固定端口；默认由操作系统分配随机端口
          --root <目录>        指定数据根目录（tools/、data/ 的父目录）；默认为系统推荐位置
          --open-tool <id>     启动后直接打开指定工具窗口（主窗口仍会显示）
          --open-settings      启动后直接打开独立的设置窗口
          --generate-tool <需求>  用自然语言生成工具并打印审查报告（不弹窗、不保存）
          --yes                配合 --generate-tool：接受安全审查并保存、随后打开预览
          --delete-tool <id>   删除指定工具（不弹窗）；配合 --delete-storage 一并删除其存储
          --delete-storage     配合 --delete-tool：同时删除该工具的存储数据
          --dev                开发者模式：开启右键菜单与 DevTools、详细日志
          --help               显示本帮助
        """;

    public static CommandLineOptions Parse(string[] args)
    {
        var selfTest = false;
        var devMode = false;
        var showHelp = false;
        var acceptHigh = false;
        var deleteStorage = false;
        var openSettings = false;
        int? fixedPort = null;
        string? rootDirectory = null;
        string? openToolId = null;
        string? generatePrompt = null;
        string? deleteToolId = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--self-test":
                    selfTest = true;
                    break;

                case "--dev":
                    devMode = true;
                    break;

                case "--yes":
                    acceptHigh = true;
                    break;

                case "--delete-storage":
                    deleteStorage = true;
                    break;

                case "--open-settings":
                    openSettings = true;
                    break;

                case "--help" or "-h" or "/?":
                    showHelp = true;
                    break;

                case "--port":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var port) || port is < 1 or > 65535)
                    {
                        throw new FormatException("--port 需要 1-65535 之间的整数");
                    }

                    fixedPort = port;
                    break;

                case "--root":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[++i]))
                    {
                        throw new FormatException("--root 需要非空目录路径");
                    }

                    rootDirectory = args[i];
                    break;

                case "--open-tool":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[++i]))
                    {
                        throw new FormatException("--open-tool 需要非空工具 ID");
                    }

                    openToolId = args[i].Trim();
                    break;

                case "--generate-tool":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[++i]))
                    {
                        throw new FormatException("--generate-tool 需要非空需求描述");
                    }

                    generatePrompt = args[i];
                    break;

                case "--delete-tool":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[++i]))
                    {
                        throw new FormatException("--delete-tool 需要非空工具 ID");
                    }

                    deleteToolId = args[i].Trim();
                    break;

                default:
                    throw new FormatException($"未知参数：{args[i]}");
            }
        }

        return new CommandLineOptions(
            selfTest, fixedPort, rootDirectory, devMode, showHelp,
            openToolId, generatePrompt, acceptHigh, deleteToolId, deleteStorage, openSettings);
    }
}
