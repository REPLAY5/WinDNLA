using WinDNLA.Core.Models;

namespace WinDNLA.Core.Services;

/// <summary>
/// Coordinates settings, scanning and auto-rescan timer.
/// </summary>
public sealed class LibraryService : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly LibraryRepository _repository;
    private readonly LibraryScanner _scanner;
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;

    public LibraryService(SettingsService settings, LibraryRepository repository, LibraryScanner scanner)
    {
        _settings = settings;
        _repository = repository;
        _scanner = scanner;
    }

    public LibraryRepository Repository => _repository;
    public LibraryScanner Scanner => _scanner;
    public SettingsService Settings => _settings;

    public LibraryStats GetStats() => _repository.GetStats();

    public void StartAutoRescan()
    {
        StopAutoRescan();
        _timerCts = new CancellationTokenSource();
        var ct = _timerCts.Token;
        _timerTask = Task.Run(() => RunTimerAsync(ct), ct);
    }

    public void StopAutoRescan()
    {
        _timerCts?.Cancel();
        _timerCts?.Dispose();
        _timerCts = null;
        _timerTask = null;
    }

    private async Task RunTimerAsync(CancellationToken ct)
    {
        try
        {
            await _scanner.ScanAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var seconds = Math.Max(5, _settings.Current.AutoRescanSeconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                await _scanner.ScanAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    public Task RescanNowAsync(CancellationToken ct = default) => _scanner.ScanAsync(ct);

    public ValueTask DisposeAsync()
    {
        StopAutoRescan();
        _repository.Dispose();
        return ValueTask.CompletedTask;
    }
}
