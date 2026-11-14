using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EMCLDesktop.Views;

public partial class MethodDialog : UserControl
{
    public MethodDialog() => InitializeComponent();

    internal event EventHandler? CloseRequested;
    internal event EventHandler? InvokeRequested;
    internal void FocusAction() => Include.Focus();

    private void CloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void InvokeClick(object? sender, RoutedEventArgs e) => InvokeRequested?.Invoke(this, EventArgs.Empty);
}
