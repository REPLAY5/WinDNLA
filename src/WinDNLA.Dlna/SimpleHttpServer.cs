using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinDNLA.Dlna;

/// <summary>
/// Minimal HTTP over TcpListener — avoids HttpListener/http.sys URL ACL ("Access is denied").
/// </summary>
internal sealed class SimpleHttpContext : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _headersSent;
    private int _closedFlag;

    private SimpleHttpContext(TcpClient client, NetworkStream stream, SimpleHttpRequest request)
    {
        _client = client;
        _stream = stream;
        Request = request;
        Response = new SimpleHttpResponse(this);
    }

    public SimpleHttpRequest Request { get; }
    public SimpleHttpResponse Response { get; }

    public static async Task<SimpleHttpContext?> AcceptAsync(TcpClient client, CancellationToken ct)
    {
        try { client.NoDelay = true; } catch { /* ignore */ }
        var stream = client.GetStream();
        stream.ReadTimeout = 30000;
        stream.WriteTimeout = 30000;

        var request = await SimpleHttpRequest.ReadAsync(stream, client, ct).ConfigureAwait(false);
        if (request is null)
        {
            client.Dispose();
            return null;
        }

        return new SimpleHttpContext(client, stream, request);
    }

    /// <summary>Long media transfers must not die on the 30s socket write timeout.</summary>
    public void ConfigureForStreaming()
    {
        try { _stream.WriteTimeout = Timeout.Infinite; } catch { /* ignore */ }
        try { _stream.ReadTimeout = Timeout.Infinite; } catch { /* ignore */ }
        try { _client.SendTimeout = 0; } catch { /* ignore */ }
        try { _client.ReceiveTimeout = 0; } catch { /* ignore */ }
    }

    internal async Task SendHeadersAsync()
    {
        if (_headersSent) return;
        _headersSent = true;

        var sb = new StringBuilder();
        var reason = ReasonPhrase(Response.StatusCode);
        sb.Append("HTTP/1.1 ").Append(Response.StatusCode).Append(' ').Append(reason).Append("\r\n");
        foreach (string key in Response.Headers)
        {
            sb.Append(key).Append(": ").Append(Response.Headers[key]).Append("\r\n");
        }

        if (Response.ContentLength64 >= 0 && Response.Headers["Content-Length"] is null)
            sb.Append("Content-Length: ").Append(Response.ContentLength64).Append("\r\n");
        if (!string.IsNullOrEmpty(Response.ContentType) && Response.Headers["Content-Type"] is null)
            sb.Append("Content-Type: ").Append(Response.ContentType).Append("\r\n");
        if (Response.SendChunked && Response.Headers["Transfer-Encoding"] is null)
            sb.Append("Transfer-Encoding: chunked\r\n");
        if (Response.Headers["Date"] is null)
            sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        if (Response.Headers["Server"] is null)
            sb.Append("Server: Windows/10 UPnP/1.0 DLNADOC/1.50 WinDNLA/1.0\r\n");

        sb.Append("Connection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await _stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
    }

    internal NetworkStream Stream => _stream;

    internal async Task WriteBodyAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        await SendHeadersAsync().ConfigureAwait(false);
        if (Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return;
        if (Response.SendChunked)
        {
            var size = Encoding.ASCII.GetBytes($"{buffer.Length:X}\r\n");
            await _stream.WriteAsync(size, ct).ConfigureAwait(false);
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        }
        else
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0) return;
        try
        {
            if (!_headersSent)
                await SendHeadersAsync().ConfigureAwait(false);
            else if (Response.SendChunked && !Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                await _stream.WriteAsync("0\r\n\r\n"u8.ToArray()).ConfigureAwait(false);

            await _stream.FlushAsync().ConfigureAwait(false);
        }
        catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private static string ReasonPhrase(int code) => code switch
    {
        200 => "OK",
        206 => "Partial Content",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "OK"
    };
}

internal sealed class SimpleHttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public string RawUrl { get; init; } = "/";
    public NameValueCollection Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Stream InputStream { get; init; } = Stream.Null;
    public string? ContentType => Headers["Content-Type"];
    public IPEndPoint? RemoteEndPoint { get; init; }
    public Encoding ContentEncoding { get; init; } = Encoding.UTF8;

    public string FormatHeaders()
    {
        var sb = new StringBuilder();
        foreach (string? key in Headers)
        {
            if (key is null) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(key).Append('=').Append(Headers[key]);
        }
        return sb.ToString();
    }

    public static async Task<SimpleHttpRequest?> ReadAsync(NetworkStream stream, TcpClient client, CancellationToken ct)
    {
        var headerBytes = await ReadHeadersAsync(stream, ct).ConfigureAwait(false);
        if (headerBytes.Length == 0) return null;

        var text = Encoding.UTF8.GetString(headerBytes);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        var method = parts[0];
        var rawUrl = parts[1];
        var path = rawUrl;
        var q = rawUrl.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) path = rawUrl[..q];
        try { path = Uri.UnescapeDataString(path); } catch { /* keep raw */ }

        var headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (headers["Expect"] is { Length: > 0 } expect &&
            expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        Stream body = Stream.Null;
        if (long.TryParse(headers["Content-Length"], out var len) && len > 0)
        {
            var buf = new byte[checked((int)Math.Min(len, 16 * 1024 * 1024))];
            var read = 0;
            while (read < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read), ct).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;
            }
            body = new MemoryStream(buf, 0, read, writable: false);
        }

        return new SimpleHttpRequest
        {
            Method = method,
            Path = path,
            RawUrl = rawUrl,
            InputStream = body,
            RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint,
            ContentEncoding = Encoding.UTF8,
            // headers copied below
        }.WithHeaders(headers);
    }

    private SimpleHttpRequest WithHeaders(NameValueCollection headers)
    {
        foreach (string? key in headers)
        {
            if (key is null) continue;
            Headers[key] = headers[key];
        }
        return this;
    }

    private static async Task<byte[]> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[1];
        var match = 0;
        // \r\n\r\n
        byte[] end = [13, 10, 13, 10];
        while (match < 4)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) break;
            ms.WriteByte(buf[0]);
            if (buf[0] == end[match]) match++;
            else match = buf[0] == end[0] ? 1 : 0;
            if (ms.Length > 64 * 1024) break;
        }
        return ms.ToArray();
    }
}

internal sealed class SimpleHttpResponse
{
    private readonly SimpleHttpContext _ctx;
    private SimpleBodyStream? _body;

    public SimpleHttpResponse(SimpleHttpContext ctx) => _ctx = ctx;

    public int StatusCode { get; set; } = 200;
    public string? ContentType { get; set; }
    public long ContentLength64 { get; set; } = -1;
    public bool SendChunked { get; set; }
    public NameValueCollection Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Stream OutputStream => _body ??= new SimpleBodyStream(_ctx);

    public void AddHeader(string name, string value) => Headers[name] = value;

    public void Close() => _ = _ctx.CloseAsync();
}

internal sealed class SimpleBodyStream : Stream
{
    private readonly SimpleHttpContext _ctx;

    public SimpleBodyStream(SimpleHttpContext ctx) => _ctx = ctx;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => _ctx.Stream.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        await _ctx.WriteBodyAsync(buffer, cancellationToken).ConfigureAwait(false);
}
