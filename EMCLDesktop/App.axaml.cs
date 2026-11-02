using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EMCLDesktop.ViewModels;
using EMCLDesktop.Views;

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

    private async void ConnectionSettingsMenuItemClick(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            { MainWindow.Content: MainView mainView })
            await mainView.OpenConnectionSettingsAsync();
    }
}
