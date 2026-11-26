using System.Text.Json.Serialization;

namespace QTools.Source;

public sealed class SourceConfigFile
{
    [JsonPropertyName("addr")] public string? Addr { get; init; }
    [JsonPropertyName("data")] public string? Data { get; init; }
    [JsonPropertyName("secureCookie")] public bool? SecureCookie { get; init; }
    [JsonPropertyName("adminPassword")] public string? AdminPassword { get; init; }
}

public sealed record SourceStartupSettings(
    string Address,
    string DataDirectory,
    bool SecureCookie,
    string? AdminPassword,
    string? ConfigPath);

public static class SourceStartup
{
    public const string DefaultConfigFileName = "source-config.json";
    public const string DefaultAdminPassword = "234567";
    public const int MinimumPasswordLength = 6;
    public const int MaximumPasswordLength = 256;
    private const string DefaultAddress = "0.0.0.0:58088";
    private const string DefaultDataDirectory = "./data";

    public static SourceStartupSettings Resolve(
        string[] args,
        Func<string, string?> getEnvironment,
        string workingDirectory)
    {
        string? argumentAddress = null;
        string? argumentData = null;
        string? argumentConfig = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-addr" or "--addr":
                    argumentAddress = RequiredValue(args, ref i, "监听地址");
                    break;
                case "-data" or "--data":
                    argumentData = RequiredValue(args, ref i, "数据目录");
                    break;
                case "-config" or "--config":
                    argumentConfig = RequiredValue(args, ref i, "配置文件");
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[i]}");
            }
        }

        var explicitConfig = argumentConfig ?? NonEmpty(getEnvironment("QTOOLS_SOURCE_CONFIG"));
        var configPath = explicitConfig is null
            ? Path.Combine(workingDirectory, DefaultConfigFileName)
            : Path.GetFullPath(explicitConfig, workingDirectory);
        SourceConfigFile? config = null;
        if (File.Exists(configPath))
        {
            try
            {
                config = JsonHelper.Deserialize<SourceConfigFile>(File.ReadAllBytes(configPath))
                    ?? throw new ArgumentException($"配置文件内容为空：{configPath}");
            }
            catch (System.Text.Json.JsonException error)
            {
                throw new ArgumentException($"配置文件格式错误：{configPath}", error);
            }
        }
        else if (explicitConfig is not null)
        {
            throw new ArgumentException($"配置文件不存在：{configPath}");
        }
        else
        {
            configPath = "";
        }

        var address = argumentAddress
            ?? NonEmpty(getEnvironment("QTOOLS_SOURCE_ADDR"))
            ?? NonEmpty(config?.Addr)
            ?? DefaultAddress;
        var environmentData = NonEmpty(getEnvironment("QTOOLS_SOURCE_DATA"));
        var configData = NonEmpty(config?.Data);
        var dataValue = argumentData
            ?? environmentData
            ?? configData
            ?? DefaultDataDirectory;
        var dataBase = argumentData is null && environmentData is null && configData is not null
            ? Path.GetDirectoryName(configPath)!
            : workingDirectory;
        var dataDirectory = Path.GetFullPath(dataValue, dataBase);

        var secureValue = getEnvironment("QTOOLS_SOURCE_SECURE_COOKIE");
        bool secureCookie;
        if (secureValue is not null)
        {
            secureCookie = secureValue.Equals("true", StringComparison.OrdinalIgnoreCase) || secureValue == "1";
            if (!secureCookie && secureValue is not ("false" or "0"))
            {
                throw new ArgumentException("QTOOLS_SOURCE_SECURE_COOKIE 只能是 true/false 或 1/0");
            }
        }
        else
        {
            secureCookie = config?.SecureCookie ?? false;
        }

        string? adminPassword = null;
        if (!File.Exists(Path.Combine(dataDirectory, "admin.json")))
        {
            adminPassword = NonEmpty(getEnvironment("QTOOLS_SOURCE_ADMIN_PASSWORD"))
                ?? NonEmpty(config?.AdminPassword)
                ?? DefaultAdminPassword;
            if (adminPassword.Length is < MinimumPasswordLength or > MaximumPasswordLength)
            {
                throw new ArgumentException(
                    $"管理员密码长度必须为 {MinimumPasswordLength}~{MaximumPasswordLength} 字符");
            }
        }

        return new SourceStartupSettings(
            address,
            dataDirectory,
            secureCookie,
            adminPassword,
            configPath.Length == 0 ? null : configPath);
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string RequiredValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{name}缺少参数值");
        }

        return args[++index].Trim();
    }
}
