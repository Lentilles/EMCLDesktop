using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

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
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
