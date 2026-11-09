using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Views;

public partial class OctetStringField : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<OctetStringField, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<OctetStringFormat?> FormatProperty =
        AvaloniaProperty.Register<OctetStringField, OctetStringFormat?>(nameof(Format), OctetStringFormat.Hex,
            defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IReadOnlyList<OctetStringPreview>?> PreviewsProperty =
        AvaloniaProperty.Register<OctetStringField, IReadOnlyList<OctetStringPreview>?>(nameof(Previews));
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<OctetStringField, bool>(nameof(IsReadOnly), true);

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public OctetStringFormat? Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public IReadOnlyList<OctetStringPreview>? Previews { get => GetValue(PreviewsProperty); set => SetValue(PreviewsProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public event EventHandler<OctetFormatRequestedEventArgs>? FormatRequested;

    public OctetStringField()
    {
        InitializeComponent();
        ValueBox.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (IsReadOnly && !OpenFormatsButton.IsPointerOver &&
                e.GetCurrentPoint(ValueBox).Properties.IsLeftButtonPressed)
                FormatsPopup.IsOpen = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F4 || (e.Key == Key.Down && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
            {
                FormatsPopup.IsOpen = !FormatsPopup.IsOpen;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) FormatsPopup.IsOpen = false;
        }, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => FormatsPopup.IsOpen = false;
    }

    private void OpenFormatsClick(object? sender, RoutedEventArgs e) => FormatsPopup.IsOpen = !FormatsPopup.IsOpen;

    private void ChooseFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: OctetStringPreview preview }) return;
        var request = new OctetFormatRequestedEventArgs(preview.Format);
        FormatRequested?.Invoke(this, request);
        if (!request.Handled) SetCurrentValue(FormatProperty, preview.Format);
        FormatsPopup.IsOpen = false;
        ValueBox.Focus();
    }
}

public sealed class OctetFormatRequestedEventArgs(OctetStringFormat format) : EventArgs
{
    public OctetStringFormat Format { get; } = format;
    public bool Handled { get; set; }
}
