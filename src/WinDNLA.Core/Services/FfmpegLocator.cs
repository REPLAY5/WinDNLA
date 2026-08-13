using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

public sealed class FfmpegLocator
{
    private readonly SettingsService _settings;

    public FfmpegLocator(SettingsService settings) => _settings = settings;

    public string? FindFfmpeg() => FindTool("ffmpeg.exe");
    public string? FindFfprobe() => FindTool("ffprobe.exe");

    private string? FindTool(string fileName)
    {
        var settings = _settings.Current;
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPathOverride))
        {
            var overrideDir = settings.FfmpegPathOverride!;
            var candidate = Directory.Exists(overrideDir)
                ? Path.Combine(overrideDir, fileName)
                : (Path.GetFileName(overrideDir).Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    ? overrideDir
                    : Path.Combine(Path.GetDirectoryName(overrideDir) ?? "", fileName));
            if (File.Exists(candidate)) return candidate;
        }

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "ffmpeg", fileName),
            Path.Combine(baseDir, "tools", "ffmpeg", fileName),
            Path.Combine(baseDir, fileName)
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return null;
    }

    public bool IsAvailable => FindFfmpeg() is not null && FindFfprobe() is not null;
}
