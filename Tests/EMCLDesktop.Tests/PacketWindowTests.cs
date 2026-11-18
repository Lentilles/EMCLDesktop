using EMCLDesktop.Services;
using EMCLDesktop.Services.PacketInspection;
using Xunit;
using static EMCLDesktop.Tests.PacketTestData;

namespace EMCLDesktop.Tests;

public class PacketWindowTests
{
    [Fact]
    public void Feed_PacketLimitExceeded_EvictsOldPacketsAndRetainsRequestContext()
    {
        // Arrange
        var analyzer = new PacketAnalyzer(2);
        analyzer.Feed(new(CreateWrapperFrame(GetRequestApdu), "TX", "Wrapper"));
        analyzer.Feed(new(CreateWrapperFrame(GetRequestApdu), "TX", "Wrapper", SessionId: 2));
        analyzer.Feed(new(CreateWrapperFrame(GetRequestApdu), "TX", "Wrapper", SessionId: 3));

        // Act
        analyzer.Feed(new(CreateWrapperFrame(GetResponseApdu, true), "RX", "Wrapper"));
        var retained = analyzer.Snapshot();

        // Assert
        Assert.Equal(4, analyzer.TotalPackets);
        Assert.Equal(new long[] { 3, 4 }, retained.Select(p => p.Id));
        Assert.Equal("1.0.32.7.0.255", retained[^1].Target?.Obis);
        Assert.Equal(new long[] { 1 }, retained[^1].RelatedPacketIds);
    }

    [Fact]
    public void TakeChanges_AfterPublishingPendingPackets_ReturnsEmpty()
    {
        // Arrange
        var analyzer = new PacketAnalyzer(10);
        analyzer.Feed(new(CreateWrapperFrame(GetRequestApdu), "TX", "Wrapper"));
        var published = analyzer.TakeChanges();

        // Act
        var changes = analyzer.TakeChanges();

        // Assert
        Assert.Single(published);
        Assert.Empty(changes);
    }

    [Fact]
    public void TakeChanges_ResponseToPublishedRequest_ReturnsUpdatedRequestAndNewResponse()
    {
        // Arrange
        var analyzer = new PacketAnalyzer(10);
        analyzer.Feed(new(CreateWrapperFrame(GetRequestApdu), "TX", "Wrapper"));
        analyzer.TakeChanges();
        analyzer.Feed(new(CreateWrapperFrame(GetResponseApdu, true), "RX", "Wrapper"));

        // Act
        var changes = analyzer.TakeChanges();

        // Assert
        Assert.Equal(new long[] { 1, 2 }, changes.Select(packet => packet.Id));
        Assert.Contains(2, changes[0].RelatedPacketIds);
    }

    [Fact]
    public void SetPacketLimit_BelowRetainedCount_EvictsOldestPacket()
    {
        // Arrange
        var analyzer = new PacketAnalyzer(10);
        analyzer.Feed(new(CreateWrapperFrame(GetRequestApdu), "TX", "Wrapper"));
        analyzer.Feed(new(CreateWrapperFrame(GetResponseApdu, true), "RX", "Wrapper"));

        // Act
        analyzer.SetPacketLimit(1);

        // Assert
        Assert.Equal(2, analyzer.FirstPacketId);
        Assert.Single(analyzer.Snapshot());
    }

    [Fact]
    public void Feed_MoreThanFiftyThousandPackets_KeepsCountingAndCapsRetainedPackets()
    {
        // Arrange
        var analyzer = new PacketAnalyzer(500);
        byte[] request = CreateWrapperFrame(GetRequestApdu);

        // Act
        for (int index = 0; index < 50_100; index++)
            analyzer.Feed(new(request, "TX", "Wrapper"));

        var changes = analyzer.TakeChanges();

        // Assert
        Assert.Equal(50_100, analyzer.TotalPackets);
        Assert.Equal(500, analyzer.Snapshot().Count);
        Assert.Equal(49_601, analyzer.FirstPacketId);
        Assert.Equal(500, changes.Count);
    }

    [Fact]
    public void AnalyzeText_PacketLimitExceeded_ReportsTotalAndKeepsMostRecentPackets()
    {
        // Arrange
        string line = "TX: " + Convert.ToHexString(GetRequestApdu);
        string text = string.Join("\n", Enumerable.Repeat(line, 7));

        // Act
        var packets = PacketAnalyzer.AnalyzeText(text, out int ignored, out long total, packetLimit: 3);

        // Assert
        Assert.Equal(0, ignored);
        Assert.Equal(7, total);
        Assert.Equal(new long[] { 5, 6, 7 }, packets.Select(packet => packet.Id));
    }

    [Fact]
    public void CapturesAfter_SessionFilter_ReturnsOrderedBatchesFromSelectedSession()
    {
        // Arrange
        var trace = new DiagnosticTrace();
        byte[] packet = CreateWrapperFrame(GetRequestApdu);
        trace.BeginConnection("Wrapper");
        trace.LogTcpBytes("TX", packet);
        trace.BeginConnection("Wrapper");
        for (int index = 0; index < 5; index++)
            trace.LogTcpBytes("TX", packet);

        // Act
        var first = trace.CapturesAfter(0, 2, 2);
        var second = trace.CapturesAfter(first[^1].Id, 2, 2);
        var third = trace.CapturesAfter(second[^1].Id, 2, 2);

        // Assert
        var captures = first.Concat(second).Concat(third).ToArray();
        Assert.Equal(new long[] { 2, 3, 4, 5, 6 }, captures.Select(capture => capture.Id));
        Assert.All(captures, capture => Assert.Equal(2, capture.SessionId));
    }

    [Fact]
    public void Feed_InspectorEvictsPackets_TraceRetainsFullHistory()
    {
        // Arrange
        var trace = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        trace.BeginConnection("Wrapper");
        byte[] packet = CreateWrapperFrame(GetRequestApdu);
        var analyzer = new PacketAnalyzer(2);

        // Act
        for (int i = 0; i < 4; i++)
        {
            trace.LogTcpBytes("TX", packet);
            analyzer.Feed(new(packet, "TX", "Wrapper"));
        }
        var captures = trace.CapturesAfter(0);

        // Assert
        Assert.Equal(4, captures.Count);
        Assert.Equal(2, trace.CapturesAfter(captures[1].Id).Count);
        Assert.Equal(4, trace.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, analyzer.Snapshot().Count);
    }
}
