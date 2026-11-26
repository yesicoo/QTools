using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace QTools.Source;

public static class SemanticVersion
{
    public static int Compare(string left, string right)
    {
        var a = Split(left);
        var b = Split(right);
        for (var i = 0; i < 3; i++)
        {
            var comparison = CompareNumeric(a.Core[i], b.Core[i]);
            if (comparison != 0) { return comparison; }
        }

        if (a.PreRelease == b.PreRelease) { return 0; }
        if (a.PreRelease.Length == 0) { return 1; }
        if (b.PreRelease.Length == 0) { return -1; }

        var aParts = a.PreRelease.Split('.');
        var bParts = b.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(aParts.Length, bParts.Length); i++)
        {
            var aNumeric = aParts[i].All(char.IsAsciiDigit);
            var bNumeric = bParts[i].All(char.IsAsciiDigit);
            int comparison;
            if (aNumeric && bNumeric)
            {
                comparison = CompareNumeric(aParts[i], bParts[i]);
            }
            else if (aNumeric)
            {
                comparison = -1;
            }
            else if (bNumeric)
            {
                comparison = 1;
            }
            else
            {
                comparison = string.CompareOrdinal(aParts[i], bParts[i]);
            }

            if (comparison != 0) { return comparison; }
        }

        return aParts.Length.CompareTo(bParts.Length);
    }

    private static (string[] Core, string PreRelease) Split(string value)
    {
        var parts = value.Split('-', 2);
        var core = parts[0].Split('.');
        while (core.Length < 3) { core = [.. core, "0"]; }
        return (core, parts.Length > 1 ? parts[1] : "");
    }

    private static int CompareNumeric(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        if (left.Length == 0) { left = "0"; }
        if (right.Length == 0) { right = "0"; }
        if (left.Length != right.Length) { return left.Length.CompareTo(right.Length); }
        return string.CompareOrdinal(left, right);
    }
}

public static class JsonHelper
{
    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, SourceJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"类型 {typeof(T)} 未注册源生成 JSON"));

    public static byte[] SerializeRuntime(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, SourceJsonContext.Default.GetTypeInfo(value.GetType())
            ?? throw new NotSupportedException($"类型 {value.GetType()} 未注册源生成 JSON"));

    public static T? Deserialize<T>(byte[] data) where T : class =>
        JsonSerializer.Deserialize(data, (JsonTypeInfo<T>)(SourceJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"类型 {typeof(T)} 未注册源生成 JSON")));
}

public static class AtomicFile
{
    public static void Write(string path, byte[] data, bool privateFile)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".tmp-" + Guid.NewGuid().ToString("n"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            if (privateFile && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }
}

public static class TextUtility
{
    public static int RuneCount(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) { count++; }
        return count;
    }

    public static bool ConstantTimeEquals(string left, string right)
    {
        if (left.Length == 0 || left.Length != right.Length) { return false; }
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
    }
}
