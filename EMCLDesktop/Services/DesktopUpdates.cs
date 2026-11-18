using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Services;

public interface IDesktopUpdates
{
    string Status { get; }
    bool IsUpdateReady { get; }
    event EventHandler? StatusChanged;
    void Start();
    void ApplyAndRestart(string[] restartArgs);
}

public static class DesktopUpdates
{
    public static IDesktopUpdates Current { get; set; } = new UnavailableUpdates();

    public static async Task ApplyAsync(IDesktopUpdates updater, MainViewModel viewModel)
    {
        bool wasConnected = viewModel.IsConnected;
        string? snapshotPath = await TrySaveSnapshotAsync(viewModel);
        try
        {
            if (wasConnected) await viewModel.DisconnectForUpdateAsync();
            updater.ApplyAndRestart(snapshotPath is null
                ? []
                : ["--restore-update", snapshotPath, wasConnected ? "connected" : "disconnected"]);
        }
        catch
        {
            if (snapshotPath is not null) DeleteSnapshot(snapshotPath);
            if (wasConnected && !viewModel.IsConnected) await viewModel.ReconnectAfterUpdateAsync();
            throw;
        }
    }

    private static async Task<string?> TrySaveSnapshotAsync(MainViewModel viewModel)
    {
        string? createdPath = null;
        bool saved = false;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"emcl-update-{Guid.NewGuid():N}.json");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(path, options))
            {
                createdPath = path;
                await viewModel.SaveUpdateSnapshotAsync(stream);
            }
            saved = true;
            return path;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Trace.WriteLine($"Update snapshot skipped: {error.GetType().Name}");
            return null;
        }
        finally
        {
            if (!saved && createdPath is not null) DeleteSnapshot(createdPath);
        }
    }

    internal static void DeleteSnapshot(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Update snapshot cleanup failed: {error.GetType().Name}");
        }
    }

    private sealed class UnavailableUpdates : IDesktopUpdates
    {
        public string Status => "Автоматическое обновление недоступно для этой сборки.";
        public bool IsUpdateReady => false;
        public event EventHandler? StatusChanged { add { } remove { } }
        public void Start() { }
        public void ApplyAndRestart(string[] restartArgs) => throw new InvalidOperationException("Обновление недоступно.");
    }
}
