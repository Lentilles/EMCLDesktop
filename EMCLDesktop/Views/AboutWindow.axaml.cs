using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EMCLDesktop.Services;

namespace EMCLDesktop.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = typeof(App).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = $"Версия {version}";
        UpdateStatusText.Text = DesktopUpdates.Current.Status;
        DesktopUpdates.Current.StatusChanged += OnUpdateStatusChanged;
        Closed += (_, _) => DesktopUpdates.Current.StatusChanged -= OnUpdateStatusChanged;
    }

    private void OnUpdateStatusChanged(object? sender, System.EventArgs e) =>
        Dispatcher.UIThread.Post(() => UpdateStatusText.Text = DesktopUpdates.Current.Status);

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
