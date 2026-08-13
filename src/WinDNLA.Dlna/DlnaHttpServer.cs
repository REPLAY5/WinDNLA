using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinDNLA.Core.Services;

namespace WinDNLA.Dlna;

public sealed class DlnaHttpServer : IAsyncDisposable
{
    private readonly LibraryRepository _repo;
    private readonly SettingsService _settings;
    private readonly IFfmpegService _ffmpeg;
    private readonly SessionTracker _sessions;
    private readonly ILogger<DlnaHttpServer>? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _baseUrl = "";
    private string _uuid = Guid.NewGuid().ToString();
    private bool _running;
    private readonly ConcurrentDictionary<string, GenaSubscription> _gena = new(StringComparer.OrdinalIgnoreCase);

    public DlnaHttpServer(
        LibraryRepository repo,
        SettingsService settings,
        IFfmpegService ffmpeg,
        SessionTracker sessions,
        ILogger<DlnaHttpServer>? logger = null)
    {
        _repo = repo;
        _settings = settings;
        _ffmpeg = ffmpeg;
        _sessions = sessions;
        _logger = logger;
    }

    public string BaseUrl => _baseUrl;
    public string Uuid => _uuid;
    public bool IsRunning => _running;

    public Task StartAsync(int port, string? preferredIp = null)
    {
        Stop();
        // HTTP listens on all interfaces; BaseUrl is only for UI / relative DIDL links.
        // SSDP advertises a LOCATION per local IP so clients on each subnet get a reachable URL.
        var ip = preferredIp
                 ?? SsdpService.GetPreferredIPv4()?.ToString()
                 ?? SsdpService.GetLocalIPv4().FirstOrDefault()?.ToString()
                 ?? "127.0.0.1";
        _baseUrl = $"http://{ip}:{port}";
        _uuid = LoadOrCreateUuid();

        // TcpListener binds sockets directly — no http.sys URL ACL / "Access is denied".
        _listener = new TcpListener(IPAddress.Any, port);
        try
        {
            _listener.Start(512);
        }
        catch (SocketException ex)
        {
            _listener = null;
            throw new InvalidOperationException(
                $"Не удалось открыть порт {port}: {ex.Message}. Закройте другую программу на этом порту или смените порт.",
                ex);
        }

        _running = true;
        _cts = new CancellationTokenSource();
        _loop = AcceptLoopAsync(_cts.Token);
        _logger?.LogInformation("HTTP listening on 0.0.0.0:{Port} BaseUrl={BaseUrl} UUID={Uuid}", port, _baseUrl, _uuid);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try
            {
                if (!loop.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    // don't hang exit path
                }
            }
            catch { /* ignore */ }
        }
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Accept error");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        SimpleHttpContext? ctx = null;
        try
        {
            ctx = await SimpleHttpContext.AcceptAsync(client, ct).ConfigureAwait(false);
            if (ctx is null) return;
            await HandleAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Client handler error");
            try
            {
                if (ctx is not null)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.CloseAsync().ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
        }
        finally
        {
            if (ctx is not null)
                await ctx.DisposeAsync().ConfigureAwait(false);
            else
                try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task HandleAsync(SimpleHttpContext ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var remote = ctx.Request.RemoteEndPoint?.ToString() ?? "?";
        var method = ctx.Request.Method;
        var path = ctx.Request.Path;
        var httpLevel = IsMediaRangeRequest(ctx) ? LogLevel.Debug : LogLevel.Information;
        _logger?.Log(
            httpLevel,
            "HTTP {Method} {Path} from {Remote} {Headers}",
            method, path, remote, ctx.Request.FormatHeaders());
        try
        {
            if (method.Equals("SUBSCRIBE", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("UNSUBSCRIBE", StringComparison.OrdinalIgnoreCase))
            {
                await HandleGenaAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/description.xml", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/device.xml", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(ctx, BuildDeviceDescription(ResolveRequestBaseUrl(ctx)), "text/xml; charset=\"utf-8\"").ConfigureAwait(false);
                return;
            }

            if (path.Equals("/cd.xml", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(ctx, ContentDirectoryScpd, "text/xml; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (path.Equals("/cm.xml", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(ctx, ConnectionManagerScpd, "text/xml; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (path.Equals("/icon.png", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/icon.jpg", StringComparison.OrdinalIgnoreCase))
            {
                await ServeIconAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/thumb/", StringComparison.OrdinalIgnoreCase))
            {
                await ServeThumbAsync(ctx, path["/thumb/".Length..]).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
            {
                await ServeMediaAsync(ctx, path["/media/".Length..], ct).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/ContentDirectory/control", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/upnp/control/ContentDirectory", StringComparison.OrdinalIgnoreCase))
            {
                await HandleContentDirectoryAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/ConnectionManager/control", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/upnp/control/ConnectionManager", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConnectionManagerAsync(ctx).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 404;
            await ctx.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Request failed {Method} {Url} from {Remote}", method, ctx.Request.RawUrl, remote);
            try
            {
                ctx.Response.StatusCode = 500;
                await ctx.CloseAsync().ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
        finally
        {
            _logger?.Log(
                httpLevel,
                "HTTP {Method} {Path} -> {Status} {ContentType} len={Len} {ElapsedMs}ms from {Remote}",
                method, path, ctx.Response.StatusCode, ctx.Response.ContentType ?? "-",
                ctx.Response.ContentLength64, sw.ElapsedMilliseconds, remote);
        }
    }

    private async Task HandleContentDirectoryAsync(SimpleHttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        var soapAction = ctx.Request.Headers["SOAPACTION"] ?? ctx.Request.Headers["SoapAction"] ?? "";
        var baseUrl = ResolveRequestBaseUrl(ctx);
        _logger?.LogDebug(
            "SOAP {Action} base={BaseUrl} body={Body}",
            soapAction, baseUrl, Truncate(body, 1500));

        string responseXml;
        if (soapAction.Contains("Browse", StringComparison.OrdinalIgnoreCase) || body.Contains("Browse"))
            responseXml = HandleBrowse(body, baseUrl);
        else if (soapAction.Contains("GetSearchCapabilities", StringComparison.OrdinalIgnoreCase))
            responseXml = """
                <u:GetSearchCapabilitiesResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <SearchCaps>dc:title,upnp:class</SearchCaps>
                </u:GetSearchCapabilitiesResponse>
                """;
        else if (soapAction.Contains("#Search", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("<u:Search", StringComparison.OrdinalIgnoreCase))
            responseXml = HandleSearch(body, baseUrl);
        else if (soapAction.Contains("GetSortCapabilities", StringComparison.OrdinalIgnoreCase))
            responseXml = """
                <u:GetSortCapabilitiesResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <SortCaps></SortCaps>
                </u:GetSortCapabilitiesResponse>
                """;
        else if (soapAction.Contains("GetSystemUpdateID", StringComparison.OrdinalIgnoreCase))
            responseXml = $"""
                <u:GetSystemUpdateIDResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <Id>{_repo.GetSystemUpdateId()}</Id>
                </u:GetSystemUpdateIDResponse>
                """;
        else if (soapAction.Contains("X_SetBookmark", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("X_SetBookmark", StringComparison.OrdinalIgnoreCase))
        {
            var objectId = ExtractSoapValue(body, "ObjectID") ?? "";
            var pos = ExtractSoapValue(body, "PosSecond") ?? "";
            _logger?.LogInformation("X_SetBookmark ObjectID={Id} PosSecond={Pos}", objectId, pos);
            responseXml = """
                <u:X_SetBookmarkResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1" />
                """;
        }
        else if (soapAction.Contains("X_GetBookmark", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("X_GetBookmark", StringComparison.OrdinalIgnoreCase))
        {
            responseXml = """
                <u:X_GetBookmarkResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <PosSecond>0</PosSecond>
                </u:X_GetBookmarkResponse>
                """;
        }
        else
        {
            await WriteTextAsync(ctx, DidlBuilder.SoapFault("Unsupported action"), "text/xml; charset=utf-8", 500)
                .ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(ctx, DidlBuilder.WrapSoap(responseXml, "ContentDirectory"), "text/xml; charset=\"utf-8\"")
            .ConfigureAwait(false);
    }

    private string ResolveRequestBaseUrl(SimpleHttpContext ctx)
    {
        var host = ctx.Request.Headers["Host"];
        if (!string.IsNullOrWhiteSpace(host))
            return $"http://{host.Trim()}";
        return _baseUrl;
    }

    private string HandleBrowse(string body, string baseUrl)
    {
        var objectId = ExtractSoapValue(body, "ObjectID") ?? "0";
        var flag = ExtractSoapValue(body, "BrowseFlag") ?? "BrowseDirectChildren";
        var startingIndex = int.TryParse(ExtractSoapValue(body, "StartingIndex"), out var si) ? si : 0;
        var requestedCount = int.TryParse(ExtractSoapValue(body, "RequestedCount"), out var rc) ? rc : 0;

        string result;
        int numberReturned;
        int totalMatches;

        if (objectId == "0")
        {
            var roots = _repo.GetChildFolders(null);
            if (flag.Contains("Metadata", StringComparison.OrdinalIgnoreCase))
            {
                result = $"""
                    <DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">
                      <container id="0" parentID="-1" restricted="1" childCount="{roots.Count}">
                        <dc:title>Root</dc:title>
                        <upnp:class>object.container.storageFolder</upnp:class>
                      </container>
                    </DIDL-Lite>
                    """;
                numberReturned = 1;
                totalMatches = 1;
            }
            else
            {
                var page = Page(roots, startingIndex, requestedCount);
                result = DidlBuilder.BuildRootChildren(page, f => _repo.CountChildFolders(f.Id) + _repo.CountVideosInFolder(f.Id), baseUrl);
                numberReturned = page.Count;
                totalMatches = roots.Count;
            }
        }
        else
        {
            var folder = _repo.GetFolderByObjectId(objectId);
            if (folder is not null)
            {
                if (flag.Contains("Metadata", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = folder.ParentId is null ? "0" :
                        (_repo.GetChildFolders(null).Concat(_repo.GetChildFolders(folder.ParentId)).FirstOrDefault(f => f.Id == folder.ParentId)?.ObjectId ?? "0");
                    // Better parent lookup
                    parent = folder.ParentId is null ? "0" : FindParentObjectId(folder) ?? "0";
                    var count = _repo.CountChildFolders(folder.Id) + _repo.CountVideosInFolder(folder.Id);
                    result = DidlBuilder.BuildMetadataContainer(folder, parent, count);
                    numberReturned = 1;
                    totalMatches = 1;
                }
                else
                {
                    var childFolders = _repo.GetChildFolders(folder.Id);
                    var videos = _repo.GetVideosInFolder(folder.Id);
                    // Manual paging across folders then videos
                    var combinedCount = childFolders.Count + videos.Count;
                    var folderPage = childFolders.Skip(startingIndex).Take(requestedCount <= 0 ? int.MaxValue : requestedCount).ToList();
                    var taken = folderPage.Count;
                    var remain = requestedCount <= 0 ? int.MaxValue : Math.Max(0, requestedCount - taken);
                    var videoStart = Math.Max(0, startingIndex - childFolders.Count);
                    var videoPage = videos.Skip(videoStart).Take(remain).ToList();
                    if (startingIndex >= childFolders.Count)
                        folderPage = [];
                    result = DidlBuilder.BuildFolderChildren(folder, folderPage, videoPage,
                        f => _repo.CountChildFolders(f.Id) + _repo.CountVideosInFolder(f.Id), baseUrl,
                        _settings.Current);
                    numberReturned = folderPage.Count + videoPage.Count;
                    totalMatches = combinedCount;
                }
            }
            else
            {
                var video = _repo.GetVideoByObjectId(objectId);
                if (video is null)
                    return DidlBuilder.SoapFault("Invalid ObjectID");

                var parentFolder = FindFolderObjectId(video.FolderId) ?? "0";
                result = DidlBuilder.BuildMetadataItem(video, parentFolder, baseUrl, _settings.Current);
                numberReturned = 1;
                totalMatches = 1;
            }
        }

        var escaped = SecurityElementEscape(result);
        _logger?.LogInformation(
            "Browse {Flag} ObjectID={ObjectId} start={Start} count={Count} returned={Returned}/{Total}",
            flag, objectId, startingIndex, requestedCount, numberReturned, totalMatches);
        return $"""
            <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
              <Result>{escaped}</Result>
              <NumberReturned>{numberReturned}</NumberReturned>
              <TotalMatches>{totalMatches}</TotalMatches>
              <UpdateID>{_repo.GetSystemUpdateId()}</UpdateID>
            </u:BrowseResponse>
            """;
    }

    private string? FindParentObjectId(Core.Models.MediaFolderRecord folder)
    {
        if (folder.ParentId is null) return "0";
        return FindFolderObjectId(folder.ParentId.Value);
    }

    private string? FindFolderObjectId(long folderId)
    {
        // BFS from roots
        var queue = new Queue<long?>();
        queue.Enqueue(null);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var f in _repo.GetChildFolders(parent))
            {
                if (f.Id == folderId) return f.ObjectId;
                queue.Enqueue(f.Id);
            }
        }
        return null;
    }

    private string HandleSearch(string body, string baseUrl)
    {
        var containerId = ExtractSoapValue(body, "ContainerID") ?? "0";
        var criteria = ExtractSoapValue(body, "SearchCriteria") ?? "";
        _logger?.LogInformation("Search ContainerID={Id} criteria={Criteria}", containerId, Truncate(criteria, 300));
        var startingIndex = int.TryParse(ExtractSoapValue(body, "StartingIndex"), out var si) ? si : 0;
        var requestedCount = int.TryParse(ExtractSoapValue(body, "RequestedCount"), out var rc) ? rc : 0;
        var synthetic =
            "<root>" +
            $"<ObjectID>{System.Security.SecurityElement.Escape(containerId)}</ObjectID>" +
            "<BrowseFlag>BrowseDirectChildren</BrowseFlag>" +
            $"<StartingIndex>{startingIndex}</StartingIndex>" +
            $"<RequestedCount>{requestedCount}</RequestedCount>" +
            "</root>";
        return HandleBrowse(synthetic, baseUrl).Replace("BrowseResponse", "SearchResponse");
    }

    private const string SourceProtocolInfo =
        "http-get:*:video/mpeg:*,http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:video/avi:*,http-get:*:video/vnd.dlna.mpeg-tts:*";

    private async Task HandleGenaAsync(SimpleHttpContext ctx)
    {
        var method = ctx.Request.Method;
        var path = ctx.Request.Path;
        var remote = ctx.Request.RemoteEndPoint?.ToString() ?? "?";
        var isEventPath =
            path.Equals("/ContentDirectory/event", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/ConnectionManager/event", StringComparison.OrdinalIgnoreCase);

        if (!isEventPath)
        {
            _logger?.LogWarning("GENA {Method} unknown path {Path} from {Remote}", method, path, remote);
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentLength64 = 0;
            await ctx.CloseAsync().ConfigureAwait(false);
            return;
        }

        if (method.Equals("UNSUBSCRIBE", StringComparison.OrdinalIgnoreCase))
        {
            var sid = ctx.Request.Headers["SID"] ?? "";
            _gena.TryRemove(sid, out _);
            _logger?.LogInformation("GENA UNSUBSCRIBE SID={Sid} {Path} from {Remote}", sid, path, remote);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = 0;
            await ctx.CloseAsync().ConfigureAwait(false);
            return;
        }

        var existingSid = ctx.Request.Headers["SID"];
        var callback = ctx.Request.Headers["CALLBACK"] ?? "";
        var timeoutHdr = ctx.Request.Headers["TIMEOUT"] ?? "Second-1800";
        var seconds = 1800;
        var dash = timeoutHdr.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(timeoutHdr[(dash + 1)..], out var parsed) && parsed > 0)
            seconds = Math.Clamp(parsed, 30, 1800);

        var sidOut = existingSid;
        if (string.IsNullOrWhiteSpace(sidOut) || !_gena.ContainsKey(sidOut))
            sidOut = "uuid:" + Guid.NewGuid().ToString();

        var isNew = string.IsNullOrWhiteSpace(existingSid);
        _gena[sidOut] = new GenaSubscription(sidOut, callback, path, DateTimeOffset.UtcNow.AddSeconds(seconds));
        ctx.Response.StatusCode = 200;
        ctx.Response.AddHeader("SID", sidOut);
        ctx.Response.AddHeader("TIMEOUT", $"Second-{seconds}");
        ctx.Response.ContentLength64 = 0;
        await ctx.CloseAsync().ConfigureAwait(false);
        _logger?.LogInformation(
            "GENA SUBSCRIBE SID={Sid} timeout={Timeout} callback={Callback} {Path} from {Remote}",
            sidOut, seconds, callback, path, remote);

        if (isNew && !string.IsNullOrWhiteSpace(callback))
            _ = SendGenaEventAsync(sidOut, initial: true);
    }

    private async Task SendGenaEventAsync(string sid, bool initial)
    {
        if (!_gena.TryGetValue(sid, out var sub)) return;
        var seq = initial ? 0 : sub.Seq;
        var xml = BuildGenaPropertySet(sub.Path);
        foreach (var url in ParseCallbackUrls(sub.Callback))
            await SendGenaNotifyAsync(url, sid, seq, xml).ConfigureAwait(false);
        sub.Seq = seq + 1;
    }

    private string BuildGenaPropertySet(string path)
    {
        if (path.Contains("ConnectionManager", StringComparison.OrdinalIgnoreCase))
        {
            var source = SecurityElementEscape(SourceProtocolInfo);
            return
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">" +
                $"<e:property><SourceProtocolInfo>{source}</SourceProtocolInfo></e:property>" +
                "<e:property><SinkProtocolInfo></SinkProtocolInfo></e:property>" +
                "<e:property><CurrentConnectionIDs>0</CurrentConnectionIDs></e:property>" +
                "</e:propertyset>";
        }

        var updateId = _repo.GetSystemUpdateId();
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">" +
            $"<e:property><SystemUpdateID>{updateId}</SystemUpdateID></e:property>" +
            "<e:property><ContainerUpdateIDs></ContainerUpdateIDs></e:property>" +
            "</e:propertyset>";
    }

    private async Task SendGenaNotifyAsync(string url, string sid, int seq, string xml)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                return;

            var payload = Encoding.UTF8.GetBytes(xml);
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 80)
                .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            var host = uri.Port > 0 ? $"{uri.Host}:{uri.Port}" : uri.Host;
            var notify =
                $"NOTIFY {uri.PathAndQuery} HTTP/1.1\r\n" +
                $"HOST: {host}\r\n" +
                $"DATE: {DateTime.UtcNow:R}\r\n" +
                $"SERVER: {SsdpService.ServerToken}\r\n" +
                "CONTENT-TYPE: text/xml; charset=\"utf-8\"\r\n" +
                $"CONTENT-LENGTH: {payload.Length}\r\n" +
                "NT: upnp:event\r\n" +
                "NTS: upnp:propchange\r\n" +
                $"SID: {sid}\r\n" +
                $"SEQ: {seq}\r\n" +
                "CONNECTION: close\r\n\r\n";

            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(notify)).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            try { client.Client.Shutdown(SocketShutdown.Send); } catch { /* ignore */ }

            var buf = new byte[1024];
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var n = await stream.ReadAsync(buf, readCts.Token).ConfigureAwait(false);
            var ack = n > 0 ? Encoding.ASCII.GetString(buf, 0, n).Replace("\r\n", " | ") : "(empty)";
            _logger?.LogInformation("GENA NOTIFY seq={Seq} SID={Sid} {Url} ack={Ack}", seq, sid, url, Truncate(ack, 180));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GENA NOTIFY failed seq={Seq} SID={Sid} {Url}", seq, sid, url);
        }
    }

    private static IEnumerable<string> ParseCallbackUrls(string callback)
    {
        foreach (var part in callback.Split('>', StringSplitOptions.RemoveEmptyEntries))
        {
            var url = part.Trim().TrimStart('<').Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                yield return url;
        }
    }

    private async Task HandleConnectionManagerAsync(SimpleHttpContext ctx)
    {
        var soapAction = ctx.Request.Headers["SOAPACTION"] ?? "";
        string responseXml;
        if (soapAction.Contains("GetProtocolInfo", StringComparison.OrdinalIgnoreCase))
        {
            responseXml = $"""
                <u:GetProtocolInfoResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1">
                  <Source>{SourceProtocolInfo}</Source>
                  <Sink></Sink>
                </u:GetProtocolInfoResponse>
                """;
        }
        else if (soapAction.Contains("GetCurrentConnectionIDs", StringComparison.OrdinalIgnoreCase))
        {
            responseXml = """
                <u:GetCurrentConnectionIDsResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1">
                  <ConnectionIDs>0</ConnectionIDs>
                </u:GetCurrentConnectionIDsResponse>
                """;
        }
        else
        {
            responseXml = """
                <u:GetCurrentConnectionInfoResponse xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1">
                  <RcsID>0</RcsID><AVTransportID>0</AVTransportID><ProtocolInfo></ProtocolInfo>
                  <PeerConnectionManager></PeerConnectionManager><PeerConnectionID>-1</PeerConnectionID>
                  <Direction>Output</Direction><Status>OK</Status>
                </u:GetCurrentConnectionInfoResponse>
                """;
        }

        await WriteTextAsync(ctx, DidlBuilder.WrapSoap(responseXml, "ConnectionManager"), "text/xml; charset=\"utf-8\"")
            .ConfigureAwait(false);
    }

    private async Task ServeMediaAsync(SimpleHttpContext ctx, string objectId, CancellationToken ct)
    {
        var video = _repo.GetVideoByObjectId(objectId);
        if (video is null || !File.Exists(video.Path))
        {
            _logger?.LogWarning("Media 404 id={Id} path={Path}", objectId, video?.Path ?? "(null)");
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentLength64 = 0;
            await ctx.CloseAsync().ConfigureAwait(false);
            return;
        }

        var clientIp = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
        var needsTranscode = TranscodeEvaluator.NeedsTranscode(_settings.Current, video);
        ctx.ConfigureForStreaming();
        var range = ctx.Request.Headers["Range"];
        _logger?.Log(
            string.IsNullOrEmpty(range) ? LogLevel.Information : LogLevel.Debug,
            "Media {Id} file={File} transcode={Transcode} range={Range} timeSeek={TimeSeek} from {Ip}",
            objectId, video.Path, needsTranscode,
            range ?? "-",
            ctx.Request.Headers["TimeSeekRange.dlna.org"] ?? "-",
            clientIp);

        if (needsTranscode)
        {
            await ServeTranscodedAsync(ctx, video, clientIp, ct).ConfigureAwait(false);
            return;
        }

        await ServeFileWithRangeAsync(ctx, video, clientIp).ConfigureAwait(false);
    }

    private async Task ServeFileWithRangeAsync(SimpleHttpContext ctx, Core.Models.VideoRecord video, string clientIp)
    {
        var fileInfo = new FileInfo(video.Path);
        var mime = TranscodeEvaluator.GuessMime(video.Path, false);
        ctx.Response.ContentType = mime;
        ctx.Response.SendChunked = false;
        ctx.Response.AddHeader("Accept-Ranges", "bytes");
        ctx.Response.AddHeader("transferMode.dlna.org", "Streaming");
        ctx.Response.AddHeader("contentFeatures.dlna.org",
            "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000");

        long start = 0;
        long end = fileInfo.Length - 1;
        var range = ctx.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range["bytes=".Length..];
            var parts = spec.Split('-');
            if (long.TryParse(parts[0], out var s)) start = s;
            if (parts.Length > 1 && long.TryParse(parts[1], out var e) && e > 0) end = e;
            ctx.Response.StatusCode = 206;
            ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileInfo.Length}");
        }

        var length = end - start + 1;
        ctx.Response.ContentLength64 = length;

        if (ctx.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.CloseAsync().ConfigureAwait(false);
            return;
        }

        using var session = _sessions.Begin(clientIp, video, false);
        await using var fs = new FileStream(video.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        long remaining = length;
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await fs.ReadAsync(buffer.AsMemory(0, toRead)).ConfigureAwait(false);
                if (read <= 0) break;
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                session.AddBytes(read);
                remaining -= read;
            }
            _logger?.Log(
                string.IsNullOrEmpty(range) ? LogLevel.Information : LogLevel.Debug,
                "Media stream done {File} remaining={Remaining} from {Ip}",
                video.Path, remaining, clientIp);
        }
        catch
        {
            // client disconnected
        }
        finally
        {
            try { await ctx.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task ServeTranscodedAsync(
        SimpleHttpContext ctx, Core.Models.VideoRecord video, string clientIp, CancellationToken ct)
    {
        var duration = video.DurationSeconds;
        var seekSeconds = 0.0;
        double? seekEnd = null;
        var timeSeekHeader = ctx.Request.Headers["TimeSeekRange.dlna.org"];
        var parsedSeek = DlnaTimeSeek.TryParseRequest(timeSeekHeader, out var start, out var end);
        if (parsedSeek)
        {
            seekSeconds = start;
            seekEnd = end;
            if (duration > 0 && seekSeconds > duration)
                seekSeconds = Math.Max(0, duration - 0.5);
            if (seekSeconds < 0) seekSeconds = 0;
        }
        else if (!string.IsNullOrWhiteSpace(timeSeekHeader))
        {
            _logger?.LogWarning(
                "TimeSeekRange parse failed header={Header} file={File} from {Ip}",
                timeSeekHeader, video.Path, clientIp);
        }

        ctx.Response.ContentType = "video/mpeg";
        ctx.Response.SendChunked = true;
        ctx.Response.AddHeader("transferMode.dlna.org", "Streaming");
        ctx.Response.AddHeader("realTimeInfo.dlna.org", "DLNA.ORG_TLAG=*");
        // OP=10 + FLAGS TIME_BASED_SEEK; client reconnects with TimeSeekRange → new ffmpeg -ss.
        ctx.Response.AddHeader("contentFeatures.dlna.org", TranscodeEvaluator.TranscodeContentFeatures);
        ctx.Response.AddHeader("availableSeekRange.dlna.org", DlnaTimeSeek.FormatAvailableRange(duration));
        ctx.Response.AddHeader("TimeSeekRange.dlna.org",
            DlnaTimeSeek.FormatResponse(seekSeconds, seekEnd, duration));
        if (duration > 0)
            ctx.Response.AddHeader("MediaInfo.sec", DlnaTimeSeek.FormatMediaInfoSec(duration));

        if (parsedSeek || timeSeekHeader is not null)
            ctx.Response.StatusCode = 206;

        _logger?.LogInformation(
            "Transcode {File} seek={Seek}s duration={Duration}s parsed={Parsed} timeSeek={TimeSeek} getMediaInfo={MediaInfo} status={Status} from {Ip}",
            video.Path, seekSeconds, duration, parsedSeek,
            timeSeekHeader ?? "-",
            ctx.Request.Headers["getMediaInfo.sec"] ?? "-",
            ctx.Response.StatusCode, clientIp);

        if (ctx.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.CloseAsync().ConfigureAwait(false);
            return;
        }

        using var session = _sessions.Begin(clientIp, video, true);
        Process? process = null;
        try
        {
            process = _ffmpeg.StartTranscode(video.Path, seekSeconds);
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                session.AddBytes(read);
            }
        }
        catch
        {
            // disconnected / cancelled
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { /* ignore */ }
            process?.Dispose();
            try { await ctx.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task ServeThumbAsync(SimpleHttpContext ctx, string rest)
    {
        var wantTn = rest.EndsWith("/tn", StringComparison.OrdinalIgnoreCase);
        var objectId = wantTn ? rest[..^3] : rest;
        var video = _repo.GetVideoByObjectId(objectId);
        var smPath = video?.ThumbPath;
        var tnPath = ThumbnailCache.CompanionTnPath(smPath);
        var file = wantTn
            ? (tnPath is not null && File.Exists(tnPath) ? tnPath : null)
            : (smPath is not null && File.Exists(smPath) ? smPath : null);
        if (file is null)
        {
            _logger?.LogWarning(
                "Thumb 404 id={Id} tn={Tn} thumbPath={Path} exists={Exists}",
                objectId, wantTn, smPath ?? "(null)",
                smPath is not null && File.Exists(smPath));
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentLength64 = 0;
            await ctx.CloseAsync().ConfigureAwait(false);
            return;
        }

        var profile = wantTn || file.EndsWith(ThumbnailCache.TnSuffix, StringComparison.OrdinalIgnoreCase)
            ? "JPEG_TN"
            : "JPEG_SM";
        var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.AddHeader("Accept-Ranges", "bytes");
        ctx.Response.AddHeader("transferMode.dlna.org", "Interactive");
        ctx.Response.AddHeader("contentFeatures.dlna.org",
            $"DLNA.ORG_PN={profile};DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=00D00000000000000000000000000000");
        _logger?.LogInformation("Thumb {Id} {Profile} {Bytes} bytes file={File}", objectId, profile, bytes.Length, file);

        if (!ctx.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        await ctx.CloseAsync().ConfigureAwait(false);
    }

    internal static string? ResolveAppIconPath()
    {
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
        if (File.Exists(logo)) return logo;
        var store = Path.Combine(AppContext.BaseDirectory, "Assets", "StoreLogo.png");
        return File.Exists(store) ? store : null;
    }

    private async Task ServeIconAsync(SimpleHttpContext ctx)
    {
        var icon = ResolveAppIconPath();
        if (icon is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentLength64 = 0;
            await ctx.CloseAsync().ConfigureAwait(false);
            return;
        }

        ctx.Response.ContentType = "image/png";
        var bytes = await File.ReadAllBytesAsync(icon).ConfigureAwait(false);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.AddHeader("transferMode.dlna.org", "Interactive");
        if (!ctx.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        await ctx.CloseAsync().ConfigureAwait(false);
    }

    private string BuildDeviceDescription(string? requestBaseUrl = null)
    {
        var settings = _settings.Current;
        var name = WebUtility.HtmlEncode(settings.FriendlyName);
        var baseUrl = (requestBaseUrl ?? _baseUrl).TrimEnd('/');
        var hasIcon = ResolveAppIconPath() is not null;
        var iconXml = hasIcon
            ? """
              <iconList>
                <icon>
                  <mimetype>image/png</mimetype>
                  <width>48</width>
                  <height>48</height>
                  <depth>24</depth>
                  <url>/icon.png</url>
                </icon>
              </iconList>
              """
            : "";

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0" xmlns:dlna="urn:schemas-dlna-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
              <URLBase>{baseUrl}/</URLBase>
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
                <friendlyName>{name}</friendlyName>
                <manufacturer>WinDLNA</manufacturer>
                <manufacturerURL>https://windlna.ru</manufacturerURL>
                <modelDescription>Windows DLNA Media Server</modelDescription>
                <modelName>WinDLNA</modelName>
                <modelNumber>1.0</modelNumber>
                <serialNumber>1</serialNumber>
                <UDN>uuid:{_uuid}</UDN>
                <dlna:X_DLNADOC xmlns:dlna="urn:schemas-dlna-org:device-1-0">DMS-1.50</dlna:X_DLNADOC>
                {iconXml}
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>
                    <SCPDURL>/cd.xml</SCPDURL>
                    <controlURL>/ContentDirectory/control</controlURL>
                    <eventSubURL>/ContentDirectory/event</eventSubURL>
                  </service>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
                    <SCPDURL>/cm.xml</SCPDURL>
                    <controlURL>/ConnectionManager/control</controlURL>
                    <eventSubURL>/ConnectionManager/event</eventSubURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;
    }

    private static string LoadOrCreateUuid()
    {
        var path = Path.Combine(Core.AppPaths.Root, "device-uuid.txt");
        Core.AppPaths.EnsureCreated();
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        var uuid = Guid.NewGuid().ToString();
        File.WriteAllText(path, uuid);
        return uuid;
    }

    private static List<T> Page<T>(List<T> source, int start, int count)
    {
        if (start < 0) start = 0;
        if (start >= source.Count) return [];
        if (count <= 0) return source.Skip(start).ToList();
        return source.Skip(start).Take(count).ToList();
    }

    private static string? ExtractSoapValue(string body, string name)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
            return el?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMediaRangeRequest(SimpleHttpContext ctx) =>
        ctx.Request.Path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(ctx.Request.Headers["Range"]);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string SecurityElementEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static async Task WriteTextAsync(SimpleHttpContext ctx, string text, string contentType, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        await ctx.CloseAsync().ConfigureAwait(false);
    }

    private const string ContentDirectoryScpd =
        """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <actionList>
            <action><name>Browse</name>
              <argumentList>
                <argument><name>ObjectID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument>
                <argument><name>BrowseFlag</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable></argument>
                <argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument>
                <argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument>
                <argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
                <argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument>
                <argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument>
                <argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
                <argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
                <argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable></argument>
              </argumentList>
            </action>
            <action><name>GetSearchCapabilities</name><argumentList><argument><name>SearchCaps</name><direction>out</direction><relatedStateVariable>SearchCapabilities</relatedStateVariable></argument></argumentList></action>
            <action><name>GetSortCapabilities</name><argumentList><argument><name>SortCaps</name><direction>out</direction><relatedStateVariable>SortCapabilities</relatedStateVariable></argument></argumentList></action>
            <action><name>GetSystemUpdateID</name><argumentList><argument><name>Id</name><direction>out</direction><relatedStateVariable>SystemUpdateID</relatedStateVariable></argument></argumentList></action>
            <action><name>Search</name>
              <argumentList>
                <argument><name>ContainerID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument>
                <argument><name>SearchCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SearchCriteria</relatedStateVariable></argument>
                <argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument>
                <argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument>
                <argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
                <argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument>
                <argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument>
                <argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
                <argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>
                <argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_UpdateID</relatedStateVariable></argument>
              </argumentList>
            </action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_ObjectID</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Result</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_BrowseFlag</name><dataType>string</dataType><allowedValueList><allowedValue>BrowseMetadata</allowedValue><allowedValue>BrowseDirectChildren</allowedValue></allowedValueList></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Filter</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_SortCriteria</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Index</name><dataType>ui4</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Count</name><dataType>ui4</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_UpdateID</name><dataType>ui4</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>SearchCapabilities</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>SortCapabilities</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_SearchCriteria</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="yes"><name>SystemUpdateID</name><dataType>ui4</dataType></stateVariable>
            <stateVariable sendEvents="yes"><name>ContainerUpdateIDs</name><dataType>string</dataType></stateVariable>
          </serviceStateTable>
        </scpd>
        """;

    private const string ConnectionManagerScpd =
        """
        <?xml version="1.0"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <actionList>
            <action><name>GetProtocolInfo</name>
              <argumentList>
                <argument><name>Source</name><direction>out</direction><relatedStateVariable>SourceProtocolInfo</relatedStateVariable></argument>
                <argument><name>Sink</name><direction>out</direction><relatedStateVariable>SinkProtocolInfo</relatedStateVariable></argument>
              </argumentList>
            </action>
            <action><name>GetCurrentConnectionIDs</name>
              <argumentList>
                <argument><name>ConnectionIDs</name><direction>out</direction><relatedStateVariable>CurrentConnectionIDs</relatedStateVariable></argument>
              </argumentList>
            </action>
          </actionList>
          <serviceStateTable>
            <stateVariable sendEvents="yes"><name>SourceProtocolInfo</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="yes"><name>SinkProtocolInfo</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="yes"><name>CurrentConnectionIDs</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_ConnectionStatus</name><dataType>string</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_ConnectionID</name><dataType>i4</dataType></stateVariable>
            <stateVariable sendEvents="no"><name>A_ARG_TYPE_Direction</name><dataType>string</dataType></stateVariable>
          </serviceStateTable>
        </scpd>
        """;

    private sealed class GenaSubscription
    {
        public GenaSubscription(string sid, string callback, string path, DateTimeOffset expires)
        {
            Sid = sid;
            Callback = callback;
            Path = path;
            Expires = expires;
        }

        public string Sid { get; }
        public string Callback { get; }
        public string Path { get; }
        public DateTimeOffset Expires { get; }
        public int Seq { get; set; }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
