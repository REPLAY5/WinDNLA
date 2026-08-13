using System.Text;
using WinDNLA.Core;
using WinDNLA.Core.Services;

namespace WinDNLA.Tests;

public class LibraryScannerTests
{
    [Fact]
    public async Task Scan_indexes_ready_videos_and_builds_folder_tree()
    {
        await using var host = TestHost.Create();
        host.CreateVideo("show/s01/ep1.mp4", "episode-one-content");
        host.CreateVideo("movie.mkv", "movie-content");
        host.Ffmpeg.ForcedVideoCodec = "h264";

        await host.ScanWithRootAsync();

        var stats = host.Repo.GetStats();
        Assert.Equal(2, stats.VideoCount);
        Assert.True(stats.FolderCount >= 2); // media root + show + s01 at least

        var roots = host.Repo.GetChildFolders(null);
        Assert.Single(roots);
        var children = host.Repo.GetChildFolders(roots[0].Id);
        Assert.Contains(children, c => c.Name.Equals("show", StringComparison.OrdinalIgnoreCase)
                                    || host.Repo.GetVideosInFolder(roots[0].Id).Count > 0);

        Assert.Equal(2, host.Ffmpeg.ProbedPaths.Count);
        Assert.Equal(2, host.Ffmpeg.ThumbPaths.Count);
    }

    [Fact]
    public async Task Scan_skips_files_locked_for_write()
    {
        await using var host = TestHost.Create();
        var ready = host.CreateVideo("ready.mp4", "ready-bytes");
        var busy = Path.Combine(host.MediaDir, "busy.mp4");

        await using (var writer = new FileStream(busy, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var data = Encoding.UTF8.GetBytes("still-downloading");
            await writer.WriteAsync(data);
            await writer.FlushAsync();

            await host.ScanWithRootAsync();

            var videos = host.Repo.GetAllVideosByPath();
            Assert.Single(videos);
            Assert.True(videos.ContainsKey(ready));
            Assert.False(videos.ContainsKey(busy));
        }

        // After unlock, rescan picks it up
        await host.ScanWithRootAsync();
        Assert.Equal(2, host.Repo.GetStats().VideoCount);
    }

    [Fact]
    public async Task Scan_stores_codec_used_for_live_transcode_decision()
    {
        await using var host = TestHost.Create();
        host.Ffmpeg.ForcedVideoCodec = "mpeg4";
        host.CreateVideo("old.avi", "avi-bytes");

        await host.ScanWithRootAsync();

        var video = host.Repo.GetAllVideosByPath().Values.Single();
        Assert.Equal("mpeg4", video.VideoCodec);
        Assert.True(TranscodeEvaluator.NeedsTranscode(host.Settings.Current, video));
    }

    [Fact]
    public async Task Scan_does_not_remove_existing_entry_while_file_is_busy()
    {
        await using var host = TestHost.Create();
        var path = host.CreateVideo("film.mp4", "v1");
        await host.ScanWithRootAsync();
        Assert.Equal(1, host.Repo.GetStats().VideoCount);

        await using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            await host.ScanWithRootAsync();
            // Still present — busy skip keeps existing row
            Assert.Equal(1, host.Repo.GetStats().VideoCount);
            Assert.True(host.Repo.GetAllVideosByPath().ContainsKey(path));
        }
    }

    [Fact]
    public async Task Cancelled_scan_keeps_already_indexed_videos()
    {
        await using var host = TestHost.Create();
        for (var i = 0; i < 40; i++)
            host.CreateVideo($"v{i:D2}.mp4", $"content-{i}");

        host.Ffmpeg.ForcedVideoCodec = "h264";
        host.Settings.Update(s =>
        {
            s.LibraryRoots = [host.MediaDir];
            s.TranscodingEnabled = true;
        });

        using var cts = new CancellationTokenSource();
        var gotSome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Scanner.LibraryChanged += (_, _) =>
        {
            if (host.Repo.GetStats().VideoCount > 0)
                gotSome.TrySetResult();
        };

        var scanTask = host.Scanner.ScanAsync(cts.Token);
        await gotSome.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var partial = host.Repo.GetStats().VideoCount;
        Assert.True(partial > 0, "partial scan must keep already indexed videos for DLNA");

        cts.Cancel();
        try { await scanTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { /* ok */ }
        catch (TimeoutException) { /* scanner still winding down — data must remain */ }

        Assert.True(host.Repo.GetStats().VideoCount >= partial);
        Assert.True(host.Repo.GetStats().VideoCount < 40, "scan should stop before finishing all files");
    }

    [Fact]
    public async Task Rescan_skips_unchanged_indexed_without_reprobe()
    {
        await using var host = TestHost.Create();
        host.CreateVideo("a.mp4", "aaa");
        host.CreateVideo("b.mp4", "bbb");
        host.Ffmpeg.ForcedVideoCodec = "h264";

        await host.ScanWithRootAsync();
        Assert.Equal(2, host.Ffmpeg.ProbedPaths.Count);
        host.Ffmpeg.ProbedPaths.Clear();
        host.Ffmpeg.ThumbPaths.Clear();

        await host.ScanWithRootAsync();
        Assert.Empty(host.Ffmpeg.ProbedPaths);
        Assert.Empty(host.Ffmpeg.ThumbPaths);
        Assert.Equal(2, host.Repo.GetStats().VideoCount);
    }

    [Fact]
    public async Task Rescan_regenerates_missing_thumb_without_reprobe()
    {
        await using var host = TestHost.Create();
        var path = host.CreateVideo("clip.mp4", "clip-bytes");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();

        var video = host.Repo.GetAllVideosByPath()[Path.GetFullPath(path)];
        Assert.False(string.IsNullOrEmpty(video.ThumbPath));
        File.Delete(video.ThumbPath!);

        host.Ffmpeg.ProbedPaths.Clear();
        host.Ffmpeg.ThumbPaths.Clear();
        await host.ScanWithRootAsync();

        Assert.Empty(host.Ffmpeg.ProbedPaths);
        Assert.Single(host.Ffmpeg.ThumbPaths);
        var again = host.Repo.GetAllVideosByPath()[Path.GetFullPath(path)];
        Assert.False(string.IsNullOrEmpty(again.ThumbPath));
        Assert.True(File.Exists(again.ThumbPath!));
    }

    [Fact]
    public async Task Rescan_upgrades_legacy_jpeg_tn_cache_to_sm()
    {
        await using var host = TestHost.Create();
        var path = host.CreateVideo("clip.mp4", "clip-bytes");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();

        var video = host.Repo.GetAllVideosByPath()[Path.GetFullPath(path)];
        var sm = video.ThumbPath!;
        var tn = ThumbnailCache.CompanionTnPath(sm)!;
        Assert.EndsWith(ThumbnailCache.SmSuffix, sm, StringComparison.OrdinalIgnoreCase);
        File.Delete(sm);
        video.ThumbPath = tn;
        host.Repo.UpsertVideo(video);

        host.Ffmpeg.ProbedPaths.Clear();
        host.Ffmpeg.ThumbPaths.Clear();
        await host.ScanWithRootAsync();

        Assert.Empty(host.Ffmpeg.ProbedPaths);
        Assert.Single(host.Ffmpeg.ThumbPaths);
        var again = host.Repo.GetAllVideosByPath()[Path.GetFullPath(path)];
        Assert.EndsWith(ThumbnailCache.SmSuffix, again.ThumbPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(again.ThumbPath!));
    }

    [Fact]
    public async Task Scan_removes_videos_deleted_from_disk()
    {
        await using var host = TestHost.Create();
        var keep = host.CreateVideo("keep.mp4", "keep-bytes");
        var gone = host.CreateVideo("gone.mp4", "gone-bytes");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();
        Assert.Equal(2, host.Repo.GetStats().VideoCount);

        File.Delete(gone);
        await host.ScanWithRootAsync();

        Assert.Equal(1, host.Repo.GetStats().VideoCount);
        Assert.True(host.Repo.GetAllVideosByPath().ContainsKey(Path.GetFullPath(keep)));
        Assert.False(host.Repo.GetAllVideosByPath().ContainsKey(Path.GetFullPath(gone)));
    }

    [Fact]
    public async Task Resume_after_cancel_probes_only_remaining_files()
    {
        await using var host = TestHost.Create();
        const int totalFiles = 40;
        for (var i = 0; i < totalFiles; i++)
            host.CreateVideo($"v{i:D2}.mp4", $"content-{i}");

        host.Ffmpeg.ForcedVideoCodec = "h264";
        host.Settings.Update(s =>
        {
            s.LibraryRoots = [host.MediaDir];
            s.TranscodingEnabled = true;
        });

        using var cts = new CancellationTokenSource();
        var gotSome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Scanner.LibraryChanged += (_, _) =>
        {
            if (host.Repo.GetStats().VideoCount >= 2)
                gotSome.TrySetResult();
        };

        var scanTask = host.Scanner.ScanAsync(cts.Token);
        await gotSome.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cts.Cancel();
        try { await scanTask.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (OperationCanceledException) { /* ok */ }
        catch (TimeoutException) { /* ok */ }

        var afterCancel = host.Repo.GetStats().VideoCount;
        Assert.True(afterCancel >= 2);
        Assert.True(afterCancel < totalFiles, "cancel should leave some files unscanned");

        host.Ffmpeg.ProbedPaths.Clear();
        await host.Scanner.ScanAsync();
        Assert.Equal(totalFiles, host.Repo.GetStats().VideoCount);
        Assert.True(
            host.Ffmpeg.ProbedPaths.Count <= totalFiles - afterCancel + LibraryScanner.MaxParallelism,
            "must not re-probe already indexed files");
        Assert.True(host.Ffmpeg.ProbedPaths.Count < totalFiles, "must not re-probe the whole library");
    }

    [Fact]
    public async Task Upsert_same_file_does_not_duplicate_rows()
    {
        await using var host = TestHost.Create();
        var path = host.CreateVideo("dup.mp4", "payload");
        host.Ffmpeg.ForcedVideoCodec = "h264";
        await host.ScanWithRootAsync();
        Assert.Equal(1, host.Repo.GetStats().VideoCount);

        var video = host.Repo.GetAllVideosByPath().Values.Single();
        video.Path = path.ToLowerInvariant();
        video.Title = "renamed-title";
        host.Repo.UpsertVideo(video);

        Assert.Equal(1, host.Repo.GetStats().VideoCount);
        Assert.Equal("renamed-title", host.Repo.GetAllVideosByPath().Values.Single().Title);
    }
}
