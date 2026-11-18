using System.Buffers;
using System.Collections.Immutable;
using EMCL.Core.Values;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Protocol.Axdr;
using EMCLDesktop.Services;
using EMCLDesktop.Services.PacketInspection;
using Xunit;
using static EMCLDesktop.Tests.PacketTestData;

namespace EMCLDesktop.Tests;

public class PacketAnalyzerTests
{
    [Fact]
    public void AnalyzeText_ApduLogWithProse_CorrelatesResponseAndMapsFieldBytes()
    {
        // Arrange
        var text = $"arbitrary preamble\n12:04:01 TX APDU: {FormatHex(GetRequestApdu)}\nnoise, OBIS=1.0.32.7.0.255\n12:04:02 RX APDU: {FormatHex(GetResponseApdu)}";

        // Act
        var normal = Analyze(text);

        // Assert
        Assert.Equal(2, normal.Length);
        Assert.Equal("1.0.32.7.0.255", normal[1].Target?.Obis);
        Assert.Equal(2301, normal[1].DataValue?.AsInt64());
        Assert.Equal([normal[0].Id], normal[1].RelatedPacketIds);
        Assert.Equal(RequestObisBytes, FieldBytes(normal[0], Field(normal[0], "OBIS / logical name")));
        Assert.Equal(new byte[] { 8, 253 }, FieldBytes(normal[1], Field(normal[1], "Значение")));
        Assert.StartsWith("1", Field(normal[0], "Invoke ID").Value);
        Assert.Equal($"2301{Environment.NewLine}0x08FD", normal[1].ValueText);
    }

    [Fact]
    public void AnalyzeText_CompactHex_CorrelatesResponse()
    {
        // Arrange
        var text = $"TX: {Convert.ToHexString(GetRequestApdu)}\nRX: {Convert.ToHexString(GetResponseApdu)}";

        // Act
        var compact = Analyze(text);

        // Assert
        Assert.Equal(2, compact.Length);
        Assert.NotNull(compact[1].Target);
    }

    [Fact]
    public void AnalyzeText_CommaSeparatedHexWithPrefixes_DecodesRequest()
    {
        // Arrange
        var text = "TX: " + string.Join(", ", GetRequestApdu.Select(b => $"0x{b:X2}"));

        // Act
        var prefixed = Analyze(text);

        // Assert
        Assert.Single(prefixed);
        Assert.NotNull(prefixed[0].Target);
    }

    [Fact]
    public void AnalyzeText_RequestAndResponseOnSameLine_CorrelatesResponse()
    {
        // Arrange
        var text = $"TX: {FormatHex(GetRequestApdu)} RX: {FormatHex(GetResponseApdu)}";

        // Act
        var sameLine = Analyze(text);

        // Assert
        Assert.Equal(2, sameLine.Length);
        Assert.NotNull(sameLine[1].Target);
    }

    [Fact]
    public void AnalyzeText_ApdusWithoutDirection_InfersDirectionAndCorrelatesResponse()
    {
        // Arrange
        var text = $"{FormatHex(GetRequestApdu)}\n{FormatHex(GetResponseApdu)}";

        // Act
        var unknownDirection = Analyze(text);

        // Assert
        Assert.Equal("TX", unknownDirection[0].Direction);
        Assert.Equal("RX", unknownDirection[1].Direction);
        Assert.NotNull(unknownDirection[1].Target);
    }

    [Fact]
    public void AnalyzeText_ApduSplitAcrossLines_ReassemblesRequest()
    {
        // Arrange
        var text = $"{FormatHex(GetRequestApdu[..6])}\n{FormatHex(GetRequestApdu[6..])}\n{FormatHex(GetResponseApdu)}";

        // Act
        var splitApdu = Analyze(text);

        // Assert
        Assert.Equal(2, splitApdu.Length);
        Assert.Equal(GetRequestApdu, splitApdu[0].Bytes);
        Assert.NotNull(splitApdu[1].Target);
    }

    [Fact]
    public void AnalyzeText_HdlcWithUnrelatedHex_ExtractsFramesAndInfersDirection()
    {
        // Arrange
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        byte[] rxFrame = CreateHdlcFrame([0xE6, 0xE7, 0, .. GetResponseApdu], true);

        // Act
        var noisy = PacketAnalyzer.AnalyzeText(
            $"2026-11-11 19:42:12.123 +03:00 counters: DE AD BE EF CAFE 1234\n" +
            $"RX packet: FE ED {FormatHex(txFrame)} counter 99 TX packet: {Convert.ToHexString(rxFrame)} | DE AD BE EF |\n" +
            "duration: 30 ms, checksum DEADBEEF", out int ignored);

        // Assert
        Assert.Equal(2, ignored);
        Assert.Equal(2, noisy.Count);
        Assert.Equal(txFrame, noisy[0].Bytes);
        Assert.Equal(rxFrame, noisy[1].Bytes);
        Assert.Equal("TX", noisy[0].Direction);
        Assert.Equal("RX", noisy[1].Direction);
        Assert.NotNull(noisy[1].Target);
    }

    [Fact]
    public void AnalyzeText_HdlcLinkSetupWithoutLlc_InfersDirectionFromControl()
    {
        // Arrange
        byte[] frames =
        [
            .. CreateHdlcFrame([], control: 0x93, oneByteServer: true),
            .. CreateHdlcFrame([], rx: true, control: 0x73, oneByteServer: true),
            .. CreateHdlcFrame([], control: 0x11, oneByteServer: true)
        ];

        // Act
        var linkSetup = Analyze(FormatHex(frames));

        // Assert
        Assert.Equal(["TX", "RX", "?"], linkSetup.Select(p => p.Direction));
    }

    [Theory]
    [InlineData(TraceDisplayFormat.VerbalWithHex)]
    [InlineData(TraceDisplayFormat.HexOnly)]
    public void AnalyzeText_ExportedTrace_ReassemblesFramesAndCorrelatesResponse(TraceDisplayFormat format)
    {
        // Arrange
        var sourceTrace = CreateFragmentedTrace();
        sourceTrace.DisplayFormat = format;
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);

        // Act
        var parsed = Analyze(sourceTrace.Snapshot());

        // Assert
        Assert.True(parsed.Length == 2 && parsed[1].Target is not null && parsed.All(p => !p.HasError), "own export format/fragment reconstruction: " + format);
        Assert.Equal(txFrame, parsed[0].Bytes);
    }

    [Fact]
    public void CapturesAfter_FragmentedTrace_PreservesFramingSessionAndCursor()
    {
        // Arrange
        var sourceTrace = CreateFragmentedTrace();

        // Act
        var captures = sourceTrace.CapturesAfter(0);

        // Assert
        Assert.Equal(3, captures.Count);
        Assert.True(captures.All(c => c.Framing == "HDLC" && c.SessionId == 1), "typed capture framing/session metadata");
        Assert.Single(sourceTrace.CapturesAfter(captures[1].Id));
    }

    [Fact]
    public void DisplayFormat_ChangedToVerbal_PreservesTypedCaptures()
    {
        // Arrange
        var sourceTrace = CreateFragmentedTrace();
        sourceTrace.DisplayFormat = TraceDisplayFormat.HexOnly;

        // Act
        sourceTrace.DisplayFormat = TraceDisplayFormat.Verbal;

        // Assert
        Assert.Equal(3, sourceTrace.CapturesAfter(0).Count);
    }

    [Fact]
    public void Feed_SingleByteHdlcFragments_ReconstructsRequestAndResponse()
    {
        // Arrange
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        byte[] rxFrame = CreateHdlcFrame([0xE6, 0xE7, 0, .. GetResponseApdu], true);

        // Act
        var fragmented = new PacketAnalyzer();
        foreach (byte b in txFrame) fragmented.Feed(new([b], "TX", "HDLC"));
        foreach (byte b in rxFrame) fragmented.Feed(new([b], "RX", "HDLC"));

        // Assert
        Assert.Equal(2, fragmented.Snapshot().Count);
        Assert.NotNull(fragmented.Snapshot()[1].Target);
    }

    [Fact]
    public void AnalyzeText_JoinedHdlcFrames_DecodesBothFrames()
    {
        // Arrange
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        var text = "TX: " + FormatHex([.. txFrame, .. txFrame]);

        // Act
        var joined = Analyze(text);

        // Assert
        Assert.Equal(2, joined.Length);
    }

    [Fact]
    public void AnalyzeText_HdlcFramesSharingFlag_DecodesBothFrames()
    {
        // Arrange
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        var text = "TX: " + FormatHex([.. txFrame, .. txFrame.Skip(1)]);

        // Act
        var sharedFlag = Analyze(text);

        // Assert
        Assert.Equal(2, sharedFlag.Length);
        Assert.True(sharedFlag.All(p => !p.HasError), "shared HDLC closing/opening flag");
    }

    [Fact]
    public void AnalyzeText_RequestWithBadFcs_ReportsErrorAndDoesNotCorrelateResponse()
    {
        // Arrange
        var corrupted = CreateCorruptedFrame();
        byte[] rxFrame = CreateHdlcFrame([0xE6, 0xE7, 0, .. GetResponseApdu], true);
        var text = $"TX: {FormatHex(corrupted)}\nRX: {FormatHex(rxFrame)}";

        // Act
        var crcError = Analyze(text);

        // Assert
        Assert.True(crcError[0].HasError, "bad FCS entered request context");
        Assert.Null(crcError[1].Target);
        Assert.Contains("не совпадает", Field(crcError[0], "FCS").Value);
        Assert.True(Field(crcError[0], "FCS").IsError);
        Assert.False(Field(crcError[0], "HCS").IsError);
    }

    [Fact]
    public void AnalyzeText_TruncatedHdlcFrame_ReportsIncompletePacket()
    {
        // Arrange
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        var text = "TX: " + FormatHex(txFrame[..^4]);

        // Act
        var truncated = Analyze(text);

        // Assert
        Assert.Single(truncated);
        Assert.True(truncated[0].IsIncomplete, "truncated HDLC omitted");
        Assert.True(truncated[0].HasError, "truncated HDLC omitted");
        Assert.DoesNotContain(Flatten(truncated[0].Fields), field => field.IsError);
    }

    [Fact]
    public void AnalyzeText_WrongHdlcClosingFlag_MarksTheReceivedByte()
    {
        byte[] frame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        frame[^1] = 0;

        var packet = Assert.Single(Analyze($"TX: {FormatHex(frame)}"));

        var invalid = Assert.Single(Flatten(packet.Fields), field => field.IsError);
        Assert.Equal("Конечный флаг", invalid.Name);
        Assert.Equal(new InspectionRange(frame.Length - 1, 1), Assert.Single(invalid.Ranges));
    }

    [Fact]
    public void AnalyzeText_InvalidLlcQuality_MarksOnlyTheUnexpectedByte()
    {
        byte[] frame = CreateHdlcFrame([0xE6, 0xE6, 0x01, .. GetRequestApdu]);

        var packet = Assert.Single(Analyze($"TX: {FormatHex(frame)}"));

        Assert.True(packet.HasError);
        var invalid = Assert.Single(Flatten(packet.Fields), field => field.IsError);
        Assert.Equal("Quality", invalid.Name);
        Assert.Equal(new byte[] { 0x01 }, FieldBytes(packet, invalid));
    }

    [Fact]
    public void AnalyzeText_WrapperWithTrailingApduBytes_MarksOnlyTheTrailingBytes()
    {
        byte[] frame = CreateWrapperFrame([.. GetRequestApdu, 0xAA, 0xBB]);

        var packet = Assert.Single(Analyze($"TX: {FormatHex(frame)}"));

        Assert.True(packet.HasError);
        var invalid = Assert.Single(Flatten(packet.Fields), field => field.IsError);
        Assert.Equal(new InspectionRange(frame.Length - 2, 2), Assert.Single(invalid.Ranges));
    }

    [Fact]
    public void AnalyzeText_CorruptHdlcLength_RecoversFollowingFrame()
    {
        // Arrange
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        var invalidLength = txFrame.ToArray();
        invalidLength[1] = 0xA7;
        invalidLength[2] = 0xFF;
        var text = "TX: " + FormatHex([.. invalidLength, .. txFrame]);

        // Act
        var recovered = Analyze(text);

        // Assert
        Assert.Equal(2, recovered.Length);
        Assert.True(recovered[0].HasError, "corrupt length swallowed the next valid HDLC frame");
        Assert.Equal("Frame format", Assert.Single(Flatten(recovered[0].Fields), field => field.IsError).Name);
        Assert.NotNull(recovered[1].Target);
    }

    [Fact]
    public void AnalyzeText_WrapperExchange_CorrelatesResponseAndMapsObisBytes()
    {
        // Arrange
        var text = $"TX: {FormatHex(CreateWrapperFrame(GetRequestApdu))}\nRX: {FormatHex(CreateWrapperFrame(GetResponseApdu, true))}";

        // Act
        var wrapped = Analyze(text);

        // Assert
        Assert.Equal(2, wrapped.Length);
        Assert.NotNull(wrapped[1].Target);
        Assert.Equal(RequestObisBytes, FieldBytes(wrapped[0], Field(wrapped[0], "OBIS / logical name")));
    }

    [Fact]
    public void AnalyzeText_HdlcAndWrapperInSameLog_DecodesFramesInOrder()
    {
        // Arrange
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        byte[] txWrapper = CreateWrapperFrame(GetRequestApdu);
        byte[] rxWrapper = CreateWrapperFrame(GetResponseApdu, true);
        var text = $"19:42:12.123 payload: {FormatHex(txFrame)} note: {FormatHex([.. txWrapper, .. rxWrapper])} tail AB CD";

        // Act
        var mixed = Analyze(text);

        // Assert
        Assert.Equal(3, mixed.Length);
        Assert.Equal("TX", mixed[1].Direction);
        Assert.Equal("RX", mixed[2].Direction);
        Assert.Equal(txWrapper, mixed[1].Bytes);
        Assert.Equal(rxWrapper, mixed[2].Bytes);
    }

    [Fact]
    public void AnalyzeText_FragmentedWrapperWithColonSeparatedHex_ReassemblesExchange()
    {
        // Arrange
        byte[] txWrapper = CreateWrapperFrame(GetRequestApdu);
        byte[] rxWrapper = CreateWrapperFrame(GetResponseApdu, true);

        // Act
        var splitWrapper = Analyze($"00000000: {FormatHex(txWrapper[..6])}\n" +
            $"unrelated text\n00000006: {FormatHex(txWrapper[6..])}\n" +
            $"TX: {FormatHex(rxWrapper).Replace(' ', ':')}");

        // Assert
        Assert.Equal(2, splitWrapper.Length);
        Assert.Equal(txWrapper, splitWrapper[0].Bytes);
        Assert.Equal(rxWrapper, splitWrapper[1].Bytes);
        Assert.Equal("RX", splitWrapper[1].Direction);
        Assert.NotNull(splitWrapper[1].Target);
    }

    [Fact]
    public void AnalyzeText_CorruptWrapperLength_RecoversFollowingFrame()
    {
        // Arrange
        byte[] txWrapper = CreateWrapperFrame(GetRequestApdu);
        var badWrapper = txWrapper.ToArray();
        badWrapper[6] = 0xFF;
        var text = $"payload: {FormatHex(badWrapper)}\nnext packet: {FormatHex(txWrapper)}";

        // Act
        var wrapperRecovered = Analyze(text);

        // Assert
        Assert.Equal(2, wrapperRecovered.Length);
        Assert.True(wrapperRecovered[0].HasError, "corrupt Wrapper length swallowed the next valid frame");
        Assert.False(wrapperRecovered[1].HasError, "corrupt Wrapper length swallowed the next valid frame");
        Assert.Equal(txWrapper, wrapperRecovered[1].Bytes);
    }

    [Fact]
    public void AnalyzeText_SegmentedHdlc_ReassemblesRequestAndMapsDisjointFields()
    {
        // Arrange
        byte[] llc = [0xE6, 0xE6, 0, .. GetRequestApdu];
        byte[] segment1 = CreateHdlcFrame(llc[..11], segmented: true);
        byte[] segment2 = CreateHdlcFrame(llc[11..], sequence: 1);
        byte[] rxFrame = CreateHdlcFrame([0xE6, 0xE7, 0, .. GetResponseApdu], true);
        var text = $"TX: {FormatHex(segment1)}\nTX: {FormatHex(segment2)}\nRX: {FormatHex(rxFrame)}";

        // Act
        var segmented = Analyze(text);

        // Assert
        Assert.Equal(4, segmented.Length);
        Assert.NotNull(segmented[^1].Target);
        var assembledRequest = segmented.Single(p => p.Framing == "Собранный HDLC APDU");
        var assembledObis = Field(assembledRequest, "OBIS / logical name");
        Assert.Equal(2, assembledObis.Ranges.Count);
        Assert.Equal(RequestObisBytes, FieldBytes(assembledRequest, assembledObis));
        Assert.Contains(assembledRequest.Id, segmented[0].RelatedPacketIds);
        Assert.False(segmented[0].IsIncomplete, "segment/assembled navigation");
    }

    [Fact]
    public void AnalyzeText_MissingFinalHdlcSegment_ReportsIncompletePacket()
    {
        // Arrange
        byte[] llc = [0xE6, 0xE6, 0, .. GetRequestApdu];
        byte[] segment1 = CreateHdlcFrame(llc[..11], segmented: true);
        var text = "TX: " + FormatHex(segment1);

        // Act
        var missingSegment = Analyze(text);

        // Assert
        Assert.True(missingSegment[0].IsIncomplete, "missing final HDLC segment");
        Assert.True(missingSegment[0].HasError, "missing final HDLC segment");
    }

    [Fact]
    public void AnalyzeText_OutOfOrderHdlcSegments_ReportsErrors()
    {
        // Arrange
        byte[] llc = [0xE6, 0xE6, 0, .. GetRequestApdu];
        byte[] segment1 = CreateHdlcFrame(llc[..11], segmented: true);
        var text = $"TX: {FormatHex(segment1)}\nTX: {FormatHex(CreateHdlcFrame(llc[11..], sequence: 3))}";

        // Act
        var badSequence = Analyze(text);

        // Assert
        Assert.True(badSequence.All(p => p.HasError), "out-of-order HDLC sequence accepted");
    }

    [Fact]
    public void AnalyzeText_NestedDlmsValue_DecodesStructureAndMapsOctetBytes()
    {
        // Arrange
        var valueBytes = EncodeNestedValue();
        var text = $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex([0xC4, 1, 0xC1, 0, .. valueBytes])}";

        // Act
        var nestedResult = Analyze(text);

        // Assert
        Assert.Equal(DlmsType.Structure, nestedResult[1].DataValue?.Type);
        Assert.False(nestedResult[1].HasError, "nested data decoded");
        var octets = Flatten(nestedResult[1].Fields).Single(f => f.Name == "Значение" && f.Value.Contains("OBIS-кандидат"));
        Assert.Equal(RequestObisBytes, FieldBytes(nestedResult[1], octets));
    }

    [Fact]
    public void AnalyzeText_GetBlockExchange_ReassemblesValueAndMapsDisjointFields()
    {
        // Arrange
        var valueBytes = EncodeNestedValue();
        var text = $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(Block(valueBytes[..^3], 1, false))}\nTX: C0 02 C1 00 00 00 01\nRX: {FormatHex(Block(valueBytes[^3..], 2, true))}";

        // Act
        var blockResult = Analyze(text);

        // Assert
        var assembledValue = blockResult.Single(p => p.Kind == "Собранное DLMS-значение");
        Assert.Equal("1.0.32.7.0.255", assembledValue.Target?.Obis);
        Assert.Equal(DlmsType.Structure, assembledValue.DataValue?.Type);
        var splitOctets = Flatten(assembledValue.Fields).Single(f => f.Name == "Значение" && f.Value.Contains("OBIS-кандидат"));
        Assert.Equal(2, splitOctets.Ranges.Count);
        Assert.Equal(RequestObisBytes, FieldBytes(assembledValue, splitOctets));
    }

    [Fact]
    public void AnalyzeText_MissingFirstGetBlock_ReportsErrorWithoutValue()
    {
        // Arrange
        var valueBytes = EncodeNestedValue();
        var text = "RX: " + FormatHex(Block(valueBytes, 2, true));

        // Act
        var noFirstBlock = Analyze(text);

        // Assert
        Assert.True(noFirstBlock[0].HasError, "missing first GET block accepted");
        Assert.True(noFirstBlock.All(p => p.DataValue is null), "missing first GET block accepted");
    }

    [Fact]
    public void AnalyzeText_RetransmittedGetBlock_AssemblesValueOnce()
    {
        // Arrange
        var valueBytes = EncodeNestedValue();
        var text = $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(Block(valueBytes[..^3], 1, false))}\nRX: {FormatHex(Block(valueBytes[..^3], 1, false))}\nRX: {FormatHex(Block(valueBytes[^3..], 2, true))}";

        // Act
        var repeatedBlock = Analyze(text);

        // Assert
        Assert.Equal(1, repeatedBlock.Count(p => p.Kind == "Собранное DLMS-значение" && !p.HasError));
    }

    [Fact]
    public void AnalyzeText_ConflictingInvokeId_ReportsAmbiguousTarget()
    {
        // Arrange
        byte[] other = GetRequestApdu.ToArray();
        other[7] = 31;
        var text = $"TX: {FormatHex(GetRequestApdu)}\nTX: {FormatHex(other)}\nRX: {FormatHex(GetResponseApdu)}";

        // Act
        var ambiguous = Analyze(text);

        // Assert
        Assert.Null(ambiguous[^1].Target);
        Assert.Contains("Неоднозначный", ambiguous[^1].Diagnostic);
    }

    [Fact]
    public void AnalyzeText_ResponseAfterConnectionRestart_DiscardsPreviousRequest()
    {
        // Arrange
        var text = $"TX: {FormatHex(GetRequestApdu)}\nНачало нового подключения (HDLC)\nRX: {FormatHex(GetResponseApdu)}";

        // Act
        var restarted = Analyze(text);

        // Assert
        Assert.Null(restarted[^1].Target);
    }

    [Fact]
    public void AnalyzeText_WrapperResponseFromDifferentPeer_DoesNotCorrelateRequest()
    {
        // Arrange
        var text = $"TX: {FormatHex(CreateWrapperFrame(GetRequestApdu))}\nRX: {FormatHex(new byte[] { 0, 1, 0, 2, 0, 48, 0, 7 }.Concat(GetResponseApdu).ToArray())}";

        // Act
        var wrongPeer = Analyze(text);

        // Assert
        Assert.Null(wrongPeer[^1].Target);
    }

    [Fact]
    public void AnalyzeText_MislabeledDirection_UsesApduDirection()
    {
        // Arrange
        var text = $"RX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(GetResponseApdu)}";

        // Act
        var wrongLabels = Analyze(text);

        // Assert
        Assert.Equal("TX", wrongLabels[0].Direction);
        Assert.Equal("RX", wrongLabels[1].Direction);
        Assert.NotNull(wrongLabels[1].Target);
    }

    [Fact]
    public void AnalyzeText_GetAccessDenied_PreservesTargetAndReportsErrorWithoutValue()
    {
        // Arrange
        var text = $"TX: {FormatHex(GetRequestApdu)}\nRX: C4 01 C1 01 03";

        // Act
        var denied = Analyze(text);

        // Assert
        Assert.True(denied[^1].HasError, "access error decoded as a value");
        Assert.NotNull(denied[^1].Target);
        Assert.Null(denied[^1].DataValue);
        Assert.Equal("Результат операции", Assert.Single(Flatten(denied[^1].Fields), field => field.IsError).Name);
    }

    [Fact]
    public void AnalyzeText_SetExchange_DecodesValueAndCorrelatesAttribute()
    {
        // Arrange
        var text = "TX: C1 01 C1 00 01 00 00 60 01 00 FF 02 00 09 03 41 42 43\nRX: C5 01 C1 00";

        // Act
        var setResult = Analyze(text);

        // Assert
        Assert.Equal(DlmsType.OctetString, setResult[0].DataValue?.Type);
        Assert.Equal((sbyte)2, setResult[1].Target?.Index);
    }

    [Fact]
    public void AnalyzeText_ActionExchange_CorrelatesMethodAndDecodesReturnValue()
    {
        // Arrange
        var text = "TX: C3 01 C1 00 0F 00 00 28 00 00 FF 01 01 09 02 01 02\nRX: C7 01 C1 00 01 00 11 01";

        // Act
        var action = Analyze(text);

        // Assert
        Assert.True(action[^1].Target?.IsMethod, "ACTION parameter/return data");
        Assert.Equal(1, action[^1].DataValue?.AsInt64());
        Assert.False(action[^1].HasError, "ACTION parameter/return data");
    }

    [Fact]
    public void AnalyzeText_EncryptedPayload_ReportsUnknownDirectionWithoutValue()
    {
        // Arrange
        var text = "RX: DB 05 01 02 03 04 05";

        // Act
        var secured = Analyze(text);

        // Assert
        Assert.Single(secured);
        Assert.Equal("?", secured[0].Direction);
        Assert.Null(secured[0].DataValue);
        Assert.Contains("зашифровано", secured[0].Diagnostic);
    }

    [Fact]
    public void AnalyzeText_PreviousScalerUnitExchange_FormatsNumericValueWithUnits()
    {
        // Arrange
        byte[] scalerRequest = GetRequestApdu.ToArray();
        scalerRequest[11] = 3;
        var text = $"TX: {FormatHex(scalerRequest)}\nRX: C4 01 C1 00 02 02 0F FF 16 23\nTX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(GetResponseApdu)}";

        // Act
        var scaled = Analyze(text);

        // Assert
        Assert.StartsWith("230.1 В", scaled[^1].ValueText);
        Assert.Equal(2301, scaled[^1].DataValue?.AsInt64());
    }

    [Fact]
    public void AnalyzeText_CompactArray_DecodesRows()
    {
        // Arrange
        var compactValue = new DlmsValue(DlmsType.CompactArray, new DlmsCompactArray(
            new(DlmsType.Structure, [CompactTypeDescription.Scalar(DlmsType.UInt16), CompactTypeDescription.Scalar(DlmsType.Int8)]),
            [DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 2301), DlmsValue.Number(DlmsType.Int8, -1)),
             DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 2302), DlmsValue.Number(DlmsType.Int8, -2))]));
        var compactWriter = new ArrayBufferWriter<byte>();
        new AxdrCodec().Encode(compactValue, compactWriter);
        var text = "RX: " + FormatHex([0xC4, 1, 0xC1, 0, .. compactWriter.WrittenSpan.ToArray()]);

        // Act
        var compactResult = Analyze(text);

        // Assert
        Assert.False(compactResult[0].HasError, "compact array decode/layout");
        Assert.Equal(DlmsType.CompactArray, compactResult[0].DataValue?.Type);
        Assert.Equal(2, Flatten(compactResult[0].Fields).Count(f => f.Name.StartsWith("Строка [")));
    }

    [Fact]
    public void AnalyzeText_LongFormAxdrLength_MapsCompleteOctetPayload()
    {
        // Arrange
        var longString = DlmsValue.Octets(Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        var longWriter = new ArrayBufferWriter<byte>();
        new AxdrCodec().Encode(longString, longWriter);
        var text = "RX: " + FormatHex([0xC4, 1, 0xC1, 0, .. longWriter.WrittenSpan.ToArray()]);

        // Act
        var longResult = Analyze(text);

        // Assert
        Assert.Equal(300, Field(longResult[0], "Значение").ByteCount);
        Assert.False(longResult[0].HasError, "long-form A-XDR length offsets");
    }

    [Fact]
    public void AnalyzeText_Aarq_DecodesUserInformation()
    {
        // Arrange
        var aarqBytes = EncodeAarq();
        var text = "TX: " + FormatHex(aarqBytes);

        // Act
        var aarq = Analyze(text);

        // Assert
        Assert.Single(aarq);
        Assert.Equal("AARQ", aarq[0].Kind);
        Assert.False(aarq[0].HasError, "ACSE envelope");
        Assert.True(Flatten(aarq[0].Fields).Any(f => f.Name == "DLMS" && f.Value == "Initiate request"), "nested user-information APDU mapping");
    }

    [Fact]
    public void AnalyzeText_GetBlocksAcrossAssociationReset_DiscardsPartialValue()
    {
        // Arrange
        var valueBytes = EncodeNestedValue();
        var aarqBytes = EncodeAarq();
        var text = $"RX: {FormatHex(Block(valueBytes[..^3], 1, false))}\nTX: {FormatHex(aarqBytes)}\nRX: {FormatHex(Block(valueBytes[^3..], 2, true))}";

        // Act
        var resetBlock = Analyze(text);

        // Assert
        Assert.True(resetBlock.All(p => p.Kind != "Собранное DLMS-значение"), "orphan GET blocks crossed an association reset");
    }

    [Fact]
    public void Feed_ResponseInSameDirectionAsRequest_DoesNotCorrelate()
    {
        // Arrange
        var sameDirection = new PacketAnalyzer();
        sameDirection.Feed(new(GetRequestApdu, "TX", "APDU"));

        // Act
        sameDirection.Feed(new(GetResponseApdu, "TX", "APDU"));

        // Assert
        Assert.Null(sameDirection.Snapshot()[^1].Target);
    }

    [Fact]
    public void AnalyzeText_DeterministicMalformedInput_DoesNotInventTargets()
    {
        // Arrange
        var random = new Random(7241);
        var inputs = Enumerable.Range(0, 500).Select(index => CreateMalformedInput(random, index)).ToArray();

        // Act
        var packets = inputs.SelectMany(Analyze).ToArray();

        // Assert
        Assert.All(packets, packet => Assert.Null(packet.Target));
    }

    [Fact]
    public void AnalyzeText_ParsedAndReassembledPackets_KeepAllFieldRangesWithinPacketBytes()
    {
        // Arrange
        var valueBytes = EncodeNestedValue();
        var aarqBytes = EncodeAarq();
        byte[] llc = [0xE6, 0xE6, 0, .. GetRequestApdu];
        byte[] segment1 = CreateHdlcFrame(llc[..11], segmented: true);
        byte[] segment2 = CreateHdlcFrame(llc[11..], sequence: 1);
        byte[] rxFrame = CreateHdlcFrame([0xE6, 0xE7, 0, .. GetResponseApdu], true);
        string[] logs =
        [
            $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(GetResponseApdu)}",
            $"TX: {FormatHex(segment1)}\nTX: {FormatHex(segment2)}\nRX: {FormatHex(rxFrame)}",
            $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex(Block(valueBytes[..^3], 1, false))}\nTX: C0 02 C1 00 00 00 01\nRX: {FormatHex(Block(valueBytes[^3..], 2, true))}",
            $"TX: {FormatHex(GetRequestApdu)}\nRX: {FormatHex([0xC4, 1, 0xC1, 0, .. valueBytes])}",
            "TX: " + FormatHex(aarqBytes)
        ];

        // Act
        var packets = logs.SelectMany(Analyze).ToArray();

        // Assert
        Assert.All(packets, packet =>
            Assert.All(Flatten(packet.Fields).SelectMany(field => field.Ranges), range =>
            {
                Assert.InRange(range.Offset, 0, packet.Bytes.Length);
                Assert.InRange(range.Length, 0, packet.Bytes.Length - range.Offset);
            }));
    }

    private static IEnumerable<InspectionField> Flatten(IEnumerable<InspectionField> fields) => fields.SelectMany(f => new[] { f }.Concat(Flatten(f.Children)));
    private static InspectionField Field(InspectedPacket packet, string name) => Flatten(packet.Fields).First(f => f.Name == name);
    private static byte[] FieldBytes(InspectedPacket packet, InspectionField field) => field.Ranges.SelectMany(r => packet.Bytes.Skip(r.Offset).Take(r.Length)).ToArray();
    private static InspectedPacket[] Analyze(string text) => PacketAnalyzer.AnalyzeText(text, out _).ToArray();
    private static byte[] Block(byte[] data, uint number, bool last)
    {
        var writer = new ArrayBufferWriter<byte>();
        new ApduCodec().Encode(new GetBlockResponse(0xC1, last, number, data.ToImmutableArray()), writer);
        return writer.WrittenSpan.ToArray();
    }

    private static DiagnosticTrace CreateFragmentedTrace()
    {
        var sourceTrace = new DiagnosticTrace();
        sourceTrace.BeginConnection("HDLC");
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        byte[] rxFrame = CreateHdlcFrame([0xE6, 0xE7, 0, .. GetResponseApdu], true);
        sourceTrace.LogTcpBytes("TX", txFrame.AsSpan(0, 8));
        sourceTrace.LogTcpBytes("TX", txFrame.AsSpan(8));
        sourceTrace.LogTcpBytes("RX", rxFrame);
        return sourceTrace;
    }

    private static byte[] CreateCorruptedFrame()
    {
        byte[] txFrame = CreateHdlcFrame([0xE6, 0xE6, 0, .. GetRequestApdu]);
        txFrame[^3] ^= 1;
        return txFrame;
    }

    private static byte[] EncodeNestedValue()
    {
        var nested = DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 2301), DlmsValue.Array(
            DlmsValue.Number(DlmsType.Int8, -1), DlmsValue.Octets(RequestObisBytes)));
        var valueWriter = new ArrayBufferWriter<byte>();
        new AxdrCodec().Encode(nested, valueWriter);
        return valueWriter.WrittenSpan.ToArray();
    }

    private static byte[] EncodeAarq()
    {
        var acseWriter = new ArrayBufferWriter<byte>();
        new ApduCodec().Encode(AcseApdu.Aarq(Convert.FromHexString("01000000065F1F040000001DFFFF")), acseWriter);
        return acseWriter.WrittenSpan.ToArray();
    }

    private static string CreateMalformedInput(Random random, int index)
    {
        var junk = new byte[random.Next(1, 128)];
        random.NextBytes(junk);
        if (index % 3 == 0)
        {
            byte[] apduTags = [0x60, 0xC0, 0xC4, 0xDB];
            junk[0] = apduTags[index % apduTags.Length];
        }
        else if (index % 3 == 1 && junk.Length > 3)
        {
            junk[0] = 0x7E;
            junk[1] = 0xA0;
            junk[2] = (byte)(junk.Length - 2);
            junk[^1] = 0x7E;
        }

        return "RX: " + FormatHex(junk);
    }
}
