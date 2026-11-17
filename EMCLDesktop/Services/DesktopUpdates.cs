using System;
using System.Threading.Tasks;

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

    private sealed class UnavailableUpdates : IDesktopUpdates
    {
        public string Status => "Автоматическое обновление недоступно для этой сборки.";
        public bool IsUpdateReady => false;
        public event EventHandler? StatusChanged { add { } remove { } }
        public void Start() { }
        public void ApplyAndRestart(string[] restartArgs) => throw new InvalidOperationException("Обновление недоступно.");
    }
}
