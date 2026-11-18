using EMCLDesktop.Services;
using EMCLDesktop.Services.PacketInspection;
using EMCLDesktop.ViewModels;
using Xunit;
using static EMCLDesktop.Tests.PacketTestData;

namespace EMCLDesktop.Tests;

public class PacketInspectorViewModelTests
{
    [Fact]
    public async Task AnalyzeTextCommand_ApduLog_PopulatesPacketsAndSelectsFirst()
    {
        // Arrange
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace());
        inspector.SourceIndex = 1;
        inspector.InputText = $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(GetResponseApdu)}";

        // Act
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(2, inspector.Packets.Count);
        Assert.NotNull(inspector.SelectedPacket);
    }

    [Fact]
    public async Task AnalyzeTextCommand_SegmentedResponse_DrawsBranchesAtSourcesAndResult()
    {
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace()) { SourceIndex = 1 };
        byte[] response = [0xE6, 0xE7, 0, .. GetResponseApdu];
        inspector.InputText = string.Join('\n',
            $"TX: {FormatHex(CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu], control: 0x10))}",
            $"RX: {FormatHex(CreateHdlcFrame(response[..8], rx: true, segmented: true, control: 0x30))}",
            $"TX: {FormatHex(CreateHdlcFrame([], control: 0x31))}",
            $"RX: {FormatHex(CreateHdlcFrame(response[8..], rx: true, control: 0x32))}");

        await inspector.AnalyzeTextCommand.ExecuteAsync(null);

        var layout = inspector.AssemblyLayout;
        Assert.Equal(1, layout.LaneCount);
        Assert.Empty(layout.LinesAt(1));
        Assert.Equal(new PacketAssemblyLine(0, false, true, true), Assert.Single(layout.LinesAt(2)));
        Assert.Equal(new PacketAssemblyLine(0, true, true, false), Assert.Single(layout.LinesAt(3)));
        Assert.Equal(new PacketAssemblyLine(0, true, true, true), Assert.Single(layout.LinesAt(4)));
        Assert.Equal(new PacketAssemblyLine(0, true, false, true), Assert.Single(layout.LinesAt(5)));
        Assert.Equal(new long[] { 2, 4, 1 }, inspector.Packets[^1].Packet.RelatedPacketIds);
    }

    [Fact]
    public async Task SelectedField_SegmentedValue_ShowsNestedValuesWithoutExpandingThePacketTree()
    {
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace()) { SourceIndex = 1 };
        byte[] response = Convert.FromHexString("E6E700C401C10001010202090C07EA0B12030A00000000000006000003E8");
        inspector.InputText = $"RX: {FormatHex(CreateHdlcFrame(response[..15], rx: true, segmented: true, control: 0x30))}\n" +
                              $"RX: {FormatHex(CreateHdlcFrame(response[15..], rx: true, control: 0x32))}";
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);

        inspector.SelectedPacket = inspector.Packets[^1];

        Assert.Equal("Полученное значение", inspector.SelectedField?.Field.Name);
        Assert.False(inspector.SelectedField!.IsExpanded);
        Assert.Collection(inspector.FieldPreview,
            row => Assert.Equal(new("Полученное значение · Array (1 элемент)", 0), row),
            row => Assert.Equal(new("[0] · Structure (2 элемента)", 1), row),
            row => Assert.Equal(new("[0] · OctetString (12 байт)", 2), row),
            row => Assert.Equal(new("HEX: 07EA0B12030A000000000000", 3, IsFormat: true), row),
            row => Assert.StartsWith("DLMS date-time: 2026-11-18 10:00:00", row.Text),
            row => Assert.Equal(new("[1] · UInt32: 1000 · 0x000003E8", 2), row));
        Assert.Equal(2, inspector.SelectedField.Field.Ranges.Count);
    }

    [Fact]
    public async Task SelectedField_OctetString_ShowsValidEncodingsAndStaysSelectedDuringHover()
    {
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace()) { SourceIndex = 1 };
        inspector.InputText = "RX: C4 01 C1 00 09 03 41 42 43";
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);

        var fields = inspector.Fields.SelectMany(f => f.Flatten()).ToArray();
        var byteValue = fields.Single(f => f.Field.Name == "Значение");
        inspector.SelectedField = byteValue;
        var preview = inspector.FieldPreview;
        Assert.Equal(
            new[] { "Значение · OctetString (3 байт)", "HEX: 414243", "Windows-1251: ABC", "UTF-8: ABC", "ASCII: ABC" },
            preview.Select(row => row.Text));

        var length = fields.Single(f => f.Field.Name == "Длина");
        inspector.HoverField(length);
        Assert.Same(preview, inspector.FieldPreview);
        Assert.Contains(length.Field.Name, inspector.ByteHighlightText);

        inspector.SelectedField = length;
        Assert.Equal("Длина: 3", Assert.Single(inspector.FieldPreview).Text);
        inspector.SelectedPacket = null;
        Assert.Empty(inspector.FieldPreview);
    }

    [Fact]
    public async Task HoverField_ObisField_HighlightsAllSixBytes()
    {
        // Arrange
        using var inspector = await CreateInspectorAsync();
        var vmObis = inspector.Fields.SelectMany(f => f.Flatten()).Single(f => f.Field.Name == "OBIS / logical name");

        // Act
        inspector.HoverField(vmObis);

        // Assert
        Assert.Equal(6, inspector.ByteRows.SelectMany(r => r.Bytes).Count(b => b.IsHighlighted));
    }

    [Fact]
    public async Task HoverByte_ClassIdByte_HighlightsClassIdField()
    {
        // Arrange
        using var inspector = await CreateInspectorAsync();
        var classByte = inspector.ByteRows.SelectMany(r => r.Bytes).Single(b => b.Offset == 4);

        // Act
        inspector.HoverByte(classByte);

        // Assert
        Assert.Equal("Class ID", inspector.ActiveField?.Field.Name);
        Assert.NotNull(inspector.ActiveField);
        Assert.True(inspector.ActiveField.IsHighlighted, "byte hover did not highlight field/value");
    }

    [Fact]
    public async Task SelectByte_ClassIdByte_PinsFieldAfterPointerLeaves()
    {
        // Arrange
        using var inspector = await CreateInspectorAsync();
        var classByte = inspector.ByteRows.SelectMany(r => r.Bytes).Single(b => b.Offset == 4);
        inspector.HoverByte(classByte);

        // Act
        inspector.SelectByte(classByte);
        inspector.HoverByte(null);

        // Assert
        Assert.Equal("Class ID", inspector.SelectedField?.Field.Name);
        Assert.Equal("3", inspector.FieldValue);
    }

    [Fact]
    public async Task SourceIndex_ReturnToPastedLog_PreservesLogAndSelection()
    {
        // Arrange
        using var inspector = await CreateInspectorAsync();
        var classByte = inspector.ByteRows.SelectMany(r => r.Bytes).Single(b => b.Offset == 4);
        inspector.SelectByte(classByte);

        // Act
        inspector.SourceIndex = 0;
        inspector.SourceIndex = 1;

        // Assert
        Assert.Contains("TX:", inspector.InputText);
        Assert.Equal("Class ID", inspector.SelectedField?.Field.Name);
    }

    [Fact]
    public async Task SelectedPacket_CorrelatedResponse_ShowsRequestObisWithoutByteRange()
    {
        // Arrange
        using var inspector = await CreateInspectorAsync();

        // Act
        inspector.SelectedPacket = inspector.Packets[1];

        // Assert
        var responseFields = inspector.Fields.SelectMany(f => f.Flatten()).ToArray();
        Assert.True(responseFields.Any(f => f.Field.Name == "OBIS из запроса" && f.Field.Value.Contains("1.0.32.7.0.255") && f.Field.Ranges.Count == 0),
            "Response structure lost correlated OBIS or invented byte offsets for it");
    }

    [Fact]
    public async Task SelectedPacket_ScaledResponse_ShowsInterpretedValueAtEncodedRange()
    {
        // Arrange
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace());
        inspector.SourceIndex = 1;
        byte[] scalerRequest = GetRequestApdu.ToArray();
        scalerRequest[11] = 3;
        inspector.InputText = $"TX: {FormatHex(scalerRequest)}\nRX: C4 01 C1 00 02 02 0F FF 16 23\nTX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(GetResponseApdu)}";
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);

        // Act
        inspector.SelectedPacket = inspector.Packets[^1];

        // Assert
        var scaledFields = inspector.Fields.SelectMany(f => f.Flatten()).ToArray();
        var interpreted = scaledFields.Single(f => f.Field.Name == "Значение с scaler/unit");
        Assert.StartsWith("230.1 В", interpreted.Field.Value);
        Assert.Equal(scaledFields.Single(f => f.Field.Name == "Полученное значение").Field.Ranges, interpreted.Field.Ranges);
    }

    [Fact]
    public async Task AnalyzeTextCommand_CorruptHdlcFrame_HighlightsFcsIndependentlyOfActiveField()
    {
        // Arrange
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace());
        inspector.SourceIndex = 1;
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        txFrame[^3] ^= 1;
        inspector.InputText = "RX: " + FormatHex(txFrame);

        // Act
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);

        // Assert
        Assert.True(inspector.Fields.Any(f => f.Field.Name == "Диагностика" && f.Field.Value.Contains("HDLC")), "packet diagnostics missing from structure");
        var fields = inspector.Fields.SelectMany(field => field.Flatten()).ToArray();
        var checksum = Assert.Single(fields, field => field.Field.Name == "FCS");
        var control = Assert.Single(fields, field => field.Field.Name == "Control");
        var bytes = inspector.ByteRows.SelectMany(row => row.Bytes).ToArray();
        Assert.Equal(new[] { txFrame.Length - 3, txFrame.Length - 2 }, bytes.Where(value => value.IsError).Select(value => value.Offset));

        inspector.SelectedField = control;
        Assert.True(checksum.IsError);
        Assert.False(checksum.IsHighlighted);
        Assert.All(bytes.Where(value => value.IsError), value => Assert.False(value.IsHighlighted));
        Assert.Same(control, Assert.Single(bytes, value => value.IsHighlighted).Owner);

        inspector.HoverField(checksum);
        Assert.True(checksum.IsHighlighted);
        Assert.Equal(bytes.Where(value => value.IsError), bytes.Where(value => value.IsHighlighted));

        inspector.HoverField(null);
        Assert.False(checksum.IsHighlighted);
        Assert.All(bytes.Where(value => value.IsError), value => Assert.False(value.IsHighlighted));
    }

    private static async Task<PacketInspectorViewModel> CreateInspectorAsync()
    {
        var inspector = new PacketInspectorViewModel(new DiagnosticTrace());
        inspector.SourceIndex = 1;
        inspector.InputText = $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(GetResponseApdu)}";
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);
        return inspector;
    }

    private static string FormatHex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));
}
