using Avalonia.Controls;
using EMCLDesktop.ViewModels;
using EMCLDesktop.Services;

namespace EMCLDesktop.Views;

public partial class MainWindow : Window
{
    private bool closingAfterCleanup;
    private bool cleanupStarted;

    public MainWindow()
    {
        InitializeComponent();
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
