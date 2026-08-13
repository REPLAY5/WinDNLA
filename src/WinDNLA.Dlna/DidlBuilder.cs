using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using WinDNLA.Core.Models;
using WinDNLA.Core.Services;

namespace WinDNLA.Dlna;

public static class DidlBuilder
{
    private static readonly XNamespace Didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace Dlna = "urn:schemas-dlna-org:metadata-1-0/";

    public static string BuildRootChildren(
        IEnumerable<MediaFolderRecord> roots,
        Func<MediaFolderRecord, int> childCount,
        string baseUrl)
    {
        var didl = new XElement(Didl + "DIDL-Lite",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "upnp", Upnp),
            new XAttribute(XNamespace.Xmlns + "dlna", Dlna),
            new XAttribute("xmlns", Didl));

        foreach (var folder in roots)
        {
            didl.Add(BuildContainer(folder, "0", childCount(folder)));
        }

        return didl.ToString(SaveOptions.DisableFormatting);
    }

    public static string BuildFolderChildren(
        MediaFolderRecord folder,
        IEnumerable<MediaFolderRecord> childFolders,
        IEnumerable<VideoRecord> videos,
        Func<MediaFolderRecord, int> folderChildCount,
        string baseUrl)
    {
        var didl = new XElement(Didl + "DIDL-Lite",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "upnp", Upnp),
            new XAttribute(XNamespace.Xmlns + "dlna", Dlna),
            new XAttribute("xmlns", Didl));

        foreach (var child in childFolders)
            didl.Add(BuildContainer(child, folder.ObjectId, folderChildCount(child)));

        foreach (var video in videos)
            didl.Add(BuildItem(video, folder.ObjectId, baseUrl));

        return didl.ToString(SaveOptions.DisableFormatting);
    }

    public static string BuildMetadataContainer(MediaFolderRecord folder, string parentId, int childCount)
    {
        var didl = new XElement(Didl + "DIDL-Lite",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "upnp", Upnp),
            new XAttribute("xmlns", Didl),
            BuildContainer(folder, parentId, childCount));
        return didl.ToString(SaveOptions.DisableFormatting);
    }

    public static string BuildMetadataItem(VideoRecord video, string parentId, string baseUrl)
    {
        var didl = new XElement(Didl + "DIDL-Lite",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "upnp", Upnp),
            new XAttribute(XNamespace.Xmlns + "dlna", Dlna),
            new XAttribute("xmlns", Didl),
            BuildItem(video, parentId, baseUrl));
        return didl.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildContainer(MediaFolderRecord folder, string parentId, int childCount) =>
        new(Didl + "container",
            new XAttribute("id", folder.ObjectId),
            new XAttribute("parentID", parentId),
            new XAttribute("restricted", "1"),
            new XAttribute("childCount", childCount),
            new XElement(Dc + "title", folder.Name),
            new XElement(Upnp + "class", "object.container.storageFolder"));

    private static XElement BuildItem(VideoRecord video, string parentId, string baseUrl)
    {
        var protocol = TranscodeEvaluator.ProtocolInfo(video.Path, video.NeedsTranscode);
        var mediaUrl = $"{baseUrl.TrimEnd('/')}/media/{video.ObjectId}";
        var duration = FormatDuration(video.DurationSeconds);

        var res = new XElement(Didl + "res",
            new XAttribute("protocolInfo", protocol),
            mediaUrl);
        if (!video.NeedsTranscode && video.Size > 0)
            res.SetAttributeValue("size", video.Size.ToString(CultureInfo.InvariantCulture));
        if (video.DurationSeconds > 0)
            res.SetAttributeValue("duration", duration);
        if (video.Width > 0 && video.Height > 0)
            res.SetAttributeValue("resolution", $"{video.Width}x{video.Height}");

        var item = new XElement(Didl + "item",
            new XAttribute("id", video.ObjectId),
            new XAttribute("parentID", parentId),
            new XAttribute("restricted", "1"),
            new XElement(Dc + "title", video.Title));

        var date = FormatDate(video.MtimeUtcTicks);
        if (date is not null)
        {
            item.Add(new XElement(Dc + "date", date));
            item.Add(new XElement(Upnp + "recordedStartDateTime", date));
        }

        item.Add(new XElement(Upnp + "class", "object.item.videoItem"));
        item.Add(res);

        if (!string.IsNullOrEmpty(video.ThumbPath) && File.Exists(video.ThumbPath))
        {
            var smUrl = $"{baseUrl.TrimEnd('/')}/thumb/{video.ObjectId}";
            item.Add(ThumbRes(smUrl, "JPEG_SM", $"{ThumbnailCache.SmWidth}x{ThumbnailCache.SmHeight}"));
            item.Add(AlbumArt(smUrl, "JPEG_SM"));

            var tnPath = ThumbnailCache.CompanionTnPath(video.ThumbPath);
            if (!string.IsNullOrEmpty(tnPath) && File.Exists(tnPath))
            {
                var tnUrl = $"{smUrl}/tn";
                item.Add(ThumbRes(tnUrl, "JPEG_TN", $"{ThumbnailCache.TnMax}x{ThumbnailCache.TnMax}"));
                item.Add(AlbumArt(tnUrl, "JPEG_TN"));
            }

            item.Add(new XElement(Upnp + "icon", smUrl));
        }

        return item;
    }

    private static XElement ThumbRes(string url, string profile, string resolution) =>
        new(Didl + "res",
            new XAttribute("protocolInfo",
                $"http-get:*:image/jpeg:DLNA.ORG_PN={profile};DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=00D00000000000000000000000000000"),
            new XAttribute("resolution", resolution),
            url);

    private static XElement AlbumArt(string url, string profile) =>
        new(Upnp + "albumArtURI",
            new XAttribute(XNamespace.Xmlns + "dlna", Dlna),
            new XAttribute(Dlna + "profileID", profile),
            url);

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "0:00:00.000";
        var ts = TimeSpan.FromSeconds(seconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}");
    }

    /// <summary>
    /// UPnP ContentDirectory ISO 8601 (same as miniDLNA %FT%T).
    /// TVs treat a missing dc:date as Unix epoch 1970-01-01.
    /// </summary>
    internal static string? FormatDate(long mtimeUtcTicks)
    {
        if (mtimeUtcTicks <= DateTime.UnixEpoch.Ticks) return null;
        try
        {
            var dt = new DateTime(mtimeUtcTicks, DateTimeKind.Utc);
            return dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string SoapFault(string message) =>
        $"""
         <?xml version="1.0"?>
         <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
           <s:Body>
             <s:Fault>
               <faultcode>s:Client</faultcode>
               <faultstring>{WebUtility.HtmlEncode(message)}</faultstring>
             </s:Fault>
           </s:Body>
         </s:Envelope>
         """;

    public static string WrapSoap(string actionResponseXml, string serviceType) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
           <s:Body>
         {actionResponseXml}
           </s:Body>
         </s:Envelope>
         """;
}
