using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;
using System.Text;

namespace EMCLDesktop.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    private void NavigateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string index } &&
            DataContext is MainViewModel viewModel && int.TryParse(index, out int section))
            viewModel.SelectedTabIndex = section;
    }

    private void SettingsNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string section })
            ConnectionSettings.NavigateTo(section);
    }

    public void OpenConnectionSettings()
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SelectedTabIndex = 3;
    }

    private async void LoadConfigurationClick(object? sender, RoutedEventArgs e) =>
        await LoadConfigurationAsync();

    public async System.Threading.Tasks.Task LoadConfigurationAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || DataContext is not MainViewModel viewModel ||
            viewModel.IsBusy || viewModel.IsReading) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Откройте конфигурацию EMCL Desktop или EMCL.Examples",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            await viewModel.LoadConfigurationAsync(stream);
        }
        catch (System.Exception error)
        {
            viewModel.ReportError($"Не удалось открыть конфигурацию: {error.Message}");
        }
    }

    private async void SaveConfigurationClick(object? sender, RoutedEventArgs e) =>
        await SaveConfigurationAsync();

    public async System.Threading.Tasks.Task SaveConfigurationAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || DataContext is not MainViewModel viewModel ||
            viewModel.IsBusy || viewModel.IsReading) return;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить параметры и каталог (пароль будет записан в JSON)",
                SuggestedFileName = "emcldesktop.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await viewModel.SaveConfigurationAsync(stream);
        }
        catch (System.Exception error)
        {
            viewModel.ReportError($"Не удалось сохранить конфигурацию: {error.Message}");
        }
    }

    private async void CopyStatusClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.HasStatusError || string.IsNullOrEmpty(viewModel.Status) ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        var status = viewModel.Status;
        try
        {
            await clipboard.SetTextAsync(status);
        }
        catch (System.Exception error)
        {
            viewModel.ReportError($"Не удалось скопировать сообщение: {error.Message}. {status}");
        }
    }

    private async void CopyTraceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var trace = viewModel.GetTraceSnapshot();
        if (trace.Length == 0) return;
        try
        {
            await clipboard.SetTextAsync(trace);
            viewModel.Status = "Трассировка скопирована.";
        }
        catch (System.Exception error)
        {
            viewModel.ReportError($"Не удалось скопировать трассировку: {error.Message}");
        }
    }

    private async void SaveTraceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var trace = viewModel.GetTraceSnapshot();
        if (trace.Length == 0) return;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить трассировку EMCL Desktop",
                SuggestedFileName = "emcldesktop-trace.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Текст") { Patterns = ["*.txt"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await viewModel.SaveTraceAsync(stream);
        }
        catch (System.Exception error)
        {
            viewModel.ReportError($"Не удалось сохранить трассировку: {error.Message}");
        }
    }

    private void SelectObjectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MeterObjectViewModel obj } &&
            DataContext is MainViewModel viewModel)
            viewModel.SelectedObject = obj;
    }

    private async void ReadAttributeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AttributeViewModel attribute } &&
            DataContext is MainViewModel viewModel)
            await viewModel.ReadAttributeAsync(attribute);
    }

    private Control? dialogTrigger;

    private void BrowseAttributeClick(object? sender, RoutedEventArgs e) => OpenAttributeDialog(sender, edit: false);

    private void EditAttributeClick(object? sender, RoutedEventArgs e) => OpenAttributeDialog(sender, edit: true);

    private void OpenAttributeDialog(object? sender, bool edit)
    {
        if (sender is not Control { DataContext: AttributeViewModel attribute } source ||
            DataContext is not MainViewModel { SelectedObject: { } obj } viewModel)
            return;

        var dialog = new AttributeDialog { DataContext = new AttributeDialogViewModel(viewModel, obj, attribute, edit) };
        dialog.CloseRequested += (_, _) => CloseObjectDialog();
        ShowObjectDialog(dialog, source, 680);
        dialog.FocusAction();
    }

    private void OpenMethodClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: MethodViewModel method } source ||
            DataContext is not MainViewModel viewModel) return;

        var dialog = new MethodDialog { DataContext = method };
        dialog.CloseRequested += (_, _) => CloseObjectDialog();
        dialog.InvokeRequested += async (_, _) =>
        {
            CloseObjectDialog();
            await viewModel.InvokeMethodAsync(method);
        };
        ShowObjectDialog(dialog, source, 420);
        dialog.FocusAction();
    }

    private void ShowObjectDialog(Control dialog, Control source, double width)
    {
        dialogTrigger = source;
        ObjectDialog.Width = Math.Min(width, Math.Max(0, Bounds.Width - 32));
        ObjectDialog.MaxHeight = Math.Max(0, Bounds.Height - 32);
        ObjectDialog.Content = dialog;
        Workspace.IsEnabled = false;
        DialogOverlay.IsVisible = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!DialogOverlay.IsVisible || ObjectDialog.Content is not Control dialog) return;
        ObjectDialog.Width = Math.Min(dialog is AttributeDialog ? 680 : 420, Math.Max(0, e.NewSize.Width - 32));
        ObjectDialog.MaxHeight = Math.Max(0, e.NewSize.Height - 32);
    }

    private void DialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseObjectDialog();
        e.Handled = true;
    }

    private void CloseObjectDialog()
    {
        DialogOverlay.IsVisible = false;
        ObjectDialog.Content = null;
        Workspace.IsEnabled = true;
        dialogTrigger?.Focus();
        dialogTrigger = null;
    }

    private async void ReadProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MeterObjectViewModel obj } &&
            DataContext is MainViewModel viewModel)
            await viewModel.ReadProfileAsync(obj);
    }

    private void ProfilePreviousPageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileTableViewModel table })
            table.PreviousPage();
    }

    private void ProfileNextPageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileTableViewModel table })
            table.NextPage();
    }
}
