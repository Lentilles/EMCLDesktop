using Avalonia.Controls;

namespace EMCLDesktop.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ContentView.CloseRequested += (_, _) => Close();
    }
}
