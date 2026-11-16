using EMCLDesktop.Services;
using EMCLDesktop.Services.PacketInspection;
using Xunit;

namespace EMCLDesktop.Tests;

public class PacketWindowTests
{
    private static readonly byte[] Request = Convert.FromHexString("C001C100030100200700FF0200");
    private static readonly byte[] Response = Convert.FromHexString("C401C1001208FD");

    private static byte[] Wrapper(byte[] apdu, bool response = false) =>
        [0, 1, 0, response ? (byte)1 : (byte)48, 0, response ? (byte)48 : (byte)1,
            (byte)(apdu.Length >> 8), (byte)apdu.Length, .. apdu];

    [Fact]
    public void WindowEvictsOldPacketsButRetainsRequestContext()
    {
        var analyzer = new PacketAnalyzer(2);
        analyzer.Feed(new(Wrapper(Request), "TX", "Wrapper"));
        analyzer.Feed(new(Wrapper(Request), "TX", "Wrapper", SessionId: 2));
        analyzer.Feed(new(Wrapper(Request), "TX", "Wrapper", SessionId: 3));
        analyzer.Feed(new(Wrapper(Response, true), "RX", "Wrapper"));
        var retained = analyzer.Snapshot();
        Assert.Equal(4, analyzer.TotalPackets);
        Assert.Equal(new long[] { 3, 4 }, retained.Select(p => p.Id));
        Assert.Equal("1.0.32.7.0.255", retained[^1].Target?.Obis);
        Assert.Equal(new long[] { 1 }, retained[^1].RelatedPacketIds);
    }

    [Fact]
    public void ChangesIncludeUpdatesToExistingPacketsAndSkipEvictedOnes()
    {
        var analyzer = new PacketAnalyzer(10);
        analyzer.Feed(new(Wrapper(Request), "TX", "Wrapper"));
        Assert.Single(analyzer.TakeChanges());
        Assert.Empty(analyzer.TakeChanges());
        analyzer.Feed(new(Wrapper(Response, true), "RX", "Wrapper"));
        var changes = analyzer.TakeChanges();
        Assert.Equal(new long[] { 1, 2 }, changes.Select(p => p.Id));
        Assert.Contains(2, changes[0].RelatedPacketIds);
        analyzer.SetPacketLimit(1);
        Assert.Equal(2, analyzer.FirstPacketId);
        Assert.Single(analyzer.Snapshot());
    }

    [Fact]
    public void AnalyzerContinuesPastFormerFiftyThousandLimit()
    {
        var analyzer = new PacketAnalyzer(500);
        byte[] request = Wrapper(Request);
        for (int i = 0; i < 50_100; i++) analyzer.Feed(new(request, "TX", "Wrapper"));
        Assert.Equal(50_100, analyzer.TotalPackets);
        Assert.Equal(500, analyzer.Snapshot().Count);
        Assert.Equal(49_601, analyzer.FirstPacketId);
        Assert.Equal(500, analyzer.TakeChanges().Count);
    }

    [Fact]
    public void TextAnalysisReportsTotalAndKeepsRecentPackets()
    {
        string line = "TX: " + Convert.ToHexString(Request);
        string text = string.Join("\n", Enumerable.Repeat(line, 7));
        var packets = PacketAnalyzer.AnalyzeText(text, out int ignored, out long total, packetLimit: 3);
        Assert.Equal(0, ignored);
        Assert.Equal(7, total);
        Assert.Equal(new long[] { 5, 6, 7 }, packets.Select(packet => packet.Id));
    }

    [Fact]
    public void CaptureBatchesSkipOldSessionsAndKeepOrder()
    {
        var trace = new DiagnosticTrace();
        byte[] packet = Wrapper(Request);
        trace.BeginConnection("Wrapper");
        trace.LogTcpBytes("TX", packet);
        trace.BeginConnection("Wrapper");
        for (int i = 0; i < 5; i++) trace.LogTcpBytes("TX", packet);
        var first = trace.CapturesAfter(0, 2, 2);
        var second = trace.CapturesAfter(first[^1].Id, 2, 2);
        var third = trace.CapturesAfter(second[^1].Id, 2, 2);
        Assert.Equal(new long[] { 2, 3, 4, 5, 6 }, first.Concat(second).Concat(third).Select(capture => capture.Id));
        Assert.All(first.Concat(second).Concat(third), capture => Assert.Equal(2, capture.SessionId));
    }

    [Fact]
    public void TraceRetainsFullHistoryWhenInspectorDropsPackets()
    {
        var trace = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        trace.BeginConnection("Wrapper");
        byte[] packet = Wrapper(Request);
        var analyzer = new PacketAnalyzer(2);
        for (int i = 0; i < 4; i++)
        {
            trace.LogTcpBytes("TX", packet);
            analyzer.Feed(new(packet, "TX", "Wrapper"));
        }
        var captures = trace.CapturesAfter(0);
        Assert.Equal(4, captures.Count);
        Assert.Equal(2, trace.CapturesAfter(captures[1].Id).Count);
        Assert.Equal(4, trace.Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, analyzer.Snapshot().Count);
        trace.Clear();
        Assert.Empty(trace.CapturesAfter(0));
        trace.LogTcpBytes("TX", packet);
        Assert.Single(trace.CapturesAfter(captures[^1].Id));
    }
}
