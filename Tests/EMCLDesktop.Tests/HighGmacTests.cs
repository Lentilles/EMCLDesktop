using EMCL.Protocol.Security;
using EMCLDesktop.Services;
using Xunit;

namespace EMCLDesktop.Tests;

public sealed class HighGmacTests : IDisposable
{
    private const string OutgoingCounterKey = "tx:test";
    private const string IncomingCounterKey = "rx:test";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"emcldesktop-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Reserve_NewStore_StartsAtConfiguredMinimum()
    {
        // Arrange
        var store = new PersistentInvocationCounterStore(_directory, 10);

        // Act
        var counter = store.Reserve(OutgoingCounterKey);

        // Assert
        Assert.Equal(10u, counter);
    }

    [Theory]
    [InlineData(3u, 11u)]
    [InlineData(20u, 20u)]
    public void Reserve_ReopenedStore_UsesHigherOfPersistedNextCounterAndConfiguredMinimum(
        uint minimum, uint expected)
    {
        // Arrange
        new PersistentInvocationCounterStore(_directory, 10).Reserve(OutgoingCounterKey);
        var reopened = new PersistentInvocationCounterStore(_directory, minimum);

        // Act
        var counter = reopened.Reserve(OutgoingCounterKey);

        // Assert
        Assert.Equal(expected, counter);
    }

    [Fact]
    public async Task Reserve_ConcurrentCalls_ReturnDistinctContiguousCounters()
    {
        // Arrange
        var store = new PersistentInvocationCounterStore(_directory, 10);

        // Act
        var counters = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.Reserve(OutgoingCounterKey))));

        // Assert
        Assert.Equal(Enumerable.Range(10, 16).Select(counter => (uint)counter), counters.Order());
    }

    [Fact]
    public void TryAccept_NewStore_AcceptsFirstIncomingCounter()
    {
        // Arrange
        var store = new PersistentInvocationCounterStore(_directory, 0);

        // Act
        var accepted = store.TryAccept(IncomingCounterKey, 7);

        // Assert
        Assert.True(accepted);
    }

    [Theory]
    [InlineData(7u, false)]
    [InlineData(8u, true)]
    public void TryAccept_ReopenedStore_AcceptsOnlyCounterAbovePersistedValue(uint counter, bool expected)
    {
        // Arrange
        new PersistentInvocationCounterStore(_directory, 0).TryAccept(IncomingCounterKey, 7);
        var reopened = new PersistentInvocationCounterStore(_directory, 0);

        // Act
        var accepted = reopened.TryAccept(IncomingCounterKey, counter);

        // Assert
        Assert.Equal(expected, accepted);
    }

    [Fact]
    public void CreateProtocol_ValidKeys_ReturnsAuthenticationProtocol()
    {
        // Arrange
        var settings = CreateSettings();
        var store = new MemoryInvocationCounterStore(settings.ParseInitialCounter());

        // Act
        var protocol = settings.CreateProtocol(store);

        // Assert
        Assert.NotNull(protocol);
    }

    [Fact]
    public void CreateProtocol_ShortAuthenticationKey_ThrowsArgumentException()
    {
        // Arrange
        var settings = CreateSettings() with { AuthenticationKey = "00" };
        var store = new MemoryInvocationCounterStore();

        // Act
        var exception = Record.Exception(() => settings.CreateProtocol(store));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static HighGmacSettings CreateSettings() =>
        new(new string('1', 32), new string('2', 32), "4142434445464748", "494A4B4C4D4E4F50", "10");
}
