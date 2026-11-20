using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using QTools.Host.Logging;
using QTools.Host.Serialization;
using QTools.Host.Tools;

namespace QTools.Host.Launcher;

/// <summary>第二实例经命名管道转交给主实例的请求（一行 JSON）。</summary>
public sealed record SingleInstanceMessage(
    string Cmd,
    string? Id = null,
    string? Title = null,
    string? Body = null)
{
    public const string OpenToolCommand = "open-tool";
    public const string NotifyCommand = "notify";
}

/// <summary>
/// 单实例转发：桌面快捷方式/命令行二次启动时，把请求经命名管道交给已在运行的实例。
/// <para>
/// 主实例在消息循环启动前开始监听；管道用 CurrentUserOnly 限制同用户访问。
/// 仅 Windows 启用——其他平台返回 false / null，单实例保护本身不受影响。
/// </para>
/// </summary>
public static class SingleInstanceOpenTool
{
    private const string PipeName = "QTools.Host.OpenTool";

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>启动监听（主实例）；返回可释放句柄，非 Windows 返回 null。</summary>
    public static IDisposable? StartListener(Func<SingleInstanceMessage, Task<bool>> handler)
    {
        return IsSupported ? new Listener(handler) : null;
    }

    /// <summary>把请求转发给已运行实例（第二实例）；成功返回 true。</summary>
    public static bool TryForward(SingleInstanceMessage message, TimeSpan timeout)
    {
        if (!IsSupported)
        {
            return false;
        }

        if (message.Cmd == SingleInstanceMessage.OpenToolCommand &&
            (string.IsNullOrWhiteSpace(message.Id) || !ToolManifestValidator.ToolIdPattern().IsMatch(message.Id)))
        {
            return false;
        }

        if (message.Cmd == SingleInstanceMessage.NotifyCommand && string.IsNullOrWhiteSpace(message.Title))
        {
            return false;
        }

        return TryForwardAsync(message, timeout).GetAwaiter().GetResult();
    }

    /// <summary>转发失败时给出可见提示——快捷方式没有控制台，静默失败等于「点了没反应」。</summary>
    public static void ShowForwardFailure(string action)
    {
        if (!IsSupported)
        {
            return;
        }

        _ = MessageBoxW(
            IntPtr.Zero,
            $"QTools 已在运行，但{action}的请求转发失败。\n请从托盘退出 QTools 后重试。",
            "QTools",
            0x00000030u); // MB_ICONWARNING
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static async Task<bool> TryForwardAsync(SingleInstanceMessage message, TimeSpan timeout)
    {
        var payload = JsonSerializer.Serialize(message, AppJsonContext.Default.SingleInstanceMessage);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using (var connect = new CancellationTokenSource(300))
                {
                    await client.ConnectAsync(connect.Token).ConfigureAwait(false);
                }

                await using (var writer = new StreamWriter(
                    client, new UTF8Encoding(false), 512, leaveOpen: true))
                {
                    await writer.WriteLineAsync(payload).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }

                using var read = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(client, Encoding.UTF8, false, 512, leaveOpen: true);
                var reply = await reader.ReadLineAsync(read.Token).ConfigureAwait(false);
                return string.Equals(reply, "OK", StringComparison.Ordinal);
            }
            catch (OperationCanceledException)
            {
                // 主实例还没开始监听 → 稍后重试
            }
            catch (IOException)
            {
                // 管道忙 / 监听刚退出 → 稍后重试
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            await Task.Delay(120).ConfigureAwait(false);
        }

        return false;
    }

    private sealed class Listener : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public Listener(Func<SingleInstanceMessage, Task<bool>> handler)
        {
            _loop = Task.Run(() => LoopAsync(handler, _cts.Token));
        }

        private static async Task LoopAsync(
            Func<SingleInstanceMessage, Task<bool>> handler, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    string? line;
                    using (var reader = new StreamReader(server, Encoding.UTF8, false, 512, leaveOpen: true))
                    {
                        line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    }

                    var ok = false;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        try
                        {
                            var message = JsonSerializer.Deserialize(
                                line, AppJsonContext.Default.SingleInstanceMessage);
                            if (message is not null && !string.IsNullOrWhiteSpace(message.Cmd))
                            {
                                ok = await handler(message).ConfigureAwait(false);
                            }
                        }
                        catch (JsonException)
                        {
                            Log.Warn("单实例转发：无法解析请求（可能是旧版本客户端）");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"单实例转发处理失败：{ex.Message}");
                        }
                    }

                    await using (var writer = new StreamWriter(
                        server, new UTF8Encoding(false), 512, leaveOpen: true))
                    {
                        await writer.WriteLineAsync(ok ? "OK" : "ERR").ConfigureAwait(false);
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"单实例转发监听异常：{ex.Message}");
                }

                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // 退出路径不阻塞
            }

            _cts.Dispose();
        }
    }
}
