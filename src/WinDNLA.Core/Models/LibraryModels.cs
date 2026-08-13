namespace WinDNLA.Core.Models;

public sealed class MediaFolderRecord
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string RootPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string ObjectId { get; set; } = "";
}

public sealed class VideoRecord
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public long Size { get; set; }
    public long MtimeUtcTicks { get; set; }
    public double DurationSeconds { get; set; }
    public string Container { get; set; } = "";
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string? ThumbPath { get; set; }
    public string ObjectId { get; set; } = "";
}

public sealed class LibraryStats
{
    public int FolderCount { get; set; }
    public int VideoCount { get; set; }
    public long SystemUpdateId { get; set; }
}

public sealed class ClientSessionInfo : System.ComponentModel.INotifyPropertyChanged
{
    private double _speedMbitPerSec;

    public string SessionId { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public long SizeBytes { get; set; }
    public string VideoCodec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsTranscoding { get; set; }
    public string TranscodingLabel => IsTranscoding ? "да" : "нет";
    public DateTimeOffset StartedAt { get; set; }

    public string DurationLabel => HumanFormat.Duration(DurationSeconds);
    public string SizeLabel => HumanFormat.FileSize(SizeBytes);
    public string ResolutionLabel => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "";
    public string FileDetails
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(DurationLabel)) parts.Add(DurationLabel);
            if (!string.IsNullOrEmpty(SizeLabel)) parts.Add(SizeLabel);
            if (!string.IsNullOrWhiteSpace(VideoCodec)) parts.Add(VideoCodec);
            if (!string.IsNullOrEmpty(ResolutionLabel)) parts.Add(ResolutionLabel);
            return string.Join(" · ", parts);
        }
    }

    public double SpeedMbitPerSec
    {
        get => _speedMbitPerSec;
        set
        {
            if (Math.Abs(_speedMbitPerSec - value) < 0.005) return;
            _speedMbitPerSec = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SpeedMbitPerSec)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public static class HumanFormat
{
    public static string Duration(double seconds)
    {
        if (seconds <= 0) return "";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}");
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{ts.Minutes}:{ts.Seconds:D2}");
    }

    public static string FileSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024) return $"{bytes} Б";
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        double value = bytes / 1024.0;
        if (value < 1024) return string.Create(culture, $"{value:0.#} КБ");
        value /= 1024.0;
        if (value < 1024) return string.Create(culture, $"{value:0.#} МБ");
        value /= 1024.0;
        return string.Create(culture, $"{value:0.##} ГБ");
    }
}

public sealed class ProbeResult
{
    public double DurationSeconds { get; set; }
    public string Container { get; set; } = "";
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class ScanProgress
{
    public string Phase { get; set; } = "";
    public string? CurrentPath { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
    public bool IsRunning { get; set; }
    /// <summary>True when the finished scan added/removed/updated library entries.</summary>
    public bool HadChanges { get; set; }
}
