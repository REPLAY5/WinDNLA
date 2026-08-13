using System.Diagnostics;
using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

public interface IFfmpegService
{
    Task<ProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default);
    Task<string?> GenerateThumbnailAsync(string filePath, double durationSeconds, CancellationToken ct = default);
    /// <param name="seekSeconds">Input seek (NPT). Restart ffmpeg with -ss for DLNA time-seek.</param>
    Process StartTranscode(string inputPath, double seekSeconds = 0);
}
