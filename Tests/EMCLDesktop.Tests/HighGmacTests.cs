using EMCL.Protocol.Security;
using EMCLDesktop.Services;
using Xunit;

namespace EMCLDesktop.Tests;

public sealed class HighGmacTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "emcldesktop-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OutgoingCountersSurviveRestartAndHonorManualMinimum()
    {
        Assert.Equal(10u, new PersistentInvocationCounterStore(directory, 10).Reserve("tx:test"));
        Assert.Equal(11u, new PersistentInvocationCounterStore(directory, 3).Reserve("tx:test"));
        Assert.Equal(20u, new PersistentInvocationCounterStore(directory, 20).Reserve("tx:test"));
    }

    [Fact]
    public async Task ConcurrentReservationsAreUnique()
    {
        var store = new PersistentInvocationCounterStore(directory, 10);
        var counters = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.Reserve("tx:test"))));
        Assert.Equal(Enumerable.Range(10, 16).Select(i => (uint)i), counters.Order());
    }

    [Fact]
    public void IncomingCountersRejectReplayAfterRestart()
    {
        Assert.True(new PersistentInvocationCounterStore(directory, 0).TryAccept("rx:test", 7));
        var reopened = new PersistentInvocationCounterStore(directory, 0);
        Assert.False(reopened.TryAccept("rx:test", 7));
        Assert.True(reopened.TryAccept("rx:test", 8));
    }

    [Fact]
    public void SecuritySettingsValidateKeyLength()
    {
        var settings = new HighGmacSettings(new string('1', 32), new string('2', 32),
            "4142434445464748", "494A4B4C4D4E4F50", "10");
        Assert.NotNull(settings.CreateProtocol(new MemoryInvocationCounterStore(settings.ParseInitialCounter())));
        Assert.Throws<ArgumentException>(() => (settings with { AuthenticationKey = "00" })
            .CreateProtocol(new MemoryInvocationCounterStore()));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
