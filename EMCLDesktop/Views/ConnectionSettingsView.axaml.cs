using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Linq;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Views;

public partial class ConnectionSettingsView : UserControl
{
    public void NavigateTo(string section)
    {
        var target = section switch
        {
            "Connection" => ConnectionSection,
            "Address" => AddressSection,
            "Association" => AssociationSection,
            "Exchange" => ExchangeSection,
            "Advanced" => AdvancedSection,
            _ => null
        };
        target?.BringIntoView();
    }

    private void ToggleConformanceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var select = viewModel.ConformanceOptions.Any(option => !option.IsSelected);
        foreach (var option in viewModel.ConformanceOptions) option.IsSelected = select;
    }

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
