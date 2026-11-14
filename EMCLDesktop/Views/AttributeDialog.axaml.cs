using Avalonia.Controls;
using Avalonia.Interactivity;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Views;

public partial class AttributeDialog : UserControl
{
    public AttributeDialog() => InitializeComponent();

    internal event EventHandler? CloseRequested;

    internal void FocusAction()
    {
        if (DataContext is AttributeDialogViewModel { IsEditing: true } editor)
        {
            if (editor.ShowLeafEditor) LeafValue.Focus();
            else ApplyButton.Focus();
        }
        else CloseButton.Focus();
    }

    private void EditClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AttributeDialogViewModel editor) return;
        editor.BeginEdit();
        FocusAction();
    }

    private void EditWholeValueClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AttributeDialogViewModel editor) editor.BeginWholeValueEdit();
    }

    private void UseReadValueClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AttributeDialogViewModel editor) editor.Editor.UseReadValue();
    }

    private void WriteOctetFormatRequested(object? sender, OctetFormatRequestedEventArgs e)
    {
        if (DataContext is not AttributeDialogViewModel editor) return;
        e.Handled = true;
        editor.Editor.TryChangeWriteOctetFormat(e.Format);
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ApplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AttributeDialogViewModel editor && editor.Apply())
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
