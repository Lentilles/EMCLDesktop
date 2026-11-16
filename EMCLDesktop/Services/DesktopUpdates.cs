using System;
using System.Threading.Tasks;

namespace EMCLDesktop.Services;

public interface IDesktopUpdates
{
    string Status { get; }
    event EventHandler? StatusChanged;
    void Start();
    Task PrepareForExitAsync();
}

public static class DesktopUpdates
{
    public static IDesktopUpdates Current { get; set; } = new UnavailableUpdates();

    private sealed class UnavailableUpdates : IDesktopUpdates
    {
        public string Status => "Автоматическое обновление недоступно для этой сборки.";
        public event EventHandler? StatusChanged { add { } remove { } }
        public void Start() { }
        public Task PrepareForExitAsync() => Task.CompletedTask;
    }
}
