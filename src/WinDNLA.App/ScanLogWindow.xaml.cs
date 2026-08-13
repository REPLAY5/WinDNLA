using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinDNLA.App.ViewModels;

namespace WinDNLA.App;

public sealed partial class ScanLogWindow : Window
{
    public ScanLogWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(720, 520));

        viewModel.ScanLogAppended += OnScanLogAppended;
        viewModel.UiRefreshRequested += OnUiRefresh;
        Closed += (_, _) =>
        {
            viewModel.ScanLogAppended -= OnScanLogAppended;
            viewModel.UiRefreshRequested -= OnUiRefresh;
        };

        DispatcherQueue.TryEnqueue(ScrollToEnd);
    }

    public MainViewModel ViewModel { get; }

    private void OnScanLogAppended(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ScrollToEnd);

    private void OnUiRefresh(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => Bindings.Update());

    private void ScrollToEnd()
    {
        if (ScanLogList.Items.Count == 0) return;
        try { ScanLogList.ScrollIntoView(ScanLogList.Items[^1]); }
        catch { /* ignore */ }
    }
}
