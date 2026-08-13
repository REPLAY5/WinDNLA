using Microsoft.Extensions.Logging;
using WinDNLA.Core.Services;

namespace WinDNLA.Dlna;

public sealed class DlnaServer : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly DlnaHttpServer _http;
    private readonly SsdpService _ssdp;
    private readonly ILogger<DlnaServer>? _logger;
    private int _activeSessions;

    public DlnaServer(
        SettingsService settings,
        DlnaHttpServer http,
        SsdpService ssdp,
        SessionTracker sessions,
        ILogger<DlnaServer>? logger = null)
    {
        _settings = settings;
        _http = http;
        _ssdp = ssdp;
        Sessions = sessions;
        _logger = logger;
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
        await _ssdp.StopAsync().ConfigureAwait(false);
        _http.Stop();
        IsRunning = false;
        StatusMessage = "Остановлен";
        _logger?.LogInformation("DLNA stopped");
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

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
