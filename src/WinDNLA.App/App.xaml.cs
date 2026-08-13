using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using WinDNLA.App.ViewModels;
using WinDNLA.Core;
using WinDNLA.Core.Logging;
using WinDNLA.Core.Services;
using WinDNLA.Dlna;

namespace WinDNLA.App;

public partial class App : Application
{
    public const string MutexName = "Global\\WinDLNA_SingleInstance_Mutex";
    public const string ShowEventName = "Global\\WinDLNA_ShowWindow_Event";

    private MainWindow? _window;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showWatcherCts;
    private ServiceProvider? _services;

    public static App Instance => (App)Current;
    public IServiceProvider Services => _services!;
    public MainWindow? MainWindow => _window;
    public bool StartQuiet { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            try
            {
                _services?.GetService<ILogger<App>>()?.LogError(e.Exception, "Unhandled UI exception");
                FileLoggerProvider.WriteEmergency($"Unhandled UI exception: {e.Exception}");
            }
            catch { /* ignore */ }
            e.Handled = true;
            if (_window is null)
            {
                ShowFatalError("Не удалось открыть окно WinDLNA.\n\nПодробности в логе:\n" + AppPaths.CurrentLogFile);
                Environment.Exit(1);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            FileLoggerProvider.WriteEmergency($"AppDomain exception: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLoggerProvider.WriteEmergency($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cli = Environment.GetCommandLineArgs();
        StartQuiet = cli.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch
            {
                // ignore
            }
            Environment.Exit(0);
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWatcherCts = new CancellationTokenSource();
        _ = WatchShowRequestsAsync(_showWatcherCts.Token);

        _services = BuildServices();
        var log = _services.GetRequiredService<ILogger<App>>();
        log.LogInformation("WinDLNA starting quiet={Quiet} log={LogFile}", StartQuiet, AppPaths.CurrentLogFile);
        var vm = _services.GetRequiredService<MainViewModel>();
        try
        {
            _window = new MainWindow(vm, StartQuiet);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create main window");
            FileLoggerProvider.WriteEmergency($"Failed to create main window: {ex}");
            ShowFatalError("Не удалось открыть окно WinDLNA.\n\nПодробности в логе:\n" + AppPaths.CurrentLogFile);
            Environment.Exit(1);
            return;
        }
        if (!StartQuiet)
            _window.Activate();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private static void ShowFatalError(string message)
    {
        try { MessageBoxW(0, message, "WinDLNA", 0x00000010); }
        catch { /* ignore */ }
    }

    public void DisposeServices()
    {
        try { _services?.GetService<ILogger<App>>()?.LogInformation("WinDLNA shutting down"); } catch { /* ignore */ }
        try { _services?.Dispose(); } catch { /* ignore */ }
        _services = null;
    }

    private static ServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new FileLoggerProvider(AppPaths.LogsDir));
        });
        sc.AddSingleton<SettingsService>();
        sc.AddSingleton<FfmpegLocator>();
        sc.AddSingleton<FfmpegService>();
        sc.AddSingleton<IFfmpegService>(sp => sp.GetRequiredService<FfmpegService>());
        sc.AddSingleton<LibraryRepository>();
        sc.AddSingleton<LibraryScanner>();
        sc.AddSingleton<LibraryService>();
        sc.AddSingleton<AutostartService>();
        sc.AddSingleton<SessionTracker>();
        sc.AddSingleton<SsdpService>();
        sc.AddSingleton<DlnaHttpServer>();
        sc.AddSingleton<DlnaServer>();
        sc.AddSingleton<MainViewModel>();
        return sc.BuildServiceProvider();
    }

    private async Task WatchShowRequestsAsync(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_showEvent is not null && _showEvent.WaitOne(500))
                    {
                        _window?.DispatcherQueue.TryEnqueue(() => _window.ShowFromTray());
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        }, ct).ConfigureAwait(false);
    }
}
