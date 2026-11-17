using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EMCLDesktop.ViewModels;
using EMCLDesktop.Views;
using EMCLDesktop.Services;

namespace EMCLDesktop;

public partial class App : Application
{
    private AboutWindow? aboutWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
            desktop.MainWindow.Opened += async (_, _) =>
            {
                DesktopUpdates.Current.Start();
                var args = desktop.Args;
                if (args is ["--restore-update", var path, var state] && desktop.MainWindow is MainWindow window)
                    await window.RestoreAfterUpdateAsync(path, state == "connected");
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void AboutMenuItemClick(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow: { IsVisible: true } owner })
            return;

        await ShowAboutAsync(owner);
    }

    public async System.Threading.Tasks.Task ShowAboutAsync(Avalonia.Controls.Window owner)
    {
        if (aboutWindow is not null)
        {
            aboutWindow.Activate();
            return;
        }

        try
        {
            aboutWindow = new AboutWindow();
            await aboutWindow.ShowDialog(owner);
        }
        finally
        {
            aboutWindow = null;
        }
    }

    private void ConnectionSettingsMenuItemClick(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            { MainWindow: MainWindow mainWindow })
            mainWindow.OpenConnectionSettings();
    }
}
