using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDNLA.Core.Models;
using WinDNLA.Core.Services;
using WinDNLA.Dlna;

namespace WinDNLA.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxScanLogLines = 500;
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly DlnaServer _dlna;
    private readonly AutostartService _autostart;
    private readonly DispatcherTimerProxy _timer;
    private readonly object _ssdpReloadLock = new();
    private CancellationTokenSource? _ssdpReloadCts;
    private CancellationTokenSource? _identitySaveCts;
    private bool _scanSessionLogged;
    private bool _loadingInterfaces;
    private bool _saving;
    private bool _ready;

    public MainViewModel(
        SettingsService settings,
        LibraryService library,
        DlnaServer dlna,
        AutostartService autostart)
    {
        _settings = settings;
        _library = library;
        _dlna = dlna;
        _autostart = autostart;
        _timer = new DispatcherTimerProxy();

        var s = _settings.Current;
        FriendlyName = s.FriendlyName;
        HttpPort = s.HttpPort;
        TranscodingEnabled = s.TranscodingEnabled;
        RunAtStartup = s.RunAtStartup;
        foreach (var root in s.LibraryRoots)
            LibraryRoots.Add(root);

        RebuildRules(s);
        LoadNetworkInterfaces();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        _library.Scanner.ProgressChanged += (_, p) =>
        {
            _timer.Enqueue(() => ApplyScanProgress(p));
        };
        _library.Scanner.LibraryChanged += (_, _) => _timer.Enqueue(() =>
        {
            RefreshStats();
            UiRefreshRequested?.Invoke(this, EventArgs.Empty);
        });
        _dlna.Sessions.SessionsChanged += (_, _) => _timer.Enqueue(RefreshSessions);

        RefreshStats();
        ServerStatus = _dlna.StatusMessage ?? "Остановлен";
        _ready = true;
    }

    public event EventHandler? UiRefreshRequested;
    public event EventHandler? ScanLogAppended;

    public ObservableCollection<string> LibraryRoots { get; } = [];
    public ObservableCollection<TranscodeRuleItem> TranscodeRules { get; } = [];
    public ObservableCollection<ClientSessionInfo> Clients { get; } = [];
    public ObservableCollection<string> ScanLog { get; } = [];
    public ObservableCollection<NetworkInterfaceItem> NetworkInterfaces { get; } = [];

    [ObservableProperty] private string friendlyName = "WinDNLA";
    [ObservableProperty] private double httpPort = 8200;
    [ObservableProperty] private bool transcodingEnabled = true;
    [ObservableProperty] private bool runAtStartup = true;
    [ObservableProperty] private bool isServerRunning;
    [ObservableProperty] private string serverStatus = "Остановлен";
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string scanSummary = "";
    [ObservableProperty] private int folderCount;
    [ObservableProperty] private int videoCount;
    [ObservableProperty] private string selectedRoot = "";
    [ObservableProperty] private string interfacesSummary = "Все";

    public void InitializeUiMarshaling(Action<Action> enqueue) => _timer.SetDispatcher(enqueue);

    public async Task StartAsync(bool quiet)
    {
        _autostart.SyncFromSettings();
        _library.StartAutoRescan();
        if (_settings.Current.ServerAutoStart)
        {
            try
            {
                await _dlna.StartAsync().ConfigureAwait(true);
                IsServerRunning = true;
                ServerStatus = _dlna.StatusMessage ?? "Работает";
            }
            catch (Exception ex)
            {
                ServerStatus = ex.Message;
                IsServerRunning = false;
            }
        }
    }

    [RelayCommand]
    private void SaveServerSettings()
    {
        PersistSettings();
        ServerStatus = IsServerRunning
            ? (_dlna.StatusMessage ?? "Работает")
            : "Настройки сохранены";
    }

    [RelayCommand]
    private async Task ToggleServerAsync()
    {
        PersistSettings();
        try
        {
            if (IsServerRunning)
            {
                await _dlna.StopAsync();
                IsServerRunning = false;
                ServerStatus = "Остановлен";
            }
            else
            {
                await _dlna.StartAsync();
                IsServerRunning = true;
                ServerStatus = _dlna.StatusMessage ?? "Работает";
            }
        }
        catch (Exception ex)
        {
            ServerStatus = ex.Message;
            IsServerRunning = false;
        }
    }

    [RelayCommand]
    private void AddFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path);
        if (LibraryRoots.Any(r => r.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        LibraryRoots.Add(path);
        PersistSettings();
        QueueBackgroundRescan();
    }

    [RelayCommand]
    private void RemoveSelectedFolder()
    {
        if (string.IsNullOrEmpty(SelectedRoot)) return;
        LibraryRoots.Remove(SelectedRoot);
        SelectedRoot = "";
        PersistSettings();
        QueueBackgroundRescan();
    }

    [RelayCommand]
    private void Rescan()
    {
        PersistSettings();
        QueueBackgroundRescan();
    }

    /// <summary>Fire-and-forget rescan on a thread-pool thread so the UI never blocks.</summary>
    public void QueueBackgroundRescan()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _library.RescanNowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _timer.Enqueue(() => AppendScanLog($"Ошибка: {ex.Message}"));
            }
            finally
            {
                _timer.Enqueue(() =>
                {
                    RefreshStats();
                    UiRefreshRequested?.Invoke(this, EventArgs.Empty);
                });
            }
        });
    }

    [RelayCommand]
    private void ToggleAutostart()
    {
        RunAtStartup = !RunAtStartup;
        _autostart.SetEnabled(RunAtStartup);
    }

    public void SetAutostart(bool enabled)
    {
        RunAtStartup = enabled;
        _autostart.SetEnabled(enabled);
    }

    [RelayCommand]
    private void AddTranscodeRule()
    {
        var item = new TranscodeRuleItem
        {
            ExtensionsText = ".avi",
            MatchNonAllowedCodecs = false,
            AllowedCodecsText = "h264,hevc",
            Enabled = true
        };
        item.PropertyChanged += TranscodeRule_PropertyChanged;
        TranscodeRules.Add(item);
        PersistSettings();
    }

    [RelayCommand]
    private void RemoveTranscodeRule(TranscodeRuleItem? item)
    {
        if (item is null) return;
        item.PropertyChanged -= TranscodeRule_PropertyChanged;
        TranscodeRules.Remove(item);
        PersistSettings();
    }

    public async Task ShutdownAsync()
    {
        try { NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { /* ignore */ }
        try { _ssdpReloadCts?.Cancel(); } catch { /* ignore */ }
        try { _identitySaveCts?.Cancel(); } catch { /* ignore */ }
        try { _library.StopAutoRescan(); } catch { /* ignore */ }
        try { _library.Scanner.Cancel(); } catch { /* ignore */ }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_library.Scanner.IsScanning && DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);

        try { await _dlna.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await _library.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
    }

    partial void OnFriendlyNameChanged(string value)
    {
        if (_saving) return;
        ScheduleIdentitySave(reloadSsdp: true);
    }

    partial void OnHttpPortChanged(double value)
    {
        if (_saving) return;
        ScheduleIdentitySave(reloadSsdp: false);
    }

    partial void OnTranscodingEnabledChanged(bool value)
    {
        if (_saving) return;
        PersistSettings();
    }

    private void PersistSettings()
    {
        if (!_ready) return;
        _saving = true;
        try
        {
            _settings.Update(s =>
            {
                s.FriendlyName = FriendlyName.Trim().Length == 0 ? "WinDNLA" : FriendlyName.Trim();
                s.HttpPort = HttpPort is > 0 and < 65535 ? (int)HttpPort : 8200;
                s.TranscodingEnabled = TranscodingEnabled;
                s.LibraryRoots = LibraryRoots.ToList();
                s.TranscodeRules = TranscodeRules.Select(r => r.ToModel()).ToList();
                s.DisabledNetworkAddresses = NetworkInterfaces.Count == 0
                    ? s.DisabledNetworkAddresses
                    : NetworkInterfaces.Where(i => !i.IsEnabled).Select(i => i.Address).ToList();
            });
        }
        finally
        {
            _saving = false;
        }
    }

    private void ScheduleIdentitySave(bool reloadSsdp)
    {
        _identitySaveCts?.Cancel();
        _identitySaveCts = new CancellationTokenSource();
        var token = _identitySaveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, token).ConfigureAwait(false);
                PersistSettings();
                if (reloadSsdp && IsServerRunning)
                    _dlna.ApplyIdentity();
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        _timer.Enqueue(LoadNetworkInterfaces);

    private void LoadNetworkInterfaces()
    {
        _loadingInterfaces = true;
        try
        {
            var disabled = _settings.Current.DisabledNetworkAddresses
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in NetworkInterfaces)
                old.PropertyChanged -= NetworkInterface_PropertyChanged;
            NetworkInterfaces.Clear();

            foreach (var bind in SsdpService.GetSelectableIPv4())
            {
                var address = bind.Address.ToString();
                var item = new NetworkInterfaceItem
                {
                    Address = address,
                    NicName = bind.NicName,
                    IsVirtual = SsdpService.IsLikelyVirtualAdapter(bind.NicName, bind.Description),
                    IsEnabled = !disabled.Contains(address)
                };
                item.PropertyChanged += NetworkInterface_PropertyChanged;
                NetworkInterfaces.Add(item);
            }

            UpdateInterfacesSummary();
        }
        finally
        {
            _loadingInterfaces = false;
        }
    }

    private void NetworkInterface_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingInterfaces || e.PropertyName != nameof(NetworkInterfaceItem.IsEnabled))
            return;
        UpdateInterfacesSummary();
        PersistSettings();
        ScheduleSsdpReload();
        UiRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateInterfacesSummary()
    {
        var total = NetworkInterfaces.Count;
        var enabled = NetworkInterfaces.Count(i => i.IsEnabled);
        InterfacesSummary = total == 0
            ? "Нет интерфейсов"
            : enabled == total
                ? $"Все ({total})"
                : enabled == 0
                    ? "Не выбраны"
                    : $"{enabled} из {total}";
    }

    private void ScheduleSsdpReload()
    {
        if (!IsServerRunning) return;
        CancellationToken token;
        lock (_ssdpReloadLock)
        {
            _ssdpReloadCts?.Cancel();
            _ssdpReloadCts?.Dispose();
            _ssdpReloadCts = new CancellationTokenSource();
            token = _ssdpReloadCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                await _dlna.ReloadSsdpAsync().ConfigureAwait(false);
                _timer.Enqueue(() =>
                {
                    ServerStatus = _dlna.StatusMessage ?? ServerStatus;
                    UiRefreshRequested?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _timer.Enqueue(() => ServerStatus = ex.Message);
            }
        });
    }

    private void TranscodeRule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_saving) return;
        PersistSettings();
    }

    private void ApplyScanProgress(ScanProgress p)
    {
        var starting = p.IsRunning && !IsScanning;
        IsScanning = p.IsRunning;
        if (starting)
        {
            ScanLog.Clear();
            _scanSessionLogged = false;
        }

        if (p.IsRunning)
        {
            ScanSummary = p.Total > 0
                ? $"{p.Phase} — {p.Processed}/{p.Total}"
                : p.Phase;

            if (!string.IsNullOrEmpty(p.CurrentPath))
            {
                if (!p.Phase.StartsWith("Пропуск", StringComparison.Ordinal))
                {
                    AppendScanLog($"[{p.Processed}/{p.Total}] {p.CurrentPath}");
                    _scanSessionLogged = true;
                }
            }
            else if (IsNotableScanPhase(p.Phase))
            {
                AppendScanLog(p.Phase);
                _scanSessionLogged = true;
            }

            if (p.Processed % 10 == 0 || p.Processed == p.Total)
                RefreshStats();
        }
        else
        {
            ScanSummary = p.Phase;
            if (_scanSessionLogged || p.HadChanges)
                AppendScanLog(p.Phase);
            RefreshStats();
            _scanSessionLogged = false;
            UiRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsNotableScanPhase(string phase) =>
        phase.StartsWith("Удалено", StringComparison.Ordinal)
        || phase.StartsWith("Ошибка", StringComparison.Ordinal)
        || phase.StartsWith("Прервано", StringComparison.Ordinal);

    private void AppendScanLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"{stamp}  {line}";
        ScanLog.Add(entry);
        while (ScanLog.Count > MaxScanLogLines)
            ScanLog.RemoveAt(0);
        ScanLogAppended?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildRules(AppSettings s)
    {
        foreach (var old in TranscodeRules)
            old.PropertyChanged -= TranscodeRule_PropertyChanged;
        TranscodeRules.Clear();
        foreach (var r in s.TranscodeRules)
        {
            var item = TranscodeRuleItem.FromModel(r);
            item.PropertyChanged += TranscodeRule_PropertyChanged;
            TranscodeRules.Add(item);
        }
    }

    private void RefreshStats()
    {
        var stats = _library.GetStats();
        FolderCount = stats.FolderCount;
        VideoCount = stats.VideoCount;
    }

    private void RefreshSessions()
    {
        var list = _dlna.Sessions.GetSessions();
        var incomingIds = list.Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);

        for (var i = Clients.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(Clients[i].SessionId))
                Clients.RemoveAt(i);
        }

        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var existingIndex = -1;
            for (var j = 0; j < Clients.Count; j++)
            {
                if (Clients[j].SessionId == c.SessionId)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                Clients.Insert(Math.Min(i, Clients.Count), c);
                continue;
            }

            Clients[existingIndex].SpeedMbitPerSec = c.SpeedMbitPerSec;
            if (existingIndex != i)
                Clients.Move(existingIndex, i);
        }

        UiRefreshRequested?.Invoke(this, EventArgs.Empty);
    }
}

public partial class NetworkInterfaceItem : ObservableObject
{
    public string Address { get; init; } = "";
    public string NicName { get; init; } = "";
    public bool IsVirtual { get; init; }

    [ObservableProperty] private bool isEnabled = true;

    public string DisplayName => IsVirtual
        ? $"{NicName}  ·  {Address}  (вирт.)"
        : $"{NicName}  ·  {Address}";
}

public partial class TranscodeRuleItem : ObservableObject
{
    [ObservableProperty] private string extensionsText = "";
    [ObservableProperty] private bool matchNonAllowedCodecs;
    [ObservableProperty] private string allowedCodecsText = "h264,avc,hevc,h265";
    [ObservableProperty] private bool enabled = true;

    public static TranscodeRuleItem FromModel(TranscodeRule r) => new()
    {
        ExtensionsText = string.Join(",", r.Extensions),
        MatchNonAllowedCodecs = r.MatchNonAllowedCodecs,
        AllowedCodecsText = string.Join(",", r.AllowedCodecs),
        Enabled = r.Enabled
    };

    public TranscodeRule ToModel() => new()
    {
        Extensions = ExtensionsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e).ToList(),
        MatchNonAllowedCodecs = MatchNonAllowedCodecs,
        AllowedCodecs = AllowedCodecsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Enabled = Enabled
    };
}

/// <summary>Tiny helper so ViewModel stays UI-agnostic.</summary>
public sealed class DispatcherTimerProxy
{
    private Action<Action>? _enqueue;
    private readonly Queue<Action> _pending = new();

    public void SetDispatcher(Action<Action> enqueue)
    {
        _enqueue = enqueue;
        while (_pending.Count > 0)
            enqueue(_pending.Dequeue());
    }

    public void Enqueue(Action action)
    {
        if (_enqueue is null) { _pending.Enqueue(action); return; }
        _enqueue(action);
    }
}
