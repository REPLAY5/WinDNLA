using System.Diagnostics;
using System.Text;
using WinDNLA.Core;
using WinDNLA.Core.Models;
using WinDNLA.Core.Services;
using WinDNLA.Dlna;

namespace WinDNLA.Tests;

internal sealed class FakeFfmpegService : IFfmpegService
{
    public string? ForcedVideoCodec { get; set; } = "h264";
    public string? ForcedAudioCodec { get; set; } = "aac";
    public List<string> ProbedPaths { get; } = [];
    public List<string> ThumbPaths { get; } = [];
    public List<string> TranscodePaths { get; } = [];
    public List<double> TranscodeSeekSeconds { get; } = [];
    private readonly object _listLock = new();

    public Task<ProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        lock (_listLock) ProbedPaths.Add(filePath);
        return Task.FromResult<ProbeResult?>(new ProbeResult
        {
            DurationSeconds = 12.5,
            Container = Path.GetExtension(filePath).TrimStart('.'),
            VideoCodec = ForcedVideoCodec ?? "h264",
            AudioCodec = ForcedAudioCodec ?? "aac",
            Width = 1280,
            Height = 720
        });
    }

    public Task<string?> GenerateThumbnailAsync(string filePath, double durationSeconds, CancellationToken ct = default)
    {
        AppPaths.EnsureCreated();
        var sm = ThumbnailCache.SmFile(filePath);
        var tn = ThumbnailCache.TnFile(filePath);
        lock (_listLock)
        {
            ThumbPaths.Add(filePath);
            File.WriteAllBytes(sm, [0xFF, 0xD8, 0xFF, 0xD9]); // minimal jpeg markers
            File.WriteAllBytes(tn, [0xFF, 0xD8, 0xFF, 0xD9]);
        }
        return Task.FromResult<string?>(sm);
    }

    public Process StartTranscode(string inputPath, double seekSeconds = 0)
    {
        lock (_listLock)
        {
            TranscodePaths.Add(inputPath);
            TranscodeSeekSeconds.Add(seekSeconds);
        }
        // Stream file bytes as a stand-in for ffmpeg MPEG-TS output.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c type \"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var p = new Process { StartInfo = psi };
        if (!p.Start())
            throw new InvalidOperationException("Failed to start fake transcoder");
        return p;
    }
}

internal sealed class TestHost : IAsyncDisposable
{
    public string Root { get; }
    public string MediaDir { get; }
    public SettingsService Settings { get; }
    public LibraryRepository Repo { get; }
    public FakeFfmpegService Ffmpeg { get; } = new();
    public LibraryScanner Scanner { get; }
    public SessionTracker Sessions { get; } = new();
    public DlnaHttpServer? Http { get; private set; }

    private TestHost(string root)
    {
        Root = root;
        MediaDir = Path.Combine(root, "media");
        Directory.CreateDirectory(MediaDir);
        AppPaths.SetRootOverride(Path.Combine(root, "appdata"));
        AppPaths.EnsureCreated();

        Settings = new SettingsService();
        Repo = new LibraryRepository();
        Scanner = new LibraryScanner(Repo, Settings, Ffmpeg);
    }

    public static TestHost Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinDNLATests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TestHost(root);
    }

    public string CreateVideo(string relativePath, string content, string? codecHint = null)
    {
        var full = Path.Combine(MediaDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.ASCII.GetBytes(content));
        return full;
    }

    public async Task ScanWithRootAsync()
    {
        Settings.Update(s =>
        {
            s.LibraryRoots = [MediaDir];
            s.TranscodingEnabled = true;
        });
        await Scanner.ScanAsync();
    }

    public async Task<DlnaHttpServer> StartHttpAsync(int? port = null)
    {
        Http = new DlnaHttpServer(Repo, Settings, Ffmpeg, Sessions);
        var p = port ?? 18000 + Random.Shared.Next(1000, 2000);
        await Http.StartAsync(p, "127.0.0.1");
        return Http;
    }

    public async ValueTask DisposeAsync()
    {
        if (Http is not null)
            await Http.DisposeAsync();
        Repo.Dispose();
        AppPaths.SetRootOverride(null);
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
