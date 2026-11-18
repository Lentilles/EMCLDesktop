using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Views;

public partial class PacketInspectorView : UserControl
{
    private PacketInspectorViewModel? observedInspector;
    private PacketInspectorViewModel? selectionOwner;
    private INotifyCollectionChanged? observedPackets;
    private readonly Dictionary<INotifyCollectionChanged, SelectionModel<PacketItemViewModel>> packetSelections = [];
    private bool isLoaded;
    private bool scrollPending;
    private bool pointerDown;
    private long lastScrollInput;
    private readonly DispatcherTimer hideScrollToFirstTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer hideScrollToLastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public PacketInspectorView()
    {
        InitializeComponent();
        Loaded += (_, _) => { isLoaded = true; ObserveInspector(); };
        Unloaded += (_, _) => { isLoaded = false; HideScrollButtons(); StopObserving(); };
        DataContextChanged += (_, _) => { if (isLoaded) ObserveInspector(); };
        hideScrollToFirstTimer.Tick += (_, _) => HideScrollButton(ScrollToFirstButton, hideScrollToFirstTimer);
        hideScrollToLastTimer.Tick += (_, _) => HideScrollButton(ScrollToLastButton, hideScrollToLastTimer);
        PacketList.AddHandler(ScrollViewer.ScrollChangedEvent, PacketListScrollChanged, RoutingStrategies.Bubble, true);
        PacketList.AddHandler(InputElement.PointerWheelChangedEvent, PacketListPointerWheel, RoutingStrategies.Tunnel, true);
        PacketList.AddHandler(InputElement.PointerPressedEvent, PacketListPointerPressed, RoutingStrategies.Tunnel, true);
        PacketList.AddHandler(InputElement.PointerMovedEvent, PacketListPointerMoved, RoutingStrategies.Tunnel, true);
        PacketList.AddHandler(InputElement.PointerReleasedEvent, PacketListPointerReleased, RoutingStrategies.Tunnel, true);
        PacketList.AddHandler(InputElement.KeyDownEvent, PacketListKeyDown, RoutingStrategies.Tunnel, true);
    }

    private PacketInspectorViewModel? Inspector => DataContext as PacketInspectorViewModel;
    private void ObserveInspector()
    {
        StopObserving();
        observedInspector = Inspector;
        if (!ReferenceEquals(selectionOwner, observedInspector))
        {
            packetSelections.Clear();
            selectionOwner = observedInspector;
        }
        if (observedInspector is null) return;
        observedInspector.PropertyChanged += InspectorPropertyChanged;
        ObservePackets();
    }

    private void StopObserving()
    {
        pointerDown = false;
        lastScrollInput = 0;
        if (observedInspector is not null) observedInspector.PropertyChanged -= InspectorPropertyChanged;
        StopObservingPackets();
        observedInspector = null;
        observedPackets = null;
    }

    private void InspectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketInspectorViewModel.Packets)) ObservePackets();
        if (e.PropertyName == nameof(PacketInspectorViewModel.SelectedPacket) && observedInspector is { } inspector &&
            ReferenceEquals(observedPackets, inspector.Packets) && PacketList.Selection is SelectionModel<PacketItemViewModel> selection &&
            !ReferenceEquals(selection.SelectedItem, inspector.SelectedPacket))
            selection.SelectedItem = inspector.SelectedPacket;
    }

    private void ObservePackets()
    {
        StopObservingPackets();
        HideScrollButtons();
        var packets = observedInspector!.Packets;
        if (!packetSelections.TryGetValue(packets, out var selection))
        {
            selection = new(packets) { SingleSelect = false };
            packetSelections.Add(packets, selection);
        }
        if (selection.Count == 0) selection.SelectedItem = observedInspector.SelectedPacket;

        // Swap the items and their selection together so the previous source keeps its selection.
        PacketList.BeginInit();
        PacketList.ItemsSource = packets;
        PacketList.Selection = selection;
        PacketList.EndInit();

        observedPackets = packets;
        packets.CollectionChanged += PacketsChanged;
        selection.SelectionChanged += PacketSelectionChanged;
        ApplyPacketSelection(selection);
        RequestScrollToLast();
    }

    private void StopObservingPackets()
    {
        if (observedPackets is null) return;
        observedPackets.CollectionChanged -= PacketsChanged;
        packetSelections[observedPackets].SelectionChanged -= PacketSelectionChanged;
        observedPackets = null;
    }

    private void PacketSelectionChanged(object? sender, SelectionModelSelectionChangedEventArgs<PacketItemViewModel> e)
    {
        if (sender is SelectionModel<PacketItemViewModel> selection) ApplyPacketSelection(selection);
    }

    private void ApplyPacketSelection(SelectionModel<PacketItemViewModel> selection)
    {
        if (observedInspector is { } inspector) inspector.SelectedPacket = selection.SelectedItem;
    }

    private void ScrollToFirstClick(object? sender, RoutedEventArgs e)
    {
        lastScrollInput = 0;
        HideScrollButtons();
        AutoScrollToggle.IsChecked = false;
        if (Inspector?.Packets is { Count: > 0 } packets) PacketList.ScrollIntoView(packets[0]);
    }

    private void ScrollToLastClick(object? sender, RoutedEventArgs e)
    {
        lastScrollInput = 0;
        HideScrollButtons();
        if (Inspector?.Packets is { Count: > 0 } packets) PacketList.ScrollIntoView(packets[^1]);
    }

    private void ScrollButtonPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender == ScrollToFirstButton) hideScrollToFirstTimer.Stop();
        else if (sender == ScrollToLastButton) hideScrollToLastTimer.Stop();
    }

    private void ScrollButtonPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender == ScrollToFirstButton) RestartHideTimer(ScrollToFirstButton, hideScrollToFirstTimer);
        else if (sender == ScrollToLastButton) RestartHideTimer(ScrollToLastButton, hideScrollToLastTimer);
    }

    private void PacketListPointerWheel(object? sender, PointerWheelEventArgs e) => RememberScrollInput();
    private void PacketListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        pointerDown = true;
        RememberScrollInput();
    }
    private void PacketListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (pointerDown) RememberScrollInput();
    }
    private void PacketListPointerReleased(object? sender, PointerReleasedEventArgs e) => pointerDown = false;
    private void PacketListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            RememberScrollInput();
    }
    private void RememberScrollInput() => lastScrollInput = Stopwatch.GetTimestamp();

    private void PacketListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!isLoaded || e.Source is not ScrollViewer viewer || e.OffsetDelta.Y == 0) return;
        bool fromUser = lastScrollInput != 0 && Stopwatch.GetElapsedTime(lastScrollInput) < TimeSpan.FromSeconds(1);
        if (!fromUser) return;
        double maxOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        if (viewer.Offset.Y <= 0 && !ScrollToFirstButton.IsPointerOver)
            HideScrollButton(ScrollToFirstButton, hideScrollToFirstTimer);
        if (viewer.Offset.Y >= maxOffset && !ScrollToLastButton.IsPointerOver)
            HideScrollButton(ScrollToLastButton, hideScrollToLastTimer);

        if (e.OffsetDelta.Y < 0 && viewer.Offset.Y > 0)
        {
            AutoScrollToggle.IsChecked = false;
            ScrollToFirstButton.IsVisible = true;
        }
        else if (e.OffsetDelta.Y > 0 && viewer.Offset.Y < maxOffset)
        {
            ScrollToLastButton.IsVisible = true;
        }
        RestartHideTimer(ScrollToFirstButton, hideScrollToFirstTimer);
        RestartHideTimer(ScrollToLastButton, hideScrollToLastTimer);
    }

    private static void RestartHideTimer(Button button, DispatcherTimer timer)
    {
        timer.Stop();
        if (button.IsVisible && !button.IsPointerOver) timer.Start();
    }

    private static void HideScrollButton(Button button, DispatcherTimer timer)
    {
        timer.Stop();
        if (!button.IsPointerOver) button.IsVisible = false;
    }

    private void HideScrollButtons()
    {
        hideScrollToFirstTimer.Stop();
        hideScrollToLastTimer.Stop();
        ScrollToFirstButton.IsVisible = false;
        ScrollToLastButton.IsVisible = false;
    }

    private void PacketsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestScrollToLast();
    private void AutoScrollClick(object? sender, RoutedEventArgs e) => RequestScrollToLast();

    private void RequestScrollToLast()
    {
        if (!isLoaded || AutoScrollToggle.IsChecked != true || scrollPending) return;
        scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            scrollPending = false;
            if (isLoaded && AutoScrollToggle.IsChecked == true && Inspector?.Packets is { Count: > 0 } packets)
                PacketList.ScrollIntoView(packets[^1]);
        }, DispatcherPriority.Background);
    }

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
