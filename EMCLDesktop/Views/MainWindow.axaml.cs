using Avalonia.Controls;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Views;

public partial class MainWindow : Window
{
    private bool closingAfterCleanup;
    private bool cleanupStarted;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void FileMenuNeedsUpdate(object? sender, System.EventArgs e)
    {
        bool canEdit = IsEnabled && DataContext is MainViewModel { CanEditConfiguration: true };
        if (sender is NativeMenu menu)
            foreach (var item in menu.Items)
                if (item is NativeMenuItem action)
                    action.IsEnabled = canEdit;
    }

    private async void LoadConfigurationMenuItemClick(object? sender, System.EventArgs e)
    {
        if (Content is MainView mainView)
            await mainView.LoadConfigurationAsync();
    }

    private async void SaveConfigurationMenuItemClick(object? sender, System.EventArgs e)
    {
        if (Content is MainView mainView)
            await mainView.SaveConfigurationAsync();
    }

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
