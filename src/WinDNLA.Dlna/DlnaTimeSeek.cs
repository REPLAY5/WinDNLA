using System.Globalization;
using System.Text.RegularExpressions;

namespace WinDNLA.Dlna;

/// <summary>
/// DLNA TimeSeekRange.dlna.org / NPT helpers for on-the-fly transcode seeking.
/// </summary>
internal static partial class DlnaTimeSeek
{
    // npt=START-END  or npt=START-  (END optional); times as seconds or H:MM:SS[.fff]
    [GeneratedRegex(
        @"^\s*npt\s*=\s*(?<start>[^-]+)-(?<end>[^/\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestRegex();

    public static bool TryParseRequest(string? header, out double startSeconds, out double? endSeconds)
    {
        startSeconds = 0;
        endSeconds = null;
        if (string.IsNullOrWhiteSpace(header)) return false;

        var m = RequestRegex().Match(header);
        if (!m.Success) return false;
        if (!TryParseNpt(m.Groups["start"].Value.Trim(), out startSeconds)) return false;

        var endRaw = m.Groups["end"].Value.Trim();
        if (endRaw.Length > 0)
        {
            if (!TryParseNpt(endRaw, out var end)) return false;
            endSeconds = end;
        }

        return true;
    }

    public static bool TryParseNpt(string value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();

        if (value.Contains(':'))
        {
            // H:MM:SS[.fff] or HH:MM:SS[.fff]
            var parts = value.Split(':');
            if (parts.Length is < 2 or > 3) return false;
            try
            {
                if (parts.Length == 2)
                {
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min))
                        return false;
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                        return false;
                    seconds = min * 60 + sec;
                    return true;
                }

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
                    return false;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                    return false;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                    return false;
                seconds = hours * 3600 + minutes * 60 + secs;
                return true;
            }
            catch
            {
                return false;
            }
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>
    /// Seconds with 3 fractional digits. Samsung Q-series send NPT this way and
    /// parse the response more reliably than H:MM:SS.
    /// </summary>
    public static string FormatNpt(double seconds)
    {
        if (seconds < 0) seconds = 0;
        return seconds.ToString("0.000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Response value, e.g. npt=90.000-600.000/600.000
    /// </summary>
    public static string FormatResponse(double startSeconds, double? endSeconds, double durationSeconds)
    {
        var start = FormatNpt(startSeconds);
        var endValue = endSeconds ?? (durationSeconds > 0 ? durationSeconds : null);
        var end = endValue is { } e ? FormatNpt(e) : "";
        if (durationSeconds > 0)
            return $"npt={start}-{end}/{FormatNpt(durationSeconds)}";
        return $"npt={start}-{end}";
    }

    /// <summary>
    /// First field is DTCP flag: 0 = cleartext (not encrypted). 1 would mean DTCP-IP.
    /// </summary>
    public static string FormatAvailableRange(double durationSeconds)
    {
        if (durationSeconds <= 0)
            return "0 npt=0.000-";
        return $"0 npt=0.000-{FormatNpt(durationSeconds)}";
    }

    public static string FormatMediaInfoSec(double durationSeconds)
    {
        if (durationSeconds <= 0) return "SEC_Duration=0;";
        var ms = (long)Math.Round(durationSeconds * 1000.0, MidpointRounding.AwayFromZero);
        return $"SEC_Duration={ms};";
    }
}
