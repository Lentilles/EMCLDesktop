using System;
using System.IO;
using System.Threading.Tasks;
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
    private bool updateInProgress;

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

    private async void ApplyUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsBusy || vm.IsReading || cleanupStarted || updateInProgress) return;
        updateInProgress = true;
        try
        {
            await DesktopUpdates.ApplyAsync(DesktopUpdates.Current, vm);
        }
        catch (OperationCanceledException)
        {
            vm.Status = "Обновление отменено.";
            return;
        }
        catch (Exception error)
        {
            vm.ReportError($"Не удалось запустить обновление: {error.Message}");
            return;
        }
        finally
        {
            updateInProgress = false;
        }
        Close();
    }

    public async Task RestoreAfterUpdateAsync(string path, bool reconnect)
    {
        if (DataContext is not MainViewModel vm) return;
        var fullPath = Path.GetFullPath(path);
        if (Path.GetDirectoryName(fullPath) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            !Path.GetFileName(fullPath).StartsWith("emcl-update-", StringComparison.Ordinal) ||
            !Path.GetFileName(fullPath).EndsWith(".json", StringComparison.Ordinal)) return;
        try
        {
            bool loaded;
            await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
                loaded = await vm.LoadConfigurationAsync(stream);
            if (loaded && reconnect) await vm.ReconnectAfterUpdateAsync();
        }
        catch (Exception error) { vm.Status = $"Не удалось восстановить состояние после обновления: {error.Message}"; }
        finally { DesktopUpdates.DeleteSnapshot(fullPath); }
    }

    public void OpenConnectionSettings() => MainContent.OpenConnectionSettings();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (updateInProgress) { e.Cancel = true; return; }
        if (e.Cancel || closingAfterCleanup || DataContext is not MainViewModel viewModel)
            return;

        e.Cancel = true;
        if (cleanupStarted) return;
        cleanupStarted = true;
        _ = CloseAfterCleanupAsync(viewModel);
    }

    private async System.Threading.Tasks.Task CloseAfterCleanupAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.DisposeAsync();
        }
        catch (System.Exception)
        {
            // Close the window even if the remote meter does not acknowledge release.
        }
        finally
        {
            closingAfterCleanup = true;
            Close();
        }
    }
}
