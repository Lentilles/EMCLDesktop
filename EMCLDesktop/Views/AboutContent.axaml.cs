using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EMCLDesktop.Services;

namespace EMCLDesktop.Views;

public partial class AboutContent : UserControl
{
    public event EventHandler? CloseRequested;

    public AboutContent()
    {
        InitializeComponent();
        VersionText.Text = $"Версия {ApplicationVersion.Current}";
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateStatusText.Text = DesktopUpdates.Current.Status;
        DesktopUpdates.Current.StatusChanged += OnUpdateStatusChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DesktopUpdates.Current.StatusChanged -= OnUpdateStatusChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public void FocusClose() => CloseButton.Focus();

    private void OnUpdateStatusChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => UpdateStatusText.Text = DesktopUpdates.Current.Status);

    private void CloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
