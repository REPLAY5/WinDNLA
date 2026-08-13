namespace WinDNLA.Core.Services;

/// <summary>
/// DLNA stills: JPEG_SM (TV tiles, ≤640×480) plus JPEG_TN (≤160×160, LG WebOS).
/// </summary>
public static class ThumbnailCache
{
    public const int SmWidth = 640;
    public const int SmHeight = 360;
    public const int TnMax = 160;
    public const string SmSuffix = "_sm.jpg";
    public const string TnSuffix = "_tn.jpg";

    public static string SmFile(string videoPath) => PathFor(videoPath, SmSuffix);

    public static string TnFile(string videoPath) => PathFor(videoPath, TnSuffix);

    public static string? CompanionTnPath(string? smOrThumbPath)
    {
        if (string.IsNullOrEmpty(smOrThumbPath)) return null;
        if (smOrThumbPath.EndsWith(SmSuffix, StringComparison.OrdinalIgnoreCase))
            return smOrThumbPath[..^SmSuffix.Length] + TnSuffix;
        if (smOrThumbPath.EndsWith(TnSuffix, StringComparison.OrdinalIgnoreCase))
            return smOrThumbPath;
        return null;
    }

    public static bool IsCurrentSm(string? thumbPath) =>
        !string.IsNullOrEmpty(thumbPath)
        && thumbPath.EndsWith(SmSuffix, StringComparison.OrdinalIgnoreCase)
        && File.Exists(thumbPath);

    private static string PathFor(string videoPath, string suffix)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(videoPath.ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(AppPaths.ThumbsDir, hash + suffix);
    }
}
