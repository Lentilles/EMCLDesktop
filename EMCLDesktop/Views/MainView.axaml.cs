using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using EMCLDesktop.ViewModels;
using System.Text;

namespace EMCLDesktop.Views;

public partial class MainView : UserControl
{
    private ConnectionSettingsWindow? connectionSettingsWindow;

    public MainView()
    {
        InitializeComponent();
    }

    private async void OpenConnectionSettingsClick(object? sender, RoutedEventArgs e) =>
        await OpenConnectionSettingsAsync();

    public async System.Threading.Tasks.Task OpenConnectionSettingsAsync()
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.CanEditConfiguration ||
            connectionSettingsWindow is not null || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        try
        {
            connectionSettingsWindow = new ConnectionSettingsWindow { DataContext = viewModel };
            await connectionSettingsWindow.ShowDialog(owner);
        }
        catch (System.Exception error)
        {
            viewModel.ReportError($"Не удалось открыть параметры подключения: {error.Message}");
        }
        finally
        {
            connectionSettingsWindow = null;
        }
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
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(trace);
            viewModel.Status = "Трассировка сохранена.";
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
