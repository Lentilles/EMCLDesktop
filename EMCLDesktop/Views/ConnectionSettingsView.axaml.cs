using Avalonia.Controls;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Views;

public partial class ConnectionSettingsView : UserControl
{
    public ConnectionSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel && viewModel.IsSerial)
                viewModel.RefreshSerialPortsCommand.Execute(null);
        };
    }
}
