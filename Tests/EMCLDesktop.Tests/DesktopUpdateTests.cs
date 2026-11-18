using EMCL.Core.Objects;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public class DesktopUpdateTests
{
    [Fact]
    public async Task ApplyAsync_DefaultSettings_StartsUpdate()
    {
        await using var viewModel = new MainViewModel();
        var updater = new TestUpdater();

        await DesktopUpdates.ApplyAsync(updater, viewModel);

        Assert.NotNull(updater.RestartArgs);
        Assert.Empty(updater.RestartArgs);
    }

    [Theory]
    [InlineData(nameof(MainViewModel.TcpPort), "invalid")]
    [InlineData(nameof(MainViewModel.TcpPort), "65536")]
    [InlineData(nameof(MainViewModel.Host), "")]
    [InlineData(nameof(MainViewModel.SerialPortName), "", "Serial")]
    [InlineData(nameof(MainViewModel.SerialBaudRate), "0", "Serial")]
    [InlineData(nameof(MainViewModel.ClientAddress), "")]
    [InlineData(nameof(MainViewModel.ServerAddress), "65536")]
    [InlineData(nameof(MainViewModel.ServerPhysicalAddress), "invalid")]
    [InlineData(nameof(MainViewModel.HdlcMaxTxBytes), "2")]
    [InlineData(nameof(MainViewModel.HdlcAcknowledgementTimeoutSeconds), "0")]
    [InlineData(nameof(MainViewModel.AssociationVersion), "invalid")]
    [InlineData(nameof(MainViewModel.Authentication), "invalid")]
    [InlineData(nameof(MainViewModel.Authentication), "HighGMAC")]
    [InlineData(nameof(MainViewModel.ConnectTimeoutSeconds), "invalid")]
    [InlineData(nameof(MainViewModel.IdleTimeoutSeconds), "invalid")]
    [InlineData(nameof(MainViewModel.KeepAliveIntervalSeconds), "invalid")]
    [InlineData(nameof(MainViewModel.KeepAliveIntervalSeconds), "NaN")]
    [InlineData(nameof(MainViewModel.MaxReceivePduSize), "65536")]
    public async Task ApplyAsync_InvalidConnectionSetting_StartsUpdateWithoutSnapshot(
        string property, string value, string transport = "TCP")
    {
        await using var viewModel = CreateConfiguredViewModel();
        viewModel.Transport = transport;
        typeof(MainViewModel).GetProperty(property)!.SetValue(viewModel, value);
        using var export = new MemoryStream();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => viewModel.SaveConfigurationAsync(export));
        var updater = new TestUpdater();

        await DesktopUpdates.ApplyAsync(updater, viewModel);

        Assert.NotNull(updater.RestartArgs);
        Assert.Empty(updater.RestartArgs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_ValidSettings_PassesRestorableSnapshot(bool wasConnected)
    {
        await using var viewModel = CreateConfiguredViewModel();
        viewModel.IsConnected = wasConnected;
        viewModel.Objects.Add(new MeterObjectViewModel(LogicalName.Parse("1.0.1.8.0.255"), 3, 0, "Register"));
        var updater = new TestUpdater();

        await DesktopUpdates.ApplyAsync(updater, viewModel);

        string path = AssertSnapshotArgs(updater, wasConnected);
        try
        {
            Assert.False(viewModel.IsConnected);
            await using var restored = new MainViewModel();
            await using var snapshot = File.OpenRead(path);
            Assert.True(await restored.LoadConfigurationAsync(snapshot), restored.Status);
            Assert.Equal(viewModel.Host, restored.Host);
            Assert.Equal(viewModel.TcpPort, restored.TcpPort);
            Assert.Equal(viewModel.Objects[0].Obis, Assert.Single(restored.Objects).Obis);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ApplyAsync_InstallerFails_DeletesSnapshot()
    {
        await using var viewModel = CreateConfiguredViewModel();
        var installError = new InvalidOperationException("Installer failed");
        var updater = new TestUpdater { ApplyError = installError };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopUpdates.ApplyAsync(updater, viewModel));

        Assert.Same(installError, error);
        Assert.False(File.Exists(AssertSnapshotArgs(updater, wasConnected: false)));
    }

    [Fact]
    public async Task ApplyAsync_SnapshotCancelled_DoesNotStartUpdate()
    {
        await using var viewModel = CreateConfiguredViewModel();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsBusy) && viewModel.IsBusy)
                viewModel.CancelOperationCommand.Execute(null);
        };
        var updater = new TestUpdater();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DesktopUpdates.ApplyAsync(updater, viewModel));

        Assert.Null(updater.RestartArgs);
        Assert.False(viewModel.IsBusy);
    }

    private static MainViewModel CreateConfiguredViewModel() => new()
    {
        Host = "localhost",
        TcpPort = "4059",
        SerialPortName = "COM1"
    };

    private static string AssertSnapshotArgs(TestUpdater updater, bool wasConnected)
    {
        var args = Assert.IsType<string[]>(updater.RestartArgs);
        Assert.Equal(3, args.Length);
        Assert.Equal("--restore-update", args[0]);
        Assert.Equal(wasConnected ? "connected" : "disconnected", args[2]);
        return args[1];
    }

    private sealed class TestUpdater : IDesktopUpdates
    {
        public string Status => "";
        public bool IsUpdateReady => true;
        public event EventHandler? StatusChanged { add { } remove { } }
        public string[]? RestartArgs { get; private set; }
        public Exception? ApplyError { get; init; }

        public void Start() { }

        public void ApplyAndRestart(string[] restartArgs)
        {
            RestartArgs = restartArgs;
            if (ApplyError is { } error) throw error;
        }
    }
}
