using Microsoft.Extensions.Logging;
using WinDNLA.Core.Services;

namespace WinDNLA.Dlna;

public sealed class DlnaServer : IAsyncDisposable
{
    private static readonly TimeSpan LibraryNotifyDebounce = TimeSpan.FromSeconds(1);

    private readonly SettingsService _settings;
    private readonly DlnaHttpServer _http;
    private readonly SsdpService _ssdp;
    private readonly LibraryScanner _scanner;
    private readonly ILogger<DlnaServer>? _logger;
    private int _activeSessions;
    private CancellationTokenSource? _notifyCts;

    public DlnaServer(
        SettingsService settings,
        DlnaHttpServer http,
        SsdpService ssdp,
        SessionTracker sessions,
        LibraryScanner scanner,
        ILogger<DlnaServer>? logger = null)
    {
        _settings = settings;
        _http = http;
        _ssdp = ssdp;
        Sessions = sessions;
        _scanner = scanner;
        _logger = logger;
        _scanner.LibraryChanged += OnLibraryChanged;
        Sessions.SessionsChanged += OnSessionsChanged;
    }

    public SessionTracker Sessions { get; }
    public bool IsRunning { get; private set; }
    public string? BaseUrl => IsRunning ? _http.BaseUrl : null;
    public string? StatusMessage { get; private set; }

    public async Task StartAsync()
    {
        if (IsRunning) return;
        var settings = _settings.Current;
        var disabled = settings.DisabledNetworkAddresses;
        try
        {
            var preferred = SsdpService.GetPreferredIPv4(disabled)?.ToString();
            await _http.StartAsync(settings.HttpPort, preferred).ConfigureAwait(false);
            _ssdp.Configure(settings.HttpPort, _http.Uuid, settings.FriendlyName, disabled);
            await _ssdp.StartAsync().ConfigureAwait(false);
            IsRunning = true;
            StatusMessage = "Работает";
            var ips = string.Join(", ", SsdpService.GetLocalIPv4(disabled));
            _logger?.LogInformation("DLNA started at {Url}; SSDP on {IPs}", _http.BaseUrl, ips);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusMessage = $"Ошибка запуска: {ex.Message}";
            _logger?.LogError(ex, "DLNA start failed");
            _http.Stop();
            await _ssdp.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Rebind SSDP to the current interface selection without restarting HTTP.</summary>
    public async Task ReloadSsdpAsync()
    {
        if (!IsRunning) return;
        var settings = _settings.Current;
        var disabled = settings.DisabledNetworkAddresses;
        _ssdp.Configure(settings.HttpPort, _http.Uuid, settings.FriendlyName, disabled);
        await _ssdp.StartAsync().ConfigureAwait(false);
        StatusMessage = "Работает";
        _logger?.LogInformation("SSDP rebound on {IPs}", string.Join(", ", SsdpService.GetLocalIPv4(disabled)));
    }

    /// <summary>Push friendly name into SSDP without tearing down sockets.</summary>
    public void ApplyIdentity()
    {
        if (!IsRunning) return;
        var settings = _settings.Current;
        _ssdp.Configure(settings.HttpPort, _http.Uuid, settings.FriendlyName, settings.DisabledNetworkAddresses);
        _ = _ssdp.AnnounceAliveAsync();
    }

    public async Task StopAsync()
    {
        CancelPendingNotify();
        await _ssdp.StopAsync().ConfigureAwait(false);
        _http.Stop();
        IsRunning = false;
        StatusMessage = "Остановлен";
        _logger?.LogInformation("DLNA stopped");
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (!IsRunning) return;
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _notifyCts, cts);
        try { prev?.Cancel(); } catch { /* ignore */ }
        try { prev?.Dispose(); } catch { /* ignore */ }
        _ = DebounceLibraryNotifyAsync(cts.Token);
    }

    private async Task DebounceLibraryNotifyAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(LibraryNotifyDebounce, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!IsRunning) return;
        _logger?.LogInformation("Library changed — GENA notify + SSDP alive");
        try
        {
            await _http.NotifyContentChangedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GENA library notify failed");
        }

        if (IsRunning)
            _ = _ssdp.AnnounceAliveAsync();
    }

    private void CancelPendingNotify()
    {
        var prev = Interlocked.Exchange(ref _notifyCts, null);
        try { prev?.Cancel(); } catch { /* ignore */ }
        try { prev?.Dispose(); } catch { /* ignore */ }
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        var n = Sessions.GetSessions().Count;
        var prev = Interlocked.Exchange(ref _activeSessions, n);
        if (prev > 0 && n == 0)
        {
            _logger?.LogInformation("Last media session ended — re-announcing SSDP alive");
            _ = _ssdp.AnnounceAliveAsync();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _scanner.LibraryChanged -= OnLibraryChanged;
        await StopAsync().ConfigureAwait(false);
    }
}
