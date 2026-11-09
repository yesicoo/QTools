using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QTools.Host.Logging;

/// <summary>
/// 极简滚动文件日志（无外部依赖，AOT 友好）。所有输出经 <see cref="Redact"/> 脱敏，
/// 确保 API Key / Authorization 头等敏感信息不会落盘。
/// </summary>
internal static partial class Log
{
    private const long MaxFileBytes = 5L * 1024 * 1024;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string _filePath = string.Empty;
    private static long _fileBytes;
    private static bool _verbose;

    public static string CurrentFilePath => _filePath;

    public static void Initialize(string logsDirectory, bool verbose = false)
    {
        _verbose = verbose;

        try
        {
            Directory.CreateDirectory(logsDirectory);
        }
        catch (Exception ex)
        {
            // 日志不可用绝不能影响宿主运行
            TryWriteConsole($"[log] 创建日志目录失败：{ex.Message}");
            return;
        }

        // 优先按日期命名；若文件被其他实例占用（单实例保护失效或多开），退化为带 PID 的文件名。
        // 关键：本方法绝不抛异常——曾因未处理 IOException 触发 fail-fast，被系统报成堆栈溢出崩溃。
        var primary = Path.Combine(logsDirectory, $"qtools-{DateTime.Now:yyyyMMdd}.log");
        if (TryOpen(primary))
        {
            return;
        }

        var fallback = Path.Combine(logsDirectory, $"qtools-{DateTime.Now:yyyyMMdd}.{Environment.ProcessId}.log");
        if (TryOpen(fallback))
        {
            TryWriteConsole($"[log] 主日志文件被占用，本次运行改用 {Path.GetFileName(fallback)}");
            return;
        }

        TryWriteConsole("[log] 无法打开任何日志文件，本次运行不写日志");
    }

    private static bool TryOpen(string path)
    {
        try
        {
            // FileMode.Append + FileShare.ReadWrite：多实例可同时追加，且每次 WriteLine 为原子追加
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            _filePath = path;
            _fileBytes = stream.Length;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception ex)
        {
            TryWriteConsole($"[log] 打开 {path} 失败：{ex.Message}");
            return false;
        }
    }

    public static void Debug(string message) => Write("DBG", message, null);

    public static void Info(string message) => Write("INF", message, null);

    public static void Warn(string message) => Write("WRN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        if (_writer is null)
        {
            return;
        }

        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {Redact(message)}";
        if (exception is not null)
        {
            line += $" | {exception.GetType().FullName}: {Redact(exception.Message)}";
            if (_verbose && exception.StackTrace is not null)
            {
                line += Environment.NewLine + Redact(exception.StackTrace);
            }
        }

        lock (Gate)
        {
            try
            {
                if (_fileBytes > MaxFileBytes)
                {
                    Rotate();
                }

                _writer!.WriteLine(line);
                _fileBytes += line.Length + Environment.NewLine.Length;
            }
            catch (Exception)
            {
                // 日志失败绝不影响宿主运行；但也不静默——尽力写到标准错误
                TryWriteConsole(line);
            }
        }
    }

    private static void Rotate()
    {
        _writer!.Dispose();
        _writer = null;

        var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        _filePath = Path.Combine(
            Path.GetDirectoryName(_filePath) ?? ".",
            $"qtools-{DateTime.Now:yyyyMMdd}.{stamp}.log");

        var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _fileBytes = 0;
    }

    private static void TryWriteConsole(string line)
    {
        try
        {
            Console.Error.WriteLine($"[log-failed] {line}");
        }
        catch (Exception)
        {
            // 无控制台时写入会失败，忽略
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception)
            {
                // 忽略关闭异常
            }
            finally
            {
                _writer = null;
            }
        }
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{12,}")]
    private static partial Regex OpenAiStyleKeyPattern();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9._\-]{12,}")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(api[_-]?key\s*[:=]\s*[""']?)[A-Za-z0-9._\-]{12,}")]
    private static partial Regex ApiKeyAssignmentPattern();

    /// <summary>防御性脱敏：日志里绝不出现 Key 或 Authorization 头。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = OpenAiStyleKeyPattern().Replace(text, "sk-***");
        text = BearerPattern().Replace(text, "$1***");
        text = ApiKeyAssignmentPattern().Replace(text, "$1***");
        return text;
    }
}
