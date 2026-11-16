using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EMCLDesktop.Tests;

public class ExistingChecksTests
{
    [Fact] public async Task ConnectionAndTrace() => Assert.True(await ConnectionAndTraceChecks.RunAsync() > 0);
    [Fact] public async Task PacketInspection() => Assert.True(await PacketInspectionChecks.RunAsync() > 0);
    [Fact] public async Task AttributeWrites() => Assert.True(await AttributeWriteChecks.RunAsync() > 0);
    [Fact] public void ProfileTable() => Assert.True(ProfileTableChecks.Run() > 0);
    [Fact] public async Task CatalogTypes() => Assert.True(await CatalogTypeChecks.RunAsync() > 0);
    [Fact] public async Task Cancellation() => Assert.True(await CancellationChecks.RunAsync() > 0);
    [Fact] public async Task HdlcSizing() => Assert.True(await HdlcSizingChecks.RunAsync() > 0);
}
