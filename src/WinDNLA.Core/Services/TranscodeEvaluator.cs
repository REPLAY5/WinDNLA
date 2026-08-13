using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

public static class TranscodeEvaluator
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".wmv", ".mov", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm"
    };

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path));

    public static IReadOnlyCollection<string> VideoExtensionsList => VideoExtensions;

    public static bool NeedsTranscode(AppSettings settings, string path, string? videoCodec)
    {
        if (!settings.TranscodingEnabled) return false;

        var ext = Path.GetExtension(path);
        var codec = NormalizeCodec(videoCodec);

        foreach (var rule in settings.TranscodeRules.Where(r => r.Enabled))
        {
            var extMatch = rule.Extensions.Count == 0 ||
                           rule.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
            if (!extMatch) continue;

            if (rule.MatchNonAllowedCodecs)
            {
                var allowed = rule.AllowedCodecs.Select(NormalizeCodec).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(codec) || !allowed.Contains(codec))
                    return true;
            }
            else if (rule.Extensions.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return "";
        var c = codec.Trim().ToLowerInvariant();
        return c switch
        {
            "avc1" or "avc" => "h264",
            "hev1" or "hvc1" or "hevc" => "h265",
            _ => c
        };
    }

    public static string GuessMime(string path, bool transcoding)
    {
        if (transcoding) return "video/mpeg";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/avi",
            ".wmv" => "video/x-ms-wmv",
            ".mov" => "video/quicktime",
            ".ts" or ".m2ts" => "video/vnd.dlna.mpeg-tts",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".webm" => "video/webm",
            ".flv" => "video/x-flv",
            _ => "video/mpeg"
        };
    }

    /// <summary>
    /// TIME_BASED_SEEK (bit 30) + STREAMING + BACKGROUND + CONNECTION_STALL + DLNA_V15.
    /// </summary>
    public const string TranscodeDlnaFlags = "41700000000000000000000000000000";

    public const string DirectDlnaFlags = "01700000000000000000000000000000";

    public const string TranscodeContentFeatures =
        "DLNA.ORG_PN=MPEG_TS_HD_NA_ISO;DLNA.ORG_OP=10;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=" + TranscodeDlnaFlags;

    public static string ProtocolInfo(string path, bool needsTranscode)
    {
        // OP=10: time-based seek only (live MPEG-TS pipe; byte Range not applicable).
        if (needsTranscode)
            return $"http-get:*:video/mpeg:{TranscodeContentFeatures}";

        var mime = GuessMime(path, false);
        return $"http-get:*:{mime}:DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS={DirectDlnaFlags}";
    }
}
