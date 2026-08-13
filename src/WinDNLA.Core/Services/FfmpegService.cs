using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

public sealed class FfmpegService : IFfmpegService
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<FfmpegService>? _logger;

    public FfmpegService(FfmpegLocator locator, ILogger<FfmpegService>? logger = null)
    {
        _locator = locator;
        _logger = logger;
    }

    public async Task<ProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        var ffprobe = _locator.FindFfprobe();
        if (ffprobe is null) return null;

        var args =
            $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"";
        var (exit, stdout, _) = await RunAsync(ffprobe, args, ct).ConfigureAwait(false);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var result = new ProbeResult();
            if (doc.RootElement.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var dur) &&
                    double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    result.DurationSeconds = d;
                if (format.TryGetProperty("format_name", out var fn))
                    result.Container = fn.GetString() ?? "";
            }

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ctProp) ? ctProp.GetString() : null;
                    var codecName = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";
                    if (codecType == "video" && string.IsNullOrEmpty(result.VideoCodec))
                    {
                        result.VideoCodec = codecName;
                        if (stream.TryGetProperty("width", out var w)) result.Width = w.GetInt32();
                        if (stream.TryGetProperty("height", out var h)) result.Height = h.GetInt32();
                    }
                    else if (codecType == "audio" && string.IsNullOrEmpty(result.AudioCodec))
                    {
                        result.AudioCodec = codecName;
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse ffprobe output for {Path}", filePath);
            return null;
        }
    }

    public async Task<string?> GenerateThumbnailAsync(string filePath, double durationSeconds, CancellationToken ct = default)
    {
        var ffmpeg = _locator.FindFfmpeg();
        if (ffmpeg is null) return null;

        AppPaths.EnsureCreated();
        var smPath = ThumbnailCache.SmFile(filePath);
        var tnPath = ThumbnailCache.TnFile(filePath);
        if (File.Exists(smPath) && File.Exists(tnPath)) return smPath;

        var seek = durationSeconds > 0 ? durationSeconds * 0.5 : 0.5;
        var seekStr = seek.ToString("0.###", CultureInfo.InvariantCulture);
        // JPEG_SM ≤640×480 for TV tiles; JPEG_TN ≤160 for LG WebOS (rejects oversized JPEG_TN).
        var args =
            $"-y -ss {seekStr} -i \"{filePath}\" -an -frames:v 1 " +
            "-filter_complex \"[0:v]scale=640:360:force_original_aspect_ratio=decrease:flags=lanczos,split[sm][tmp];" +
            "[tmp]scale=160:160:force_original_aspect_ratio=decrease:flags=lanczos[tn]\" " +
            $"-map \"[sm]\" -frames:v 1 -q:v 2 \"{smPath}\" " +
            $"-map \"[tn]\" -frames:v 1 -q:v 5 \"{tnPath}\"";
        var (exit, _, stderr) = await RunAsync(ffmpeg, args, ct).ConfigureAwait(false);
        if (exit != 0 || !File.Exists(smPath))
        {
            _logger?.LogWarning("Thumbnail failed for {Path}: {Err}", filePath, stderr);
            return null;
        }

        _logger?.LogDebug("Thumbnail {Path} -> {Thumb} ({Bytes} bytes)", filePath, smPath, new FileInfo(smPath).Length);
        return smPath;
    }

    public Process StartTranscode(string inputPath, double seekSeconds = 0)
    {
        var ffmpeg = _locator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg.exe не найден.");

        if (seekSeconds < 0) seekSeconds = 0;
        var seekStr = seekSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        // -ss before -i: fast input seek; client reconnects on each TimeSeekRange.
        var seekArg = seekSeconds > 0.001 ? $"-ss {seekStr} " : "";
        // Keep MPEG-TS PCR/PTS aligned with NPT so the TV clock matches the seek.
        var tsOffsetArg = seekSeconds > 0.001
            ? $"-output_ts_offset {seekStr} -avoid_negative_ts disabled "
            : "";

        var args =
            $"-hide_banner -loglevel warning -fflags +genpts {seekArg}-i \"{inputPath}\" " +
            "-map 0:v:0 -map 0:a:0? " +
            "-c:v libx264 -preset veryfast -tune zerolatency -profile:v high -level 4.1 " +
            $"-pix_fmt yuv420p -c:a aac -ac 2 -b:a 192k {tsOffsetArg}" +
            "-muxdelay 0 -muxpreload 0 -f mpegts -mpegts_flags +resend_headers pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");

        _logger?.LogInformation(
            "ffmpeg transcode pid={Pid} seek={Seek}s file={File} args={Args}",
            process.Id, seekSeconds, inputPath, args);

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger?.LogWarning("ffmpeg pid={Pid}: {Line}", process.Id, e.Data);
        };
        try { process.BeginErrorReadLine(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "ffmpeg stderr reader failed"); }

        return process;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
