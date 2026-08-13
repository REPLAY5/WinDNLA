namespace WinDNLA.Core.Services;

/// <summary>
/// Detects files that are still being written (downloads, copies) so the scanner can skip them.
/// </summary>
public static class FileAvailability
{
    private static readonly HashSet<string> IncompleteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".part", ".crdownload", ".download", ".!qb", ".!ut", ".tmp", ".temp", ".partial", ".filepart"
    };

    /// <summary>
    /// Returns false when the file looks incomplete or is exclusively locked for write by another process.
    /// </summary>
    public static bool IsReadyForIndexing(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var name = Path.GetFileName(path);
        if (name.StartsWith(".~", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal))
            return false;

        var ext = Path.GetExtension(path);
        if (IncompleteExtensions.Contains(ext))
            return false;

        // Double-extension patterns: video.mp4.part already caught; also "file.mp4.download"
        var withoutExt = Path.GetFileNameWithoutExtension(path);
        var innerExt = Path.GetExtension(withoutExt);
        if (!string.IsNullOrEmpty(innerExt) &&
            IncompleteExtensions.Contains(Path.GetExtension(path)))
            return false;

        try
        {
            // Exclusive open fails while another process holds a write handle (typical downloader).
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 1,
                FileOptions.SequentialScan);

            if (stream.Length <= 0)
                return false;

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Optional stability check: size must not change during <paramref name="settleMs"/>.
    /// Used when exclusive open succeeds but the writer used a share mode that allows readers.
    /// </summary>
    public static async Task<bool> IsSizeStableAsync(string path, int settleMs = 250, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return false;
        var len1 = new FileInfo(path).Length;
        await Task.Delay(settleMs, ct).ConfigureAwait(false);
        if (!File.Exists(path)) return false;
        var len2 = new FileInfo(path).Length;
        return len1 == len2 && len1 > 0;
    }
}
