using System.Text.Json.Serialization;

namespace QTools.Host.Configuration;

/// <summary>
/// 宿主配置（data/config.json，源生成 JSON；手写默认值，不用反射 binder）。
/// 注意：**不包含 API Key**——Key 只存系统保护存储（见 Security 命名空间）。
/// </summary>
public sealed record AppConfig(
    AiConfigSection Ai,
    ServerConfigSection Server,
    StorageConfigSection Storage,
    BackupConfigSection? Backup = null,
    ShellConfigSection? Shell = null)
{
    public static AppConfig Default { get; } = new(
        new AiConfigSection(
            BaseUrl: "https://api.deepseek.com",
            Model: "deepseek-flash",
            Temperature: 0.7,
            TimeoutSeconds: 60),
        new ServerConfigSection(
            MaxRequestBodyBytes: 1024 * 1024,
            MaxFileBytes: 8 * 1024 * 1024),
        new StorageConfigSection(
            MaxValueBytes: 256 * 1024,
            MaxTotalBytesPerTool: 5 * 1024 * 1024),
        new BackupConfigSection(
            GitRemoteUrl: null,
            LastPushUtc: null,
            LastPushStatus: null),
        new ShellConfigSection(
            HotkeyEnabled: true));
}

public sealed record AiConfigSection(
    string BaseUrl,
    string Model,
    double Temperature,
    int TimeoutSeconds);

public sealed record ServerConfigSection(long MaxRequestBodyBytes, long MaxFileBytes);

public sealed record StorageConfigSection(long MaxValueBytes, long MaxTotalBytesPerTool);

/// <summary>备份配置：Git 远程仓库地址与最近一次推送状态（不含任何凭证——凭证由 git 自身管理）。</summary>
public sealed record BackupConfigSection(
    string? GitRemoteUrl,
    string? LastPushUtc,
    string? LastPushStatus);

/// <summary>
/// 壳配置：全局热键（Alt+Space 呼出/隐藏主窗口，仅 Windows）、主窗口尺寸（DIP，null = 340×720）、
/// 托盘快捷菜单数量（1–20，默认 10）、内容窗口上次的位置（Windows 物理像素，其他平台为 Photino 坐标）
/// 与尺寸（DIP），以及内容窗口/独立工具窗口的置顶与钉住偏好。
/// </summary>
public sealed record ShellConfigSection(
    bool HotkeyEnabled = true,
    int? MainWindowWidth = null,
    int? MainWindowHeight = null,
    int TrayMenuItems = 10,
    int? ContentWindowLeft = null,
    int? ContentWindowTop = null,
    int? ContentWindowWidth = null,
    int? ContentWindowHeight = null,
    bool ContentWindowTopmost = false,
    bool ContentWindowPinned = false,
    Dictionary<string, ToolWindowState>? ToolWindowStates = null);

/// <summary>
/// 独立工具窗口偏好：置顶、钉住、「⋯」按钮在窗口内的位置（DIP），
/// 以及上次的位置（Windows 物理像素，其他平台为 Photino 坐标）与尺寸（DIP），按 toolId 分别记忆。
/// </summary>
public sealed record ToolWindowState(
    bool Topmost = false,
    bool Pinned = false,
    double? ChipLeft = null,
    double? ChipTop = null,
    int? Left = null,
    int? Top = null,
    int? Width = null,
    int? Height = null);
