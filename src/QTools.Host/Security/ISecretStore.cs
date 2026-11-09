using System.Security.Cryptography;

namespace QTools.Host.Security;

/// <summary>
/// 敏感信息（API Key）的系统保护存储抽象。
/// <para>
/// 实现优先级：Windows DPAPI → 本地加密文件回退（AES-GCM + 0600 密钥文件）。
/// 网页侧永远拿不到 Key：它只存在于宿主内存与这里的持久化中。
/// </para>
/// </summary>
public interface ISecretStore
{
    /// <summary>存储模式名（用于宿主 UI 展示，例如 "Windows DPAPI" / "本地加密文件"）。</summary>
    string ModeName { get; }

    /// <summary>是否已配置（用于 UI 展示，绝不返回 Key 本身）。</summary>
    bool IsConfigured { get; }

    bool TryGet(string service, string account, out string? secret);

    void Set(string service, string account, string secret);

    void Clear(string service, string account);
}

/// <summary>
/// AES-GCM 加密文件存储：跨平台回退实现。
/// <para>
/// 密钥为 <c>data/secrets/key.bin</c>（32 字节随机数，Unix 下 0600）。
/// 每个密文文件 = nonce(12) + tag(16) + ciphertext。
/// </para>
/// <para>
/// 强度说明：可防止「文件被拷贝/同步/备份泄露」；无法抵御已获得同一用户账户权限的攻击者
/// （密钥与该用户可读）。因此 Windows 上优先用 DPAPI，并由 UI 明示当前所处模式。
/// </para>
/// </summary>
public sealed class EncryptedFileSecretStore : ISecretStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly string _directory;
    private readonly byte[] _key;

    public EncryptedFileSecretStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);

        var keyPath = Path.Combine(directory, "key.bin");
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
            if (_key.Length != KeySize)
            {
                // 密钥损坏：重建（旧密文将无法解密，需重新录入 Key）
                _key = RandomNumberGenerator.GetBytes(KeySize);
                File.WriteAllBytes(keyPath, _key);
                RestrictPermissions(keyPath);
            }
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllBytes(keyPath, _key);
            RestrictPermissions(keyPath);
        }
    }

    public string ModeName => "本地加密文件（AES-GCM）";

    public bool IsConfigured => File.Exists(FilePath("QTools.AI", "default"));

    private string FilePath(string service, string account) =>
        Path.Combine(_directory, $"{Sanitize(service)}.{Sanitize(account)}.bin");

    private static string Sanitize(string value)
    {
        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            buffer[i] = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_';
        }

        return new string(buffer);
    }

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // 某些文件系统不支持 Unix 权限模式；忽略（密钥内容仍是随机的）
        }
    }

    public bool TryGet(string service, string account, out string? secret)
    {
        var path = FilePath(service, account);
        if (!File.Exists(path))
        {
            secret = null;
            return false;
        }

        try
        {
            var buffer = File.ReadAllBytes(path);
            if (buffer.Length < NonceSize + TagSize)
            {
                secret = null;
                return false;
            }

            var nonce = buffer[..NonceSize];
            var tag = buffer[NonceSize..(NonceSize + TagSize)];
            var ciphertext = buffer[(NonceSize + TagSize)..];

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            secret = System.Text.Encoding.UTF8.GetString(plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            // 密钥不匹配或文件损坏：视为未配置，交由调用方重新录入
            secret = null;
            return false;
        }
        catch (Exception)
        {
            secret = null;
            return false;
        }
    }

    public void Set(string service, string account, string secret)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var path = FilePath(service, account);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, [.. nonce, .. tag, .. ciphertext]);
        File.Move(temp, path, overwrite: true);
        RestrictPermissions(path);
    }

    public void Clear(string service, string account)
    {
        var path = FilePath(service, account);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>按平台选择实现；无可用系统保护存储时回退到加密文件。</summary>
public static class SecretStoreFactory
{
    public const string AiServiceName = "QTools.AI";
    public const string AiAccountName = "default";

    public static ISecretStore Create(string dataDirectory)
    {
        var secretsDirectory = Path.Combine(dataDirectory, "secrets");

        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretStore(secretsDirectory);
        }

        return new EncryptedFileSecretStore(secretsDirectory);
    }
}
