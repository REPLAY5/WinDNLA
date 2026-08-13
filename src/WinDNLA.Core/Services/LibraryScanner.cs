using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

public sealed class LibraryScanner
{
    public const int MaxParallelism = 10;

    private readonly LibraryRepository _repo;
    private readonly SettingsService _settings;
    private readonly IFfmpegService _ffmpeg;
    private readonly ILogger<LibraryScanner>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _folderLock = new();
    private CancellationTokenSource? _cts;

    public LibraryScanner(
        LibraryRepository repo,
        SettingsService settings,
        IFfmpegService ffmpeg,
        ILogger<LibraryScanner>? logger = null)
    {
        _repo = repo;
        _settings = settings;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public bool IsScanning { get; private set; }
    public event EventHandler<ScanProgress>? ProgressChanged;
    public event EventHandler? LibraryChanged;

    public async Task ScanAsync(CancellationToken externalCt = default)
    {
        if (!await _gate.WaitAsync(0, externalCt).ConfigureAwait(false))
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;
        IsScanning = true;
        var changed = 0;
        var cancelled = false;
        var hadChanges = false;
        try
        {
            Report("Сканирование…", null, 0, 0, true);
            var settings = _settings.Current;
            var roots = settings.LibraryRoots
                .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Drop DB rows for files that no longer exist — on every scan start.
            var pruned = _repo.PruneMissingVideos();
            if (pruned > 0)
            {
                Interlocked.Exchange(ref changed, 1);
                PublishPartial();
                Report($"Удалено из библиотеки (нет на диске): {pruned}", null, 0, 0, true);
            }

            var existing = _repo.GetAllVideosByPath();
            var keep = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var folderCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            var files = new List<string>();
            foreach (var root in roots)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                    {
                        if (!TranscodeEvaluator.IsVideoFile(file))
                            continue;
                        files.Add(Path.GetFullPath(file));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Cannot enumerate {Root}", root);
                }
            }

            files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Phase 1: only new / size-changed. Phase 2: already indexed (fast verify + missing thumbs).
            var work = new List<string>();
            var known = new List<string>();
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (IsKnownSameSize(f, existing))
                    known.Add(f);
                else
                    work.Add(f);
            }

            var total = files.Count;
            // Progress starts from already-indexed count: 934/1073, not 1/1073.
            var processed = known.Count;
            var skippedBusy = 0;
            var skippedUnchanged = 0;

            Report(
                work.Count == 0
                    ? "Проверка библиотеки…"
                    : $"Новые/изменённые: {work.Count}, уже в базе: {known.Count}",
                null, processed, total, true);

            await RunParallelAsync(
                work,
                async (file, token) =>
                {
                    await ProcessOneAsync(
                        file,
                        roots,
                        existing,
                        keep,
                        folderCache,
                        total,
                        () => Interlocked.Increment(ref processed),
                        () => Interlocked.Increment(ref skippedBusy),
                        () => Interlocked.Increment(ref skippedUnchanged),
                        () => Interlocked.Exchange(ref changed, 1),
                        fastPathOnly: false,
                        token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            // Known files already counted in `processed`; only verify/thumbs — don't bump the counter again.
            await RunParallelAsync(
                known,
                async (file, token) =>
                {
                    await ProcessOneAsync(
                        file,
                        roots,
                        existing,
                        keep,
                        folderCache,
                        total,
                        () => Volatile.Read(ref processed),
                        () => Interlocked.Increment(ref skippedBusy),
                        () => Interlocked.Increment(ref skippedUnchanged),
                        () => Interlocked.Exchange(ref changed, 1),
                        fastPathOnly: true,
                        token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            // Ensure UI can reach total after work+known (busy skips may leave a gap).
            if (!ct.IsCancellationRequested && processed < total && work.Count == 0)
                Interlocked.Exchange(ref processed, total);
            else if (!ct.IsCancellationRequested && processed < total && keep.Count >= total)
                Interlocked.Exchange(ref processed, total);

            if (skippedBusy > 0)
                _logger?.LogInformation("Skipped {Count} busy/incomplete files", skippedBusy);
            if (skippedUnchanged > 0)
                _logger?.LogDebug("Skipped {Count} unchanged indexed files", skippedUnchanged);

            if (!ct.IsCancellationRequested)
                Report("Готово", null, total, total, true);

            foreach (var root in roots)
                EnsureFolderChainLocked(root, root, folderCache);

            var keepSet = keep.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _repo.DeleteVideosNotIn(keepSet);
            _repo.DeleteOrphanFolders();
            if (changed != 0 || keepSet.Count != existing.Count)
            {
                hadChanges = true;
                _repo.BumpSystemUpdateId();
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (changed != 0)
            {
                hadChanges = true;
                _repo.BumpSystemUpdateId();
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            IsScanning = false;
            Report(
                cancelled
                    ? "Прервано — уже просканированное сохранено и доступно по DLNA"
                    : "Готово",
                null, 0, 0, false, hadChanges || cancelled);
            _gate.Release();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private static Task RunParallelAsync(
        List<string> files,
        Func<string, CancellationToken, ValueTask> body,
        CancellationToken ct) =>
        files.Count == 0
            ? Task.CompletedTask
            : Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = ct },
                async (file, token) => await body(file, token).ConfigureAwait(false));

    private async ValueTask ProcessOneAsync(
        string file,
        List<string> roots,
        Dictionary<string, VideoRecord> existing,
        ConcurrentDictionary<string, byte> keep,
        Dictionary<string, long> folderCache,
        int total,
        Func<int> nextProcessed,
        Action incBusy,
        Action incUnchanged,
        Action markChanged,
        bool fastPathOnly,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var info = new FileInfo(file);
        if (!info.Exists)
        {
            nextProcessed();
            return;
        }

        VideoRecord? old;
        lock (existing)
            existing.TryGetValue(file, out old);

        // Fast path: already in DB with same size — no settle delay, no probe.
        // Size-only (not mtime): network/torrent drives often bump mtime without content change.
        if (IsSameSizeRecord(old, info))
        {
            keep[file] = 0;
            incUnchanged();
            var done = nextProcessed();

            old!.MtimeUtcTicks = info.LastWriteTimeUtc.Ticks;

            if (ThumbNeedsGeneration(old.ThumbPath))
            {
                Report("Догенерация превью", file, done, total, true);
                try
                {
                    var generatedThumb = await _ffmpeg.GenerateThumbnailAsync(file, old.DurationSeconds, ct)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(generatedThumb))
                    {
                        old.ThumbPath = generatedThumb;
                        _repo.UpsertVideo(old);
                        markChanged();
                        PublishPartial();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Thumb failed {File}", file);
                }
            }
            else if (done == total || done % 100 == 0)
            {
                Report("Пропуск уже просканированных", null, done, total, true);
            }

            return;
        }

        if (fastPathOnly)
        {
            // Known list expected same-size; if size changed under us, fall through next full scan.
            nextProcessed();
            return;
        }

        var processed = nextProcessed();
        Report("Обработка файлов", file, processed, total, true);

        if (!FileAvailability.IsReadyForIndexing(file))
        {
            incBusy();
            if (old is not null)
                keep[file] = 0;
            return;
        }

        if (!await FileAvailability.IsSizeStableAsync(file, 200, ct).ConfigureAwait(false))
        {
            incBusy();
            if (old is not null)
                keep[file] = 0;
            return;
        }

        keep[file] = 0;
        info.Refresh();

        var root = roots.FirstOrDefault(r =>
            file.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetDirectoryName(file), r, StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (root is null) return;

        var dir = Path.GetDirectoryName(file)!;
        var folderId = EnsureFolderChainLocked(root, dir, folderCache);

        ProbeResult? probe = null;
        try { probe = await _ffmpeg.ProbeAsync(file, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Probe failed {File}", file); }

        string? thumb = old?.ThumbPath;
        if (ThumbNeedsGeneration(thumb))
        {
            try
            {
                thumb = await _ffmpeg.GenerateThumbnailAsync(file, probe?.DurationSeconds ?? old?.DurationSeconds ?? 0, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Thumb failed {File}", file);
            }
        }

        var video = new VideoRecord
        {
            FolderId = folderId,
            Path = file,
            Title = Path.GetFileNameWithoutExtension(file),
            Size = info.Length,
            MtimeUtcTicks = info.LastWriteTimeUtc.Ticks,
            DurationSeconds = probe?.DurationSeconds ?? old?.DurationSeconds ?? 0,
            Container = probe?.Container ?? old?.Container ?? Path.GetExtension(file).TrimStart('.'),
            VideoCodec = probe?.VideoCodec ?? old?.VideoCodec ?? "",
            AudioCodec = probe?.AudioCodec ?? old?.AudioCodec ?? "",
            Width = probe?.Width ?? old?.Width ?? 0,
            Height = probe?.Height ?? old?.Height ?? 0,
            ThumbPath = thumb
        };
        _repo.UpsertVideo(video);
        lock (existing)
            existing[file] = video;
        markChanged();
        PublishPartial();
    }

    private static bool IsKnownSameSize(string file, Dictionary<string, VideoRecord> existing)
    {
        if (!existing.TryGetValue(file, out var old))
            return false;
        try
        {
            var info = new FileInfo(file);
            return info.Exists && IsSameSizeRecord(old, info);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameSizeRecord(VideoRecord? old, FileInfo info) =>
        old is not null && old.Size == info.Length;

    /// <summary>Current cache is *_sm.jpg (JPEG_SM 640×360). Older hash.jpg / *_tn.jpg-only entries are rebuilt.</summary>
    private static bool ThumbNeedsGeneration(string? thumbPath) =>
        !ThumbnailCache.IsCurrentSm(thumbPath);

    private void PublishPartial()
    {
        _repo.BumpSystemUpdateId();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private long EnsureFolderChainLocked(string rootPath, string absoluteDir, Dictionary<string, long> cache)
    {
        lock (_folderLock)
            return EnsureFolderChain(rootPath, absoluteDir, cache);
    }

    private long EnsureFolderChain(string rootPath, string absoluteDir, Dictionary<string, long> cache)
    {
        rootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        absoluteDir = Path.GetFullPath(absoluteDir).TrimEnd(Path.DirectorySeparatorChar);

        if (cache.TryGetValue(absoluteDir, out var cached))
            return cached;

        if (string.Equals(absoluteDir, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            var id = _repo.UpsertFolder(rootPath, "", Path.GetFileName(rootPath), null);
            cache[absoluteDir] = id;
            return id;
        }

        if (!absoluteDir.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var id = _repo.UpsertFolder(rootPath, "", Path.GetFileName(rootPath), null);
            cache[absoluteDir] = id;
            return id;
        }

        var parentDir = Path.GetDirectoryName(absoluteDir)!;
        var parentId = EnsureFolderChain(rootPath, parentDir, cache);
        var relative = absoluteDir[rootPath.Length..].TrimStart(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(absoluteDir);
        var folderId = _repo.UpsertFolder(rootPath, relative, name, parentId);
        cache[absoluteDir] = folderId;
        return folderId;
    }

    private void Report(
        string phase, string? path, int processed, int total, bool running, bool hadChanges = false) =>
        ProgressChanged?.Invoke(this, new ScanProgress
        {
            Phase = phase,
            CurrentPath = path,
            Processed = processed,
            Total = total,
            IsRunning = running,
            HadChanges = hadChanges
        });
}
