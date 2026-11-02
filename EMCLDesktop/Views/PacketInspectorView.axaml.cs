using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Views;

public partial class PacketInspectorView : UserControl
{
    public PacketInspectorView() => InitializeComponent();
    private PacketInspectorViewModel? Inspector => DataContext as PacketInspectorViewModel;
    private void FieldPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: InspectorFieldViewModel field }) Inspector?.HoverField(field);
    }
    private void FieldPointerExited(object? sender, PointerEventArgs e) => Inspector?.HoverField(null);
    private void BytePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: InspectorByteViewModel value })
        {
            Inspector?.HoverByte(value);
            RevealField(value.Owner);
        }
    }
    private void BytePointerExited(object? sender, PointerEventArgs e) => Inspector?.HoverByte(null);
    private void ByteGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InspectorByteViewModel value }) Inspector?.HoverByte(value);
    }
    private void ByteLostFocus(object? sender, RoutedEventArgs e) => Inspector?.HoverByte(null);
    private void RevealField(InspectorFieldViewModel? field)
    {
        if (field is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            InspectorFieldsTree.GetVisualDescendants().OfType<TreeViewItem>()
                .FirstOrDefault(item => ReferenceEquals(item.DataContext, field))?.BringIntoView();
        }, DispatcherPriority.Background);
    }
    private void ByteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InspectorByteViewModel value }) Inspector?.SelectByte(value);
    }
    private void RelatedPacketClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RelatedPacketViewModel related }) Inspector?.SelectRelatedCommand.Execute(related);
    }
}
