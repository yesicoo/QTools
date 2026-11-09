using System.Security.Cryptography;

namespace QTools.Host.Security;

/// <summary>
/// Windows DPAPI（当前用户作用域）存储：系统级保护，密钥随用户配置文件派生。
/// 非 Windows 平台或调用失败时，透明回退到 <see cref="EncryptedFileSecretStore"/>。
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly EncryptedFileSecretStore _fallback;
    private readonly string _directory;

    public DpapiSecretStore(string secretsDirectory)
    {
        _directory = secretsDirectory;
        _fallback = new EncryptedFileSecretStore(secretsDirectory);
    }

    public string ModeName => "Windows DPAPI（当前用户）";

    public bool IsConfigured =>
        (OperatingSystem.IsWindows() && TryReadDpapi("QTools.AI", "default", out _)) || _fallback.IsConfigured;

    private string PathFor(string service, string account) =>
        Path.Combine(_directory, $"dpapi.{service}.{account}.bin");

    /// <summary>仅在 Windows 上调用：读 DPAPI 密文文件并解密。</summary>
    private bool TryReadDpapi(string service, string account, out string? secret)
    {
        secret = null;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var path = PathFor(service, account);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            secret = System.Text.Encoding.UTF8.GetString(plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            // 用户配置文件变化/机器迁移导致无法解密：按未配置处理
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGet(string service, string account, out string? secret)
    {
        if (TryReadDpapi(service, account, out secret))
        {
            return true;
        }

        return _fallback.TryGet(service, account, out secret);
    }

    public void Set(string service, string account, string secret)
    {
        if (OperatingSystem.IsWindows() && TryWriteDpapi(service, account, secret))
        {
            return;
        }

        // 非 Windows 或 DPAPI 写入失败：回退加密文件，保证功能不中断
        _fallback.Set(service, account, secret);
    }

    /// <summary>仅在 Windows 上调用：DPAPI 加密并原子写入。</summary>
    private bool TryWriteDpapi(string service, string account, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var plaintext = System.Text.Encoding.UTF8.GetBytes(secret);
            var protectedBytes = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

            var path = PathFor(service, account);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Clear(string service, string account)
    {
        var path = PathFor(service, account);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // 忽略：回退存储中的副本也会被清理
            }
        }

        _fallback.Clear(service, account);
    }
}
