using System.Net;
using System.Text;
using WinDNLA.Core.Models;
using WinDNLA.Core.Services;
using WinDNLA.Dlna;

namespace WinDNLA.Tests;

public class SessionTrackerTests
{
    [Fact]
    public async Task Tracks_speed_and_enriches_file_info_from_db()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "h264";
        var path = host.CreateVideo("a.mp4", "payload");
        // Override probe defaults via direct DB update after scan for stable asserts.
        await host.ScanWithRootAsync();
        var video = host.Repo.GetAllVideosByPath()[Path.GetFullPath(path)];
        video.DurationSeconds = 3723;
        video.Size = 1_500_000_000;
        video.Width = 1920;
        video.Height = 1080;
        host.Repo.UpsertVideo(video);

        var changed = 0;
        host.Sessions.SessionsChanged += (_, _) => changed++;

        using (var session = host.Sessions.Begin("192.168.1.10", path, "a", isTranscoding: true))
        {
            session.AddBytes(1_000_000);
            Thread.Sleep(1100);
            session.AddBytes(500_000);

            var info = host.Sessions.GetSessions().Single();
            Assert.Equal("192.168.1.10", info.ClientIp);
            Assert.Equal("a", info.FileName);
            Assert.True(info.IsTranscoding);
            Assert.Equal("да", info.TranscodingLabel);
            Assert.True(info.SpeedMbitPerSec >= 0);
            Assert.Equal("1:02:03", info.DurationLabel);
            Assert.Equal("1.4 ГБ", info.SizeLabel);
            Assert.Equal("h264", info.VideoCodec);
            Assert.Equal("1920x1080", info.ResolutionLabel);
            Assert.Equal("1:02:03 · 1.4 ГБ · h264 · 1920x1080", info.FileDetails);
        }

        Assert.Empty(host.Sessions.GetSessions());
        Assert.True(changed >= 2);
    }

    [Fact]
    public async Task Idle_stream_decays_speed_to_zero()
    {
        await using var host = TestHost.Create();
        var path = host.CreateVideo("idle.mp4", "payload");
        await host.ScanWithRootAsync();

        var sawPositive = false;
        var zeroAfterPositive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Sessions.SessionsChanged += (_, _) =>
        {
            var session = host.Sessions.GetSessions().FirstOrDefault();
            if (session is null) return;
            if (session.SpeedMbitPerSec > 0)
                sawPositive = true;
            else if (sawPositive)
                zeroAfterPositive.TrySetResult();
        };

        using var session = host.Sessions.Begin("192.168.1.20", path, "idle", isTranscoding: false);
        session.AddBytes(2_000_000);
        await Task.Delay(1100);
        session.AddBytes(1_000_000);

        Assert.True(host.Sessions.GetSessions().Single().SpeedMbitPerSec > 0);
        Assert.True(sawPositive);

        // No further bytes — pause. Idle timer should reset speed to 0.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await zeroAfterPositive.Task.WaitAsync(cts.Token);
        Assert.Equal(0, host.Sessions.GetSessions().Single().SpeedMbitPerSec);
    }
}

public class HumanFormatTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(45, "0:45")]
    [InlineData(125, "2:05")]
    [InlineData(3723, "1:02:03")]
    public void Duration(double seconds, string expected) =>
        Assert.Equal(expected, HumanFormat.Duration(seconds));

    [Theory]
    [InlineData(0, "")]
    [InlineData(500, "500 Б")]
    [InlineData(2048, "2 КБ")]
    [InlineData(1_572_864, "1.5 МБ")]
    [InlineData(1_500_000_000, "1.4 ГБ")]
    public void FileSize(long bytes, string expected) =>
        Assert.Equal(expected, HumanFormat.FileSize(bytes));
}

public class DlnaHttpServerTests
{
    [Fact]
    public async Task Device_description_and_browse_root()
    {
        await using var host = TestHost.Create();
        host.CreateVideo("clip.mp4", "hello-dlna");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();

        host.Settings.Update(s => s.FriendlyName = "TestDNLA");
        var http = await host.StartHttpAsync();

        using var client = new HttpClient();
        var desc = await client.GetStringAsync($"{http.BaseUrl}/description.xml");
        Assert.Contains("TestDNLA", desc);
        Assert.Contains("MediaServer:1", desc);
        Assert.Contains("DMS-1.50", desc);
        Assert.Contains("URLBase", desc);

        var browseXml = await BrowseAsync(client, http.BaseUrl!, "0");
        Assert.Contains("NumberReturned", browseXml);
        Assert.Contains("DIDL-Lite", browseXml);
    }

    [Fact]
    public async Task Direct_stream_without_transcode()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "h264";
        var path = host.CreateVideo("direct.mp4", "DIRECT-PAYLOAD-0123456789");
        await host.ScanWithRootAsync();

        var video = host.Repo.GetAllVideosByPath()[path];
        Assert.False(TranscodeEvaluator.NeedsTranscode(host.Settings.Current, video));

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync($"{http.BaseUrl}/media/{video.ObjectId}");
        Assert.Equal("DIRECT-PAYLOAD-0123456789", Encoding.UTF8.GetString(bytes));

        await Task.Delay(100);
        Assert.Empty(host.Sessions.GetSessions());
        Assert.Empty(host.Ffmpeg.TranscodePaths);
    }

    [Fact]
    public async Task Transcoded_stream_uses_ffmpeg_pipe()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "mpeg4";
        var path = host.CreateVideo("old.avi", "AVI-TRANSCODE-PAYLOAD");
        await host.ScanWithRootAsync();

        var video = host.Repo.GetAllVideosByPath()[path];
        Assert.True(TranscodeEvaluator.NeedsTranscode(host.Settings.Current, video));

        var sawTranscodingSession = false;
        host.Sessions.SessionsChanged += (_, _) =>
        {
            var list = host.Sessions.GetSessions();
            if (list.Any(s => s.IsTranscoding))
                sawTranscodingSession = true;
        };

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync($"{http.BaseUrl}/media/{video.ObjectId}");
        Assert.Equal("AVI-TRANSCODE-PAYLOAD", Encoding.UTF8.GetString(bytes));
        Assert.Contains(path, host.Ffmpeg.TranscodePaths);
        Assert.Contains(0.0, host.Ffmpeg.TranscodeSeekSeconds);
        Assert.True(sawTranscodingSession);
    }

    [Fact]
    public async Task Rule_change_applies_without_rescan()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "xvid";
        var path = host.CreateVideo("clip.mkv", "RULE-LIVE-PAYLOAD");
        host.Settings.Update(s =>
        {
            s.TranscodingEnabled = true;
            s.TranscodeRules =
            [
                new TranscodeRule
                {
                    Extensions = [".avi"],
                    MatchNonAllowedCodecs = false,
                    Enabled = true
                }
            ];
        });
        await host.ScanWithRootAsync();

        var video = host.Repo.GetAllVideosByPath()[path];
        Assert.False(TranscodeEvaluator.NeedsTranscode(host.Settings.Current, video));

        host.Settings.Update(s =>
        {
            s.TranscodeRules =
            [
                new TranscodeRule
                {
                    Extensions = [".mkv"],
                    MatchNonAllowedCodecs = true,
                    AllowedCodecs = ["h264", "h265"],
                    Enabled = true
                }
            ];
        });
        Assert.True(TranscodeEvaluator.NeedsTranscode(host.Settings.Current, video));

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync($"{http.BaseUrl}/media/{video.ObjectId}");
        Assert.Equal("RULE-LIVE-PAYLOAD", Encoding.UTF8.GetString(bytes));
        Assert.Contains(path, host.Ffmpeg.TranscodePaths);
    }

    [Fact]
    public async Task Transcoded_TimeSeekRange_restarts_ffmpeg_with_seek()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "mpeg4";
        var path = host.CreateVideo("seek.avi", "SEEK-PAYLOAD");
        await host.ScanWithRootAsync();
        var video = host.Repo.GetAllVideosByPath()[path];
        Assert.True(TranscodeEvaluator.NeedsTranscode(host.Settings.Current, video));
        Assert.Equal(12.5, video.DurationSeconds);

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{http.BaseUrl}/media/{video.ObjectId}");
        req.Headers.TryAddWithoutValidation("TimeSeekRange.dlna.org", "npt=5.25-");
        using var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.True(resp.Headers.TryGetValues("TimeSeekRange.dlna.org", out var timeSeekVals));
        Assert.Contains("npt=5.250-", string.Join(",", timeSeekVals));
        Assert.True(resp.Headers.TryGetValues("availableSeekRange.dlna.org", out var avail));
        Assert.StartsWith("0 npt=", string.Join(",", avail));
        Assert.True(resp.Headers.TryGetValues("contentFeatures.dlna.org", out var features));
        Assert.Contains("OP=10", string.Join(",", features));
        Assert.Contains("FLAGS=41700000", string.Join(",", features));
        Assert.True(resp.Headers.TryGetValues("MediaInfo.sec", out var mediaInfo));
        Assert.Equal("SEC_Duration=12500;", string.Join(",", mediaInfo));
        Assert.True(resp.Headers.TryGetValues("realTimeInfo.dlna.org", out _));

        Assert.Equal(path, Assert.Single(host.Ffmpeg.TranscodePaths));
        Assert.Equal(5.25, Assert.Single(host.Ffmpeg.TranscodeSeekSeconds));
        Assert.Equal("SEEK-PAYLOAD", Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Range_request_returns_partial_content()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "h264";
        var path = host.CreateVideo("range.mp4", "ABCDEFGHIJ");
        await host.ScanWithRootAsync();
        var video = host.Repo.GetAllVideosByPath()[path];

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"{http.BaseUrl}/media/{video.ObjectId}");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        var body = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());
        Assert.Equal("CDEF", body);
    }

    [Fact]
    public async Task Head_media_returns_headers_without_body_or_session()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "h264";
        var payload = "HEAD-PAYLOAD-0123456789";
        var path = host.CreateVideo("head.mp4", payload);
        await host.ScanWithRootAsync();
        var video = host.Repo.GetAllVideosByPath()[path];

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Head, $"{http.BaseUrl}/media/{video.ObjectId}");
        using var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(payload.Length, resp.Content.Headers.ContentLength);
        Assert.Contains("bytes", resp.Headers.AcceptRanges);
        Assert.Empty(await resp.Content.ReadAsByteArrayAsync());
        Assert.Empty(host.Sessions.GetSessions());
        Assert.Empty(host.Ffmpeg.TranscodePaths);
    }

    [Fact]
    public async Task Head_media_range_returns_partial_headers_without_body()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "h264";
        var path = host.CreateVideo("head-range.mp4", "ABCDEFGHIJ");
        await host.ScanWithRootAsync();
        var video = host.Repo.GetAllVideosByPath()[path];

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Head, $"{http.BaseUrl}/media/{video.ObjectId}");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal(4, resp.Content.Headers.ContentLength);
        Assert.Equal("bytes 2-5/10", resp.Content.Headers.ContentRange?.ToString());
        Assert.Empty(await resp.Content.ReadAsByteArrayAsync());
        Assert.Empty(host.Sessions.GetSessions());
    }

    [Fact]
    public async Task Head_transcode_does_not_start_ffmpeg()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "mpeg4";
        var path = host.CreateVideo("head.avi", "AVI-HEAD");
        await host.ScanWithRootAsync();
        var video = host.Repo.GetAllVideosByPath()[path];
        Assert.True(TranscodeEvaluator.NeedsTranscode(host.Settings.Current, video));

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Head, $"{http.BaseUrl}/media/{video.ObjectId}");
        using var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("video/mpeg", resp.Content.Headers.ContentType?.MediaType);
        Assert.True(resp.Headers.TryGetValues("contentFeatures.dlna.org", out var features));
        Assert.Contains("OP=10", string.Join(",", features));
        Assert.True(resp.Headers.TryGetValues("availableSeekRange.dlna.org", out var avail));
        Assert.StartsWith("0 npt=", string.Join(",", avail));
        Assert.True(resp.Headers.TryGetValues("TimeSeekRange.dlna.org", out _));
        Assert.True(resp.Headers.TryGetValues("MediaInfo.sec", out var mediaInfo));
        Assert.Equal("SEC_Duration=12500;", string.Join(",", mediaInfo));
        Assert.Empty(host.Ffmpeg.TranscodePaths);
        Assert.Empty(host.Sessions.GetSessions());
    }

    [Fact]
    public async Task X_SetBookmark_returns_success_not_fault()
    {
        await using var host = TestHost.Create();
        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var envelope =
            """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:X_SetBookmark xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <ObjectID>Vabc</ObjectID>
                  <PosSecond>3098</PosSecond>
                </u:X_SetBookmark>
              </s:Body>
            </s:Envelope>
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{http.BaseUrl}/ContentDirectory/control");
        req.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation(
            "SOAPACTION",
            "\"urn:schemas-upnp-org:service:ContentDirectory:1#X_SetBookmark\"");
        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("X_SetBookmarkResponse", body);
        Assert.DoesNotContain("Fault", body);
    }

    [Fact]
    public async Task Browse_children_lists_video_item()
    {
        await using var host = TestHost.Create();
        host.CreateVideo("listed.mp4", "data");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();

        var root = host.Repo.GetChildFolders(null).Single();
        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var xml = await BrowseAsync(client, http.BaseUrl!, root.ObjectId);
        Assert.Contains("listed", xml);
        Assert.Contains("object.item.videoItem", xml);
        Assert.Contains("albumArtURI", xml);
        Assert.Contains("JPEG_SM", xml);
        Assert.Contains("JPEG_TN", xml);
        Assert.Contains("/thumb/", xml);
        Assert.Contains("/tn", xml);
        Assert.Contains("upnp:icon", xml);
        Assert.Contains("640x360", xml);
        var video = host.Repo.GetAllVideosByPath().Values.Single();
        var expectedDate = DidlBuilder.FormatDate(video.MtimeUtcTicks);
        Assert.NotNull(expectedDate);
        Assert.Contains($"dc:date&gt;{expectedDate}", xml);
        Assert.Contains($"recordedStartDateTime&gt;{expectedDate}", xml);
        Assert.DoesNotContain("1970-01-01", xml);
    }

    [Fact]
    public void FormatDate_uses_iso8601_and_skips_epoch()
    {
        var ticks = new DateTime(2024, 3, 15, 12, 34, 56, DateTimeKind.Utc).Ticks;
        Assert.Equal("2024-03-15T12:34:56", DidlBuilder.FormatDate(ticks));
        Assert.Null(DidlBuilder.FormatDate(0));
        Assert.Null(DidlBuilder.FormatDate(DateTime.UnixEpoch.Ticks));
    }

    [Fact]
    public async Task Thumb_has_dlna_jpeg_sm_and_tn_headers()
    {
        await using var host = TestHost.Create();
        host.CreateVideo("listed.mp4", "data");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();
        var video = host.Repo.GetAllVideosByPath().Values.Single();

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();

        using var sm = await client.GetAsync($"{http.BaseUrl}/thumb/{video.ObjectId}");
        sm.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", sm.Content.Headers.ContentType?.MediaType);
        Assert.True(sm.Headers.TryGetValues("contentFeatures.dlna.org", out var smFeatures));
        Assert.Contains("JPEG_SM", string.Join(",", smFeatures));
        Assert.True(sm.Headers.TryGetValues("transferMode.dlna.org", out var mode));
        Assert.Contains("Interactive", string.Join(",", mode));
        Assert.True((await sm.Content.ReadAsByteArrayAsync()).Length > 0);

        using var tn = await client.GetAsync($"{http.BaseUrl}/thumb/{video.ObjectId}/tn");
        tn.EnsureSuccessStatusCode();
        Assert.True(tn.Headers.TryGetValues("contentFeatures.dlna.org", out var tnFeatures));
        Assert.Contains("JPEG_TN", string.Join(",", tnFeatures));
        Assert.True((await tn.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [Fact]
    public async Task Search_returns_children_not_fault()
    {
        await using var host = TestHost.Create();
        host.CreateVideo("listed.mp4", "data");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();
        var root = host.Repo.GetChildFolders(null).Single();

        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var envelope =
            $"""
             <?xml version="1.0"?>
             <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
               <s:Body>
                 <u:Search xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                   <ContainerID>{root.ObjectId}</ContainerID>
                   <SearchCriteria>*</SearchCriteria>
                   <Filter>*</Filter>
                   <StartingIndex>0</StartingIndex>
                   <RequestedCount>0</RequestedCount>
                   <SortCriteria></SortCriteria>
                 </u:Search>
               </s:Body>
             </s:Envelope>
             """;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{http.BaseUrl}/ContentDirectory/control");
        req.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Search\"");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("SearchResponse", body);
        Assert.Contains("listed", body);
        Assert.DoesNotContain("Fault", body);
    }

    [Fact]
    public async Task Gena_subscribe_returns_sid()
    {
        await using var host = TestHost.Create();
        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), $"{http.BaseUrl}/ContentDirectory/event");
        req.Headers.TryAddWithoutValidation("CALLBACK", "<http://127.0.0.1:9/cb>");
        req.Headers.TryAddWithoutValidation("NT", "upnp:event");
        req.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        Assert.True(resp.Headers.TryGetValues("SID", out var sids));
        Assert.StartsWith("uuid:", sids.Single());
        Assert.True(resp.Headers.TryGetValues("TIMEOUT", out var timeouts));
        Assert.Contains("Second-", timeouts.Single());
    }

    [Fact]
    public async Task ConnectionManager_GetProtocolInfo()
    {
        await using var host = TestHost.Create();
        var http = await host.StartHttpAsync();
        using var client = new HttpClient();
        var envelope =
            """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:GetProtocolInfo xmlns:u="urn:schemas-upnp-org:service:ConnectionManager:1" />
              </s:Body>
            </s:Envelope>
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{http.BaseUrl}/ConnectionManager/control");
        req.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", "\"urn:schemas-upnp-org:service:ConnectionManager:1#GetProtocolInfo\"");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("video/mp4", body);
    }

    private static async Task<string> BrowseAsync(HttpClient client, string baseUrl, string objectId)
    {
        var envelope =
            $"""
             <?xml version="1.0"?>
             <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
               <s:Body>
                 <u:Browse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                   <ObjectID>{objectId}</ObjectID>
                   <BrowseFlag>BrowseDirectChildren</BrowseFlag>
                   <Filter>*</Filter>
                   <StartingIndex>0</StartingIndex>
                   <RequestedCount>0</RequestedCount>
                   <SortCriteria></SortCriteria>
                 </u:Browse>
               </s:Body>
             </s:Envelope>
             """;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/ContentDirectory/control");
        req.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
