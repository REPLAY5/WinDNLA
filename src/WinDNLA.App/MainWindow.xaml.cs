using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinDNLA.App.ViewModels;
using WinRT.Interop;

namespace WinDNLA.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _exitRequested;
    private readonly bool _startQuiet;
    private ScanLogWindow? _scanLogWindow;

    public MainWindow(MainViewModel viewModel, bool startQuiet)
    {
        _viewModel = viewModel;
        _startQuiet = startQuiet;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(820, 1000));
        ApplyTrayIcon();

        ViewModel = viewModel;
        ShowWindowCommand = new RelayCommand(ShowFromTray);
        ExitCommand = new RelayCommand(ExitApplication);

        viewModel.InitializeUiMarshaling(action => DispatcherQueue.TryEnqueue(() => action()));
        viewModel.UiRefreshRequested += (_, _) => DispatcherQueue.TryEnqueue(() => Bindings.Update());

        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;

        AutostartMenuItem.IsChecked = viewModel.RunAtStartup;

        _ = InitializeAsync();
    }

    public MainViewModel ViewModel { get; }

    public IRelayCommand ShowWindowCommand { get; }
    public IRelayCommand ExitCommand { get; }

    public string ServerToggleLabel => ViewModel.IsServerRunning ? "Остановить" : "Запустить";

    public string LibrarySummary =>
        $"Папок: {ViewModel.FolderCount}  ·  Видео: {ViewModel.VideoCount}";

    public string ClientsHeader =>
        ViewModel.Clients.Count == 0
            ? "Клиенты"
            : $"Клиенты ({ViewModel.Clients.Count})";

    public Brush StatusDotBrush => new SolidColorBrush(
        ViewModel.IsServerRunning ? Colors.LimeGreen : Colors.Gray);

    public Visibility VisibleWhen(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VisibleWhenNot(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    private void ApplyTrayIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(icoPath))
                return;
            TrayIcon.Icon = new System.Drawing.Icon(icoPath);
        }
        catch
        {
            // leave default empty icon rather than crash startup
        }
    }

    private async Task InitializeAsync()
    {
        await ViewModel.StartAsync(_startQuiet);
        Bindings.Update();

        if (_startQuiet)
            HideToTray();
        else
            Activate();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Bindings.Update();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_exitRequested)
            return;

        args.Handled = true;
        HideToTray();
    }

    public void ShowFromTray()
    {
        try
        {
            AppWindow.Show();
            Activate();
        }
        catch
        {
            // ignore
        }
    }

    private void HideToTray()
    {
        try { AppWindow.Hide(); }
        catch { /* ignore */ }
    }

    public void ExitApplication()
    {
        if (_exitRequested) return;
        _exitRequested = true;

        try { _scanLogWindow?.Close(); } catch { /* ignore */ }
        try { TrayIcon.Dispose(); } catch { /* ignore */ }

        Closed -= MainWindow_Closed;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await ViewModel.ShutdownAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // timeout or shutdown error — still exit
            }
            finally
            {
                try { App.Instance.DisposeServices(); } catch { /* ignore */ }
                Environment.Exit(0);
            }
        });

        DispatcherQueue.TryEnqueue(() =>
        {
            try { Close(); } catch { /* ignore */ }
            try { Application.Current.Exit(); } catch { /* ignore */ }
        });
    }

    private async void ToggleServer_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleServerCommand.ExecuteAsync(null);
        Bindings.Update();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.AddFolderCommand.Execute(folder.Path);
        Bindings.Update();
    }

    private void OpenScanLog_Click(object sender, RoutedEventArgs e)
    {
        if (_scanLogWindow is not null)
        {
            try
            {
                _scanLogWindow.Activate();
                return;
            }
            catch
            {
                _scanLogWindow = null;
            }
        }

        _scanLogWindow = new ScanLogWindow(ViewModel);
        _scanLogWindow.Closed += (_, _) => _scanLogWindow = null;
        _scanLogWindow.Activate();
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscodeRuleItem item })
            ViewModel.RemoveTranscodeRuleCommand.Execute(item);
    }

    private void AutostartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts)
        {
            ViewModel.SetAutostart(ts.IsOn);
            AutostartMenuItem.IsChecked = ts.IsOn;
        }
    }

    private void AutostartMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutostartMenuItem.IsChecked;
        ViewModel.SetAutostart(enabled);
        Bindings.Update();
    }

    private void RescanMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RescanCommand.Execute(null);
        Bindings.Update();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }
}
