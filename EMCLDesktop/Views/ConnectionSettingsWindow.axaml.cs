using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EMCLDesktop.Views;

public partial class ConnectionSettingsWindow : Window
{
    public ConnectionSettingsWindow()
    {
        InitializeComponent();
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
