using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QTools.Source;

public sealed class SourceHttpServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxTargetLength = 4096;
    private const int MaxHeaderCount = 100;
    private const int MaxJsonBodyBytes = 4096;
    private readonly TcpListener _listener;
    private readonly Func<SourceHttpRequest, CancellationToken, Task<SourceHttpResponse>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private Task? _acceptLoop;
    private int _serial;

    public SourceHttpServer(IPAddress address, int port, Func<SourceHttpRequest, CancellationToken, Task<SourceHttpResponse>> handler, int maxConnections = 32)
    {
        _listener = new TcpListener(address, port);
        _handler = handler;
        _slots = new SemaphoreSlim(maxConnections, maxConnections);
    }

    public int Port { get; private set; }

    public void Start()
    {
        if (_acceptLoop is not null) { return; }
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
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
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException exception)
            {
                Console.Error.WriteLine($"接受连接失败：{exception.Message}");
                continue;
            }

            var serial = Interlocked.Increment(ref _serial);
            _clients[serial] = client;
            _ = HandleClientAsync(client, serial);
        }
    }

    private async Task HandleClientAsync(TcpClient client, int serial)
    {
        var acquired = false;
        try
        {
            if (!await _slots.WaitAsync(0, _shutdown.Token).ConfigureAwait(false)) { return; }
            acquired = true;
            client.NoDelay = true;
            await using var stream = client.GetStream();
            SourceHttpRequest? request = null;
            SourceHttpResponse response;
            try
            {
                request = await ReadRequestAsync(stream, client, _shutdown.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                response = await _handler(request, timeout.Token).ConfigureAwait(false);
            }
            catch (HttpParseException exception)
            {
                response = SourceHttpResponse.Error(exception.StatusCode, exception.Code, exception.Message);
            }

            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            writeTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            await WriteResponseAsync(stream, request, response, writeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"连接处理失败：{exception.Message}");
        }
        finally
        {
            if (acquired) { _slots.Release(); }
            _clients.TryRemove(serial, out _);
            client.Dispose();
        }
    }

    private static async Task<SourceHttpRequest> ReadRequestAsync(
        NetworkStream stream, TcpClient client, CancellationToken cancellationToken)
    {
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        var header = new ArrayBufferWriter<byte>(1024);
        byte[] initialBody = [];
        int headerEnd;
        while (true)
        {
            var capacity = Math.Min(buffer.Length, MaxHeaderBytes + 1 - header.WrittenCount);
            if (capacity <= 0) { throw new HttpParseException(431, "HEADERS_TOO_LARGE", "请求头过大"); }
            var read = await stream.ReadAsync(buffer.AsMemory(0, capacity), headerTimeout.Token).ConfigureAwait(false);
            if (read == 0) { throw new HttpParseException(400, "BAD_REQUEST", "请求不完整"); }
            header.Write(buffer.AsSpan(0, read));
            headerEnd = header.WrittenSpan.IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0) { break; }
        }

        if (headerEnd > MaxHeaderBytes)
        {
            throw new HttpParseException(431, "HEADERS_TOO_LARGE", "请求头过大");
        }

        initialBody = header.WrittenSpan[(headerEnd + 4)..].ToArray();
        var text = Encoding.Latin1.GetString(header.WrittenSpan[..headerEnd]);
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[2] is not ("HTTP/1.0" or "HTTP/1.1"))
        {
            throw new HttpParseException(400, "BAD_REQUEST", "请求行不合法");
        }

        var method = requestLine[0];
        if (method is not ("GET" or "HEAD" or "POST"))
        {
            throw new HttpParseException(405, "METHOD_NOT_ALLOWED", "仅支持 GET、HEAD 和 POST");
        }

        var target = requestLine[1];
        if (target.Length is 0 or > MaxTargetLength || target[0] != '/' || target.Contains('#'))
        {
            throw new HttpParseException(400, "BAD_REQUEST", "请求地址不合法");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? host = null;
        long? contentLength = null;
        for (var i = 1; i < lines.Length; i++)
        {
            if (i > MaxHeaderCount)
            {
                throw new HttpParseException(431, "HEADERS_TOO_LARGE", "请求头数量过多");
            }
            var line = lines[i];
            if (line.Length == 0) { continue; }
            if (line[0] is ' ' or '\t')
            {
                throw new HttpParseException(400, "BAD_REQUEST", "不支持折行请求头");
            }
            var colon = line.IndexOf(':');
            if (colon <= 0 || !IsToken(line[..colon]) || !IsHeaderValue(line[(colon + 1)..]))
            {
                throw new HttpParseException(400, "BAD_REQUEST", "请求头格式不合法");
            }

            var name = line[..colon];
            var value = line[(colon + 1)..].Trim();
            if (!headers.TryAdd(name, value))
            {
                throw new HttpParseException(400, "BAD_REQUEST", "请求头重复");
            }
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(value)) { throw new HttpParseException(400, "BAD_REQUEST", "缺少 Host"); }
                host = value;
            }
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
                {
                    throw new HttpParseException(400, "BAD_REQUEST", "Content-Length 不合法");
                }
                contentLength = length;
            }
        }

        if (host is null || headers.ContainsKey("Transfer-Encoding"))
        {
            throw new HttpParseException(host is null ? 400 : 400, "BAD_REQUEST", "请求缺少 Host 或使用不支持的传输编码");
        }

        var queryStart = target.IndexOf('?');
        var rawPath = queryStart < 0 ? target : target[..queryStart];
        var rawQuery = queryStart < 0 ? "" : target[(queryStart + 1)..];
        var path = Decode(rawPath);
        if (!IsValidPath(path))
        {
            throw new HttpParseException(400, "BAD_REQUEST", "请求路径不合法");
        }
        var query = ParseQuery(rawQuery);
        byte[] body = [];
        if (method == "POST")
        {
            if (contentLength is null)
            {
                throw new HttpParseException(411, "LENGTH_REQUIRED", "POST 请求必须提供 Content-Length");
            }
            var limit = path == "/api/v1/submissions" ? SourceLimits.MaxSubmissionBodyBytes : MaxJsonBodyBytes;
            if (contentLength.Value > limit)
            {
                throw new HttpParseException(413, "BODY_TOO_LARGE", "请求体超过大小上限");
            }
            if (contentLength.Value > int.MaxValue)
            {
                throw new HttpParseException(413, "BODY_TOO_LARGE", "请求体超过大小上限");
            }
            if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), headerTimeout.Token).ConfigureAwait(false);
            }

            body = new byte[(int)contentLength.Value];
            var copied = Math.Min(initialBody.Length, body.Length);
            Array.Copy(initialBody, body, copied);
            if (copied < body.Length)
            {
                using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bodyTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await stream.ReadExactlyAsync(body.AsMemory(copied), bodyTimeout.Token).ConfigureAwait(false);
            }
        }
        else if (contentLength > 0)
        {
            throw new HttpParseException(400, "BAD_REQUEST", "GET 或 HEAD 请求不能携带请求体");
        }

        var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        return new SourceHttpRequest
        {
            Method = method,
            Path = path,
            Host = host,
            RemoteIp = remoteIp,
            Headers = headers,
            Query = query,
            Body = body,
        };
    }

    private static Dictionary<string, string?> ParseQuery(string raw)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) { return result; }
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = Decode((equals < 0 ? part : part[..equals]).Replace("+", " "));
            if (key.Length == 0) { continue; }
            result[key] = equals < 0 ? null : Decode(part[(equals + 1)..].Replace("+", " "));
        }
        return result;
    }

    private static string Decode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') { continue; }
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
            {
                throw new HttpParseException(400, "BAD_REQUEST", "URL 编码不合法");
            }
        }
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            throw new HttpParseException(400, "BAD_REQUEST", "URL 编码不合法");
        }
    }

    private static bool IsValidPath(string path)
    {
        if (path.Length == 0 || path[0] != '/') { return false; }
        foreach (var character in path)
        {
            if (character == '\\' || character < 0x20 || character == 0x7f) { return false; }
        }
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).All(segment => segment is not ("." or ".."));
    }

    private static bool IsToken(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && "!#$%&'*+-.^_`|~".IndexOf(character) < 0)
            {
                return false;
            }
        }
        return value.Length > 0;
    }

    private static bool IsHeaderValue(string value)
    {
        foreach (var character in value)
        {
            if ((character < 0x20 && character != '\t') || character == 0x7f) { return false; }
        }
        return true;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, SourceHttpRequest? request, SourceHttpResponse response, CancellationToken cancellationToken)
    {
        var head = new StringBuilder(512);
        head.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(ReasonPhrase(response.StatusCode)).Append("\r\n");
        head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        if (!string.IsNullOrEmpty(response.ContentType)) { head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n"); }
        AddDefault(head, "X-Content-Type-Options", "nosniff");
        AddDefault(head, "Referrer-Policy", "no-referrer");
        AddDefault(head, "Permissions-Policy", "camera=(), microphone=(), display-capture=()");
        AddDefault(head, "Cache-Control", "no-store");
        AddDefault(head, "Server", "QTools.Source/" + SourceLimits.ServiceVersion);
        foreach (var (name, value) in response.Headers)
        {
            if (name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n')) { continue; }
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        if (request?.IsHead != true)
        {
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        void AddDefault(StringBuilder target, string name, string value)
        {
            if (!response.Headers.ContainsKey(name) && !name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                target.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK", 201 => "Created", 202 => "Accepted", 204 => "No Content",
        302 => "Found", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 411 => "Length Required",
        413 => "Payload Too Large", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large",
        500 => "Internal Server Error", 503 => "Service Unavailable", _ => "Error",
    };

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested) { return; }
        _shutdown.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Values)
        {
            try { client.Dispose(); } catch (SocketException) { }
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _slots.Dispose();
        _shutdown.Dispose();
    }
}

public sealed class HttpParseException : Exception
{
    public HttpParseException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}
