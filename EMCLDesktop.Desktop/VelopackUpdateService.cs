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

    public string Status => status;
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
                SetStatus("Обновление загружено и будет установлено при закрытии приложения.");
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
            SetStatus("Обновление загружено и будет установлено при закрытии приложения.");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            Trace.WriteLine($"Update check failed: {error}");
            SetStatus("Не удалось проверить или загрузить обновление. Повторная попытка будет выполнена при следующем запуске.");
        }
    }

    public async Task PrepareForExitAsync()
    {
        stopping.Cancel();
        // A check may be waiting for the network and has no cancellation API. Never delay exit for it.
        if (check is { IsCompleted: false })
            await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(2)));
        try
        {
            if (check is { IsCompleted: false } || manager?.UpdatePendingRestart is not { } pending)
                return;
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(current.ProcessName);
            try
            {
                // Never ask the updater to replace files while another instance is running.
                if (processes.Any(process => process.Id != current.Id)) return;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch (Exception error)
        {
            Trace.WriteLine($"Update scheduling failed: {error}");
        }
    }
}
