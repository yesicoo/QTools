using System.Security.Cryptography;
using System.Text;

namespace QTools.Source;

public sealed class AuthSession
{
    public required string CsrfToken { get; init; }
    public required DateTimeOffset Expires { get; init; }
}

public sealed class SourceAuth
{
    public const string CookieName = "qtools_admin";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly object _sync = new();
    private readonly Dictionary<string, AuthSession> _sessions = new(StringComparer.Ordinal);
    private readonly PasswordRecord _password;

    public SourceAuth(string dataDirectory, string? password)
    {
        var path = Path.Combine(dataDirectory, "admin.json");
        if (File.Exists(path))
        {
            _password = JsonHelper.Deserialize<PasswordRecord>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("管理员凭据文件为空");
            if (_password.Algorithm != "pbkdf2-sha256-120000" ||
                Convert.FromBase64String(_password.Salt).Length != 16 ||
                Convert.FromBase64String(_password.Hash).Length != 32)
            {
                throw new InvalidDataException("管理员凭据文件内容不受支持");
            }
        }
        else
        {
            if (password is null ||
                password.Length is < SourceStartup.MinimumPasswordLength or > SourceStartup.MaximumPasswordLength)
            {
                throw new ArgumentException(
                    $"首次启动的管理员密码长度必须为 {SourceStartup.MinimumPasswordLength}~{SourceStartup.MaximumPasswordLength}");
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            _password = new PasswordRecord
            {
                Algorithm = "pbkdf2-sha256-120000",
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(Derive(salt, password)),
            };
            AtomicFile.Write(path, JsonHelper.Serialize(_password), privateFile: true);
        }
    }

    public (string Token, string CsrfToken) Login(string password)
    {
        lock (_sync)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Derive(Convert.FromBase64String(_password.Salt), password),
                    Convert.FromBase64String(_password.Hash)))
            {
                throw new UnauthorizedAccessException("密码错误");
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var token in _sessions.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
            {
                _sessions.Remove(token);
            }

            var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _sessions[sessionToken] = new AuthSession { CsrfToken = csrf, Expires = now.Add(SessionLifetime) };
            return (sessionToken, csrf);
        }
    }

    public AuthSession? Validate(string token)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(token, out var session)) { return null; }
            if (session.Expires <= DateTimeOffset.UtcNow)
            {
                _sessions.Remove(token);
                return null;
            }
            return session;
        }
    }

    public void Logout(string token)
    {
        lock (_sync) { _sessions.Remove(token); }
    }

    public static string BuildCookie(string token, bool secure, bool clear = false)
    {
        var value = clear ? "" : token;
        var expiry = clear
            ? "Max-Age=-1"
            : "Expires=" + DateTimeOffset.UtcNow.Add(SessionLifetime).ToString("R");
        return $"{CookieName}={value}; Path=/; HttpOnly; SameSite=Strict; {(secure ? "Secure; " : "")}{expiry}";
    }

    public static string? ReadCookie(SourceHttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue("Cookie", out var header)) { return null; }
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && part[..equals].Equals(name, StringComparison.Ordinal))
            {
                return part[(equals + 1)..];
            }
        }
        return null;
    }

    public static bool ValidOrigin(SourceHttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origin) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
        {
            return false;
        }
        return parsed.Scheme is "http" or "https" &&
               string.IsNullOrEmpty(parsed.UserInfo) &&
               (parsed.AbsolutePath is "" or "/") &&
               string.IsNullOrEmpty(parsed.Query) &&
               string.IsNullOrEmpty(parsed.Fragment) &&
               parsed.Authority.Equals(request.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Derive(byte[] salt, string password) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
}

public sealed class RateLimiter
{
    public const int MaxTrackedKeys = 4096;

    private readonly object _sync = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _entries = new(StringComparer.Ordinal);

    public int TrackedKeyCount
    {
        get
        {
            lock (_sync) { return _entries.Count; }
        }
    }

    public bool Allow(string key, int limit, TimeSpan window)
    {
        if (string.IsNullOrEmpty(key) || limit < 1 || window <= TimeSpan.Zero) { return false; }

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_entries.TryGetValue(key, out var timestamps))
            {
                if (_entries.Count >= MaxTrackedKeys)
                {
                    var expired = _entries
                        .Where(item => item.Value.Count == 0 || now - item.Value[^1] >= window)
                        .Select(item => item.Key)
                        .ToArray();
                    foreach (var expiredKey in expired) { _entries.Remove(expiredKey); }
                    if (_entries.Count >= MaxTrackedKeys) { return false; }
                }

                timestamps = [];
                _entries[key] = timestamps;
            }
            timestamps.RemoveAll(item => now - item >= window);
            if (timestamps.Count >= limit) { return false; }
            timestamps.Add(now);
            return true;
        }
    }
}
