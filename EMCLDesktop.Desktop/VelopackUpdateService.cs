using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EMCLDesktop.Services;
using Velopack;

namespace EMCLDesktop.Desktop;

internal sealed class VelopackUpdateService : IDesktopUpdates
{
    private const string Feed = "https://github.com/Lentilles/EMCLDesktop/releases/latest/download";
    private readonly CancellationTokenSource stopping = new();
    private UpdateManager? manager;
    private Task? check;
    private string status = "Проверка обновлений ещё не выполнялась.";
    private volatile bool isUpdateReady;

    public string Status => status;
    public bool IsUpdateReady => isUpdateReady;
    public event EventHandler? StatusChanged;

    private void SetStatus(string value)
    {
        status = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Start() => check ??= CheckAsync();

    private async Task CheckAsync()
    {
        try
        {
            manager = new UpdateManager(Feed);
            if (!manager.IsInstalled)
            {
                SetStatus("Автоматическое обновление недоступно для этой сборки. Установите сборку Velopack из GitHub Releases.");
                return;
            }

            if (manager.UpdatePendingRestart is not null)
            {
                isUpdateReady = true;
                SetStatus("Обновление загружено. Нажмите «Обновить» в главном окне.");
                return;
            }

            SetStatus("Проверка обновлений…");
            var update = await manager.CheckForUpdatesAsync();
            if (stopping.IsCancellationRequested) return;
            if (update is null)
            {
                SetStatus("Установлена последняя версия.");
                return;
            }

            SetStatus("Загрузка обновления…");
            await manager.DownloadUpdatesAsync(update, cancelToken: stopping.Token);
            if (stopping.IsCancellationRequested) return;
            isUpdateReady = true;
            SetStatus("Обновление загружено. Нажмите «Обновить» в главном окне.");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            Trace.WriteLine($"Update check failed: {error}");
            SetStatus("Не удалось проверить или загрузить обновление. Повторная попытка будет выполнена при следующем запуске.");
        }
    }

    public void ApplyAndRestart(string[] restartArgs)
    {
        if (!isUpdateReady || manager?.UpdatePendingRestart is not { } pending)
            throw new InvalidOperationException("Загруженное обновление не найдено.");
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(current.ProcessName);
        try
        {
            if (processes.Any(process => process.Id != current.Id))
                throw new InvalidOperationException("Закройте другие экземпляры приложения и повторите попытку.");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, restartArgs: restartArgs);
    }
}
