using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QTools.Host.Logging;

namespace QTools.Host.Server;

/// <summary>
/// 轻量本地 HTTP/1.1 服务：只监听回环地址，单请求单连接（响应后关闭），只处理 GET / HEAD。
/// 替代 ASP.NET Core/Kestrel，避免为几个只读静态资源引入完整 Web 框架。
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxRequestTargetLength = 4096;
    private const int MaxHeaderCount = 100;
    private const int MaxConnections = 64;
    private const int IoTimeoutMs = 15_000;

    private readonly TcpListener _listener;
    private readonly Func<LocalHttpRequest, LocalHttpResponse, Task> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private int _clientSerial;
    private Task? _acceptLoop;

    public LocalHttpServer(int port, Func<LocalHttpRequest, LocalHttpResponse, Task> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>实际监听端口（固定端口启动失败时由调用方处理异常）。</summary>
    public int Port { get; }

    public void Start()
    {
        _acceptLoop ??= AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Warn($"本地 HTTP 接受连接失败：{ex.Message}");
                continue;
            }

            var serial = Interlocked.Increment(ref _clientSerial);
            _clients[serial] = client;
            _ = HandleClientAsync(client, serial);
        }
    }

    private async Task HandleClientAsync(TcpClient client, int serial)
    {
        var acquired = false;
        try
        {
            await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            acquired = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(IoTimeoutMs);

            client.NoDelay = true;
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            var response = new LocalHttpResponse();
            if (request is null)
            {
                response.SetText(400, "Bad Request");
            }
            else
            {
                await _handler(request, response).ConfigureAwait(false);
            }

            await WriteResponseAsync(stream, request, response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止或请求超时：不记录为错误
        }
        catch (IOException)
        {
            // 浏览器取消导航/关闭连接时常见，静默处理
        }
        catch (Exception ex)
        {
            Log.Warn($"本地 HTTP 连接处理失败：{ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                _slots.Release();
            }

            _clients.TryRemove(serial, out _);
            client.Dispose();
        }
    }

    /// <summary>
    /// 解析请求头。只读取到 \r\n\r\n 为止，单连接只处理一个请求；
    /// 请求体不读取（服务只有只读 GET/HEAD）。
    /// </summary>
    private static async Task<LocalHttpRequest?> ReadRequestAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var header = new ArrayBufferWriter<byte>(1024);
        var headerEnd = -1;

        while (header.WrittenCount < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            header.Write(buffer.AsSpan(0, read));
            headerEnd = header.WrittenSpan.IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0 || headerEnd > MaxHeaderBytes)
        {
            return null;
        }

        var text = Encoding.Latin1.GetString(header.WrittenSpan[..headerEnd]);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 ||
            requestLine[1].Length is 0 or > MaxRequestTargetLength ||
            requestLine[1][0] != '/' ||
            !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return null;
        }

        string? host = null;
        var headerCount = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            if (++headerCount > MaxHeaderCount)
            {
                return null;
            }

            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            // 拒绝 obs-fold 与非法头行，避免请求走私/解析歧义
            if (line[0] is ' ' or '\t')
            {
                return null;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return null;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!IsValidHeaderText(name) || !IsValidHeaderText(value))
            {
                return null;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (host is not null)
                {
                    return null;
                }

                host = value;
            }
        }

        var rawTarget = requestLine[1];
        var query = rawTarget.IndexOf('?');
        var rawPath = query < 0 ? rawTarget : rawTarget[..query];

        string path;
        try
        {
            path = Uri.UnescapeDataString(rawPath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (!IsValidPath(path))
        {
            return null;
        }

        return new LocalHttpRequest
        {
            Method = requestLine[0],
            Path = path,
            Host = host ?? string.Empty,
        };
    }

    private static bool IsValidHeaderText(string text)
    {
        foreach (var c in text)
        {
            if (c < 0x20 && c != '\t')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPath(string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c == '\\' || c < 0x20 || c == 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, LocalHttpRequest? request, LocalHttpResponse response,
        CancellationToken cancellationToken)
    {
        var body = response.Body;
        var text = new StringBuilder(256);
        text.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ')
            .Append(ReasonPhrase(response.StatusCode)).Append("\r\n");
        text.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (!string.IsNullOrEmpty(response.ContentType))
        {
            text.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        }

        foreach (var (name, value) in response.Headers)
        {
            // 内部代码生成的头，仍做一次换行过滤，避免任何注入风险
            if (name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
            {
                continue;
            }

            text.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        text.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(text.ToString());
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);

        if (request is null || !request.IsHead)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Internal Server Error",
    };

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // 已停止
        }

        foreach (var client in _clients.Values)
        {
            try
            {
                client.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 忽略
            }
        }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 停止期间连接异常可忽略
        }

        _slots.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>轻量 HTTP 请求（只保留服务端需要的字段）。</summary>
internal sealed class LocalHttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string Host { get; init; }
    public bool IsHead => Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
    public bool IsGet => Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
}

/// <summary>轻量 HTTP 响应。</summary>
internal sealed class LocalHttpResponse
{
    public int StatusCode { get; set; } = 200;
    public string? ContentType { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetText(int status, string text, string contentType = "text/plain; charset=utf-8")
    {
        StatusCode = status;
        ContentType = contentType;
        Body = Encoding.UTF8.GetBytes(text);
    }
}
