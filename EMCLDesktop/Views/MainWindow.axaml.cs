using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EMCLDesktop.ViewModels;
using EMCLDesktop.Services;

namespace EMCLDesktop.Views;

public partial class MainWindow : Window
{
    private bool closingAfterCleanup;
    private bool cleanupStarted;
    private bool updateNoticeDismissed;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"EMCL Desktop {ApplicationVersion.Current} — счётчик DLMS/COSEM";
        DesktopUpdates.Current.StatusChanged += OnUpdateStatusChanged;
        Closed += (_, _) => DesktopUpdates.Current.StatusChanged -= OnUpdateStatusChanged;
    }

    private void OnUpdateStatusChanged(object? sender, System.EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!cleanupStarted && !updateNoticeDismissed && DesktopUpdates.Current.IsUpdateReady)
                UpdateNotice.IsVisible = true;
        });

    private void DismissUpdateNoticeClick(object? sender, RoutedEventArgs e)
    {
        updateNoticeDismissed = true;
        UpdateNotice.IsVisible = false;
    }

    public void OpenConnectionSettings() => MainContent.OpenConnectionSettings();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || closingAfterCleanup || DataContext is not MainViewModel viewModel)
            return;

        e.Cancel = true;
        if (cleanupStarted) return;
        cleanupStarted = true;
        _ = CloseAfterCleanupAsync(viewModel);
    }

    private async System.Threading.Tasks.Task CloseAfterCleanupAsync(MainViewModel viewModel)
    {
        var cleanedUp = false;
        try
        {
            await viewModel.DisposeAsync();
            cleanedUp = true;
        }
        catch (System.Exception)
        {
            // Close the window even if the remote meter does not acknowledge release.
        }
        finally
        {
            if (cleanedUp)
                await DesktopUpdates.Current.PrepareForExitAsync();
            closingAfterCleanup = true;
            Close();
        }
    }
}
