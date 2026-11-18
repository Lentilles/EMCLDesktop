using System.Buffers;
using System.Collections.Immutable;
using EMCL.Framing.Hdlc.Frames;
using EMCLDesktop.Services.PacketInspection;
using Xunit;

namespace EMCLDesktop.Tests;

public sealed class IncompleteHdlcInspectionTests
{
    private static readonly byte[] Request = Convert.FromHexString("E6E600C001C100030100200700FF0200");
    private static readonly byte[] Response = Convert.FromHexString("E6E700C401C1001208FD");

    [Fact]
    public void Feed_MissingClosingFlag_ShowsFieldsAndCompletesSamePacketWhenFlagArrives()
    {
        var analyzer = new PacketAnalyzer();
        byte[] request = CreateFrame(Request);
        analyzer.Feed(new(request[..^1], "TX", "HDLC"));

        var pending = Assert.Single(analyzer.TakeChanges());
        Assert.True(pending.IsIncomplete);
        Assert.False(pending.HasError);
        Assert.Contains("ожидается конечный флаг 7E", pending.Diagnostic);
        Assert.Equal("OK", Field(pending, "FCS").Value);
        Assert.Equal("1.0.32.7.0.255", Field(pending, "OBIS / logical name").Value);
        Assert.DoesNotContain(Fields(pending.Fields), field => field.Name == "Конечный флаг");
        Assert.Null(pending.Target);

        analyzer.Feed(new(request[^1..], "TX", "HDLC"));
        var complete = Assert.Single(analyzer.TakeChanges());
        Assert.Equal(pending.Id, complete.Id);
        Assert.False(complete.IsIncomplete);
        Assert.Equal("7E", Field(complete, "Конечный флаг").Value);
        Assert.NotNull(complete.Target);

        analyzer.Feed(new(CreateFrame(Response, response: true), "RX", "HDLC"));
        var changes = analyzer.TakeChanges();
        var response = Assert.Single(changes, packet => packet.Direction == "RX");
        Assert.Equal(complete.Target, response.Target);
        Assert.Contains(complete.Id, response.RelatedPacketIds);
    }

    [Fact]
    public void AnalyzeText_TruncatedRequest_ShowsReceivedObisWithoutCorrelatingResponse()
    {
        byte[] request = CreateFrame(Request);
        byte[] response = CreateFrame(Response, response: true);
        var packets = PacketAnalyzer.AnalyzeText(
            $"TX: {Convert.ToHexString(request[..^4])}\nRX: {Convert.ToHexString(response)}", out _);

        Assert.Equal(2, packets.Count);
        var truncated = packets[0];
        Assert.True(truncated.IsIncomplete);
        Assert.True(truncated.HasError);
        Assert.Contains("неполные", truncated.Diagnostic);
        Assert.Equal("OK", Field(truncated, "HCS").Value);
        Assert.Equal(new InspectionRange(17, 6), Assert.Single(Field(truncated, "OBIS / logical name").Ranges));
        Assert.DoesNotContain(Fields(truncated.Fields), field => field.Name == "FCS");
        Assert.Null(packets[1].Target);
    }

    [Fact]
    public void AnalyzeText_FragmentBeforeValidFrame_KeepsDecodedFieldsAndRecoversNextFrame()
    {
        byte[] frame = CreateFrame(Request);
        byte[] log = [.. frame[..^4], .. frame];
        var packets = PacketAnalyzer.AnalyzeText($"TX: {Convert.ToHexString(log)}", out _);

        Assert.Equal(2, packets.Count);
        Assert.True(packets[0].IsIncomplete);
        Assert.True(packets[0].HasError);
        Assert.Equal("1.0.32.7.0.255", Field(packets[0], "OBIS / logical name").Value);
        Assert.False(packets[1].IsIncomplete);
        Assert.NotNull(packets[1].Target);
    }

    [Fact]
    public void Feed_EveryTruncatedPrefix_MapsOnlyReceivedBytes()
    {
        byte[] request = CreateFrame(Request);
        for (int end = 2; end < request.Length; end++)
        {
            var analyzer = new PacketAnalyzer();
            analyzer.Feed(new(request[..end], "TX", "HDLC"));

            var packet = Assert.Single(analyzer.Snapshot());
            Assert.True(packet.IsIncomplete);
            Assert.All(Fields(packet.Fields).SelectMany(field => field.Ranges), range =>
            {
                Assert.InRange(range.Offset, 0, end - 1);
                Assert.InRange(range.Length, 1, end - range.Offset);
            });
            if (end == request.Length - 2)
                Assert.Equal("Получен 1 из 2 байтов", Field(packet, "FCS").Value);
        }
    }

    [Fact]
    public void AnalyzeText_TruncatedAssociation_ShowsCompletedBerFields()
    {
        byte[] frame = CreateFrame(Convert.FromHexString("E6E700610AA203020100A303020100"), response: true);
        var packet = Assert.Single(PacketAnalyzer.AnalyzeText($"RX: {Convert.ToHexString(frame[..^6])}", out _));

        Assert.True(packet.IsIncomplete);
        Assert.Equal("0", Field(packet, "Integer").Value);
        Assert.Contains(Fields(packet.Fields), field => field.Name == "Result source diagnostic");
    }

    private static InspectionField Field(InspectedPacket packet, string name) =>
        Assert.Single(Fields(packet.Fields), field => field.Name == name);

    private static IEnumerable<InspectionField> Fields(IEnumerable<InspectionField> fields) =>
        fields.SelectMany(field => new[] { field }.Concat(Fields(field.Children)));

    private static byte[] CreateFrame(byte[] information, bool response = false)
    {
        var client = HdlcAddress.Client(48);
        var server = HdlcAddress.Server(1, 17);
        var writer = new ArrayBufferWriter<byte>();
        new HdlcCodec().Encode(new(response ? client : server, response ? server : client, 0, information.ToImmutableArray()), writer);
        return writer.WrittenSpan.ToArray();
    }
}
