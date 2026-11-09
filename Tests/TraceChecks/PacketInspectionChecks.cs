using System.Buffers;
using System.Collections.Immutable;
using EMCL.Core.Values;
using EMCL.Framing.Hdlc.Frames;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Protocol.Axdr;
using EMCLDesktop.Services;
using EMCLDesktop.Services.PacketInspection;
using EMCLDesktop.ViewModels;

internal static class PacketInspectionChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Packet inspector: " + message);
            checks++;
        }
        byte[] request = Convert.FromHexString("C001C100030100200700FF0200");
        byte[] response = Convert.FromHexString("C401C1001208FD");
        byte[] obis = [1, 0, 32, 7, 0, 255];
        IEnumerable<InspectionField> Flatten(IEnumerable<InspectionField> fields) => fields.SelectMany(f => new[] { f }.Concat(Flatten(f.Children)));
        InspectionField Field(InspectedPacket packet, string name) => Flatten(packet.Fields).First(f => f.Name == name);
        byte[] FieldBytes(InspectedPacket packet, InspectionField field) => field.Ranges.SelectMany(r => packet.Bytes.Skip(r.Offset).Take(r.Length)).ToArray();
        InspectedPacket[] Analyze(string text) => PacketAnalyzer.AnalyzeText(text, out _).ToArray();
        string Hex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));
        byte[] Hdlc(byte[] information, bool rx = false, bool segmented = false, byte sequence = 0)
        {
            var writer = new ArrayBufferWriter<byte>();
            var client = HdlcAddress.Client(48);
            var server = HdlcAddress.Server(1, 17);
            new HdlcCodec().Encode(new(rx ? client : server, rx ? server : client,
                (byte)(sequence << 1), information.ToImmutableArray(), segmented), writer);
            return writer.WrittenSpan.ToArray();
        }
        byte[] Wrapper(byte[] apdu, bool rx = false) => [0, 1, 0, rx ? (byte)1 : (byte)48, 0, rx ? (byte)48 : (byte)1,
            (byte)(apdu.Length >> 8), (byte)apdu.Length, .. apdu];

        var normal = Analyze($"arbitrary preamble\n12:04:01 TX APDU: {Hex(request)}\nnoise, OBIS=1.0.32.7.0.255\n12:04:02 RX APDU: {Hex(response)}");
        Check(normal.Length == 2, "prose was extracted as bytes");
        Check(normal[1].Target?.Obis == "1.0.32.7.0.255" && normal[1].DataValue?.AsInt64() == 2301, "normal response correlation/value");
        Check(normal[1].RelatedPacketIds.SequenceEqual([normal[0].Id]), "response/request navigation");
        Check(FieldBytes(normal[0], Field(normal[0], "OBIS / logical name")).SequenceEqual(obis), "OBIS field offsets");
        Check(FieldBytes(normal[1], Field(normal[1], "Значение")).SequenceEqual(new byte[] { 8, 253 }), "numeric payload includes tag or wrong bytes");
        Check(Field(normal[0], "Invoke ID").Value.StartsWith("1"), "invoke-id bit field");

        var compact = Analyze($"TX: {Convert.ToHexString(request)}\nRX: {Convert.ToHexString(response)}");
        Check(compact.Length == 2 && compact[1].Target is not null, "compact HEX input");
        var prefixed = Analyze("TX: " + string.Join(", ", request.Select(b => $"0x{b:X2}")));
        Check(prefixed.Length == 1 && prefixed[0].Target is not null, "0x/comma HEX input");
        var sameLine = Analyze($"TX: {Hex(request)} RX: {Hex(response)}");
        Check(sameLine.Length == 2 && sameLine[1].Target is not null, "two directions on the same line");
        var unknownDirection = Analyze($"{Hex(request)}\n{Hex(response)}");
        Check(unknownDirection[1].Target is not null, "directionless raw APDU correlation");

        var sourceTrace = new DiagnosticTrace();
        sourceTrace.BeginConnection("HDLC");
        byte[] txFrame = Hdlc([0xE6, 0xE6, 0, .. request]);
        byte[] rxFrame = Hdlc([0xE6, 0xE7, 0, .. response], true);
        sourceTrace.LogTcpBytes("TX", txFrame.AsSpan(0, 8));
        sourceTrace.LogTcpBytes("TX", txFrame.AsSpan(8));
        sourceTrace.LogTcpBytes("RX", rxFrame);
        foreach (var format in new[] { TraceDisplayFormat.VerbalWithHex, TraceDisplayFormat.HexOnly })
        {
            sourceTrace.DisplayFormat = format;
            var parsed = Analyze(sourceTrace.Snapshot());
            Check(parsed.Length == 2 && parsed[1].Target is not null && parsed.All(p => !p.HasError), "own export format/fragment reconstruction: " + format);
            Check(parsed[0].Bytes.SequenceEqual(txFrame), "transport fragmentation changed wire bytes");
        }
        var captures = sourceTrace.CapturesAfter(0);
        Check(captures.Count == 3 && captures.All(c => c.Framing == "HDLC" && c.SessionId == 1), "typed capture framing/session metadata");
        Check(sourceTrace.CapturesAfter(captures[1].Id).Count == 1, "capture cursor");
        sourceTrace.DisplayFormat = TraceDisplayFormat.Verbal;
        Check(sourceTrace.CapturesAfter(0).Count == 3, "display format affected capture data");

        var fragmented = new PacketAnalyzer();
        foreach (byte b in txFrame) fragmented.Feed(new([b], "TX", "HDLC"));
        foreach (byte b in rxFrame) fragmented.Feed(new([b], "RX", "HDLC"));
        Check(fragmented.Snapshot().Count == 2 && fragmented.Snapshot()[1].Target is not null, "single-byte transport reads");
        var joined = Analyze("TX: " + Hex([.. txFrame, .. txFrame]));
        Check(joined.Length == 2, "multiple frames in one chunk");
        var sharedFlag = Analyze("TX: " + Hex([.. txFrame, .. txFrame.Skip(1)]));
        Check(sharedFlag.Length == 2 && sharedFlag.All(p => !p.HasError), "shared HDLC closing/opening flag");
        var corrupted = txFrame.ToArray(); corrupted[^3] ^= 1;
        var crcError = Analyze($"TX: {Hex(corrupted)}\nRX: {Hex(rxFrame)}");
        Check(crcError[0].HasError && crcError[1].Target is null, "bad FCS entered request context");
        Check(Field(crcError[0], "FCS").Value.Contains("не совпадает"), "FCS field diagnostics");
        var truncated = Analyze("TX: " + Hex(txFrame[..^4]));
        Check(truncated.Length == 1 && truncated[0].IsIncomplete && truncated[0].HasError, "truncated HDLC omitted");
        var invalidLength = txFrame.ToArray(); invalidLength[1] = 0xA7; invalidLength[2] = 0xFF;
        var recovered = Analyze("TX: " + Hex([.. invalidLength, .. txFrame]));
        Check(recovered.Length == 2 && recovered[0].HasError && recovered[1].Target is not null, "corrupt length swallowed the next valid HDLC frame");

        var wrapped = Analyze($"TX: {Hex(Wrapper(request))}\nRX: {Hex(Wrapper(response, true))}");
        Check(wrapped.Length == 2 && wrapped[1].Target is not null, "Wrapper decode and correlation");
        Check(FieldBytes(wrapped[0], Field(wrapped[0], "OBIS / logical name")).SequenceEqual(obis), "Wrapper/APDU offset mapping");

        byte[] llc = [0xE6, 0xE6, 0, .. request];
        byte[] segment1 = Hdlc(llc[..11], segmented: true);
        byte[] segment2 = Hdlc(llc[11..], sequence: 1);
        var segmented = Analyze($"TX: {Hex(segment1)}\nTX: {Hex(segment2)}\nRX: {Hex(rxFrame)}");
        Check(segmented.Length == 4 && segmented[^1].Target is not null, "HDLC segmented APDU assembly");
        var assembledRequest = segmented.Single(p => p.Framing == "Собранный HDLC APDU");
        var assembledObis = Field(assembledRequest, "OBIS / logical name");
        Check(assembledObis.Ranges.Count == 2 && FieldBytes(assembledRequest, assembledObis).SequenceEqual(obis), "disjoint OBIS mapping across physical frame headers/checksums");
        Check(segmented[0].RelatedPacketIds.Contains(assembledRequest.Id) && !segmented[0].IsIncomplete, "segment/assembled navigation");
        var missingSegment = Analyze("TX: " + Hex(segment1));
        Check(missingSegment[0].IsIncomplete && missingSegment[0].HasError, "missing final HDLC segment");
        var badSequence = Analyze($"TX: {Hex(segment1)}\nTX: {Hex(Hdlc(llc[11..], sequence: 3))}");
        Check(badSequence.All(p => p.HasError), "out-of-order HDLC sequence accepted");

        var nested = DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 2301), DlmsValue.Array(
            DlmsValue.Number(DlmsType.Int8, -1), DlmsValue.Octets(obis)));
        var valueWriter = new ArrayBufferWriter<byte>(); new AxdrCodec().Encode(nested, valueWriter);
        byte[] valueBytes = valueWriter.WrittenSpan.ToArray();
        var nestedResult = Analyze($"TX: {Hex(request)}\nRX: {Hex([0xC4, 1, 0xC1, 0, .. valueBytes])}");
        Check(nestedResult[1].DataValue?.Type == DlmsType.Structure && !nestedResult[1].HasError, "nested data decoded");
        var octets = Flatten(nestedResult[1].Fields).Single(f => f.Name == "Значение" && f.Value.Contains("OBIS-кандидат"));
        Check(FieldBytes(nestedResult[1], octets).SequenceEqual(obis), "nested octet payload offsets");

        byte[] Block(byte[] data, uint number, bool last)
        {
            var writer = new ArrayBufferWriter<byte>();
            new ApduCodec().Encode(new GetBlockResponse(0xC1, last, number, data.ToImmutableArray()), writer);
            return writer.WrittenSpan.ToArray();
        }
        var blockResult = Analyze($"TX: {Hex(request)}\nRX: {Hex(Block(valueBytes[..^3], 1, false))}\nTX: C0 02 C1 00 00 00 01\nRX: {Hex(Block(valueBytes[^3..], 2, true))}");
        var assembledValue = blockResult.Single(p => p.Kind == "Собранное DLMS-значение");
        Check(assembledValue.Target?.Obis == "1.0.32.7.0.255" && assembledValue.DataValue?.Type == DlmsType.Structure, "GET block assembly/context");
        var splitOctets = Flatten(assembledValue.Fields).Single(f => f.Name == "Значение" && f.Value.Contains("OBIS-кандидат"));
        Check(splitOctets.Ranges.Count == 2 && FieldBytes(assembledValue, splitOctets).SequenceEqual(obis), "GET block field mapping includes envelope bytes");
        var noFirstBlock = Analyze("RX: " + Hex(Block(valueBytes, 2, true)));
        Check(noFirstBlock[0].HasError && noFirstBlock.All(p => p.DataValue is null), "missing first GET block accepted");
        var repeatedBlock = Analyze($"TX: {Hex(request)}\nRX: {Hex(Block(valueBytes[..^3], 1, false))}\nRX: {Hex(Block(valueBytes[..^3], 1, false))}\nRX: {Hex(Block(valueBytes[^3..], 2, true))}");
        Check(repeatedBlock.Count(p => p.Kind == "Собранное DLMS-значение" && !p.HasError) == 1, "retransmitted GET block corrupted assembly");

        byte[] other = request.ToArray(); other[7] = 31;
        var ambiguous = Analyze($"TX: {Hex(request)}\nTX: {Hex(other)}\nRX: {Hex(response)}");
        Check(ambiguous[^1].Target is null && ambiguous[^1].Diagnostic.Contains("Неоднозначный"), "conflicting invoke ID guessed OBIS");
        var restarted = Analyze($"TX: {Hex(request)}\nНачало нового подключения (HDLC)\nRX: {Hex(response)}");
        Check(restarted[^1].Target is null, "request leaked across connections");
        var wrongPeer = Analyze($"TX: {Hex(Wrapper(request))}\nRX: {Hex(new byte[] { 0, 1, 0, 2, 0, 48, 0, 7 }.Concat(response).ToArray())}");
        Check(wrongPeer[^1].Target is null, "request leaked across peer addresses");
        var sameDirection = Analyze($"TX: {Hex(request)}\nTX: {Hex(response)}");
        Check(sameDirection[^1].Target is null, "same-direction response matched");

        var denied = Analyze($"TX: {Hex(request)}\nRX: C4 01 C1 01 03");
        Check(denied[^1].HasError && denied[^1].Target is not null && denied[^1].DataValue is null, "access error decoded as a value");
        var setResult = Analyze("TX: C1 01 C1 00 01 00 00 60 01 00 FF 02 00 09 03 41 42 43\nRX: C5 01 C1 00");
        Check(setResult[0].DataValue?.Type == DlmsType.OctetString && setResult[1].Target?.Index == 2, "SET value/context");
        var action = Analyze("TX: C3 01 C1 00 0F 00 00 28 00 00 FF 01 01 09 02 01 02\nRX: C7 01 C1 00 01 00 11 01");
        Check(action[^1].Target?.IsMethod == true && action[^1].DataValue?.AsInt64() == 1 && !action[^1].HasError, "ACTION parameter/return data");
        var secured = Analyze("RX: DB 05 01 02 03 04 05");
        Check(secured.Length == 1 && secured[0].DataValue is null && secured[0].Diagnostic.Contains("зашифровано"), "ciphertext presented as values");

        byte[] scalerRequest = request.ToArray(); scalerRequest[11] = 3;
        var scaled = Analyze($"TX: {Hex(scalerRequest)}\nRX: C4 01 C1 00 02 02 0F FF 16 23\nTX: {Hex(request)}\nRX: {Hex(response)}");
        Check(scaled[^1].ValueText.StartsWith("230.1 В") && scaled[^1].DataValue?.AsInt64() == 2301, "scaler/unit from prior exchange");
        Check(normal[1].ValueText == $"2301{Environment.NewLine}0x08FD", "numeric preview lost DEC/HEX or invented units without scaler/unit");

        var compactValue = new DlmsValue(DlmsType.CompactArray, new DlmsCompactArray(
            new(DlmsType.Structure, [CompactTypeDescription.Scalar(DlmsType.UInt16), CompactTypeDescription.Scalar(DlmsType.Int8)]),
            [DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 2301), DlmsValue.Number(DlmsType.Int8, -1)),
             DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 2302), DlmsValue.Number(DlmsType.Int8, -2))]));
        var compactWriter = new ArrayBufferWriter<byte>(); new AxdrCodec().Encode(compactValue, compactWriter);
        var compactResult = Analyze("RX: " + Hex([0xC4, 1, 0xC1, 0, .. compactWriter.WrittenSpan.ToArray()]));
        Check(!compactResult[0].HasError && compactResult[0].DataValue?.Type == DlmsType.CompactArray, "compact array decode/layout");
        Check(Flatten(compactResult[0].Fields).Count(f => f.Name.StartsWith("Строка [")) == 2, "compact array row layout");

        var longString = DlmsValue.Octets(Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        var longWriter = new ArrayBufferWriter<byte>(); new AxdrCodec().Encode(longString, longWriter);
        var longResult = Analyze("RX: " + Hex([0xC4, 1, 0xC1, 0, .. longWriter.WrittenSpan.ToArray()]));
        Check(Field(longResult[0], "Значение").ByteCount == 300 && !longResult[0].HasError, "long-form A-XDR length offsets");

        var random = new Random(7241);
        for (int i = 0; i < 500; i++)
        {
            var junk = new byte[random.Next(1, 128)]; random.NextBytes(junk);
            if (i % 3 == 0) junk[0] = new byte[] { 0x60, 0xC0, 0xC4, 0xDB }[i % 4];
            if (i % 3 == 1 && junk.Length > 3) { junk[0] = 0x7E; junk[1] = 0xA0; junk[2] = (byte)(junk.Length - 2); junk[^1] = 0x7E; }
            var malformed = Analyze("RX: " + Hex(junk));
            Check(malformed.All(p => p.Target is null), "random/malformed bytes invented a target");
        }

        var acseWriter = new ArrayBufferWriter<byte>();
        new ApduCodec().Encode(AcseApdu.Aarq(Convert.FromHexString("01000000065F1F040000001DFFFF")), acseWriter);
        var aarq = Analyze("TX: " + Hex(acseWriter.WrittenSpan.ToArray()));
        Check(aarq.Length == 1 && aarq[0].Kind == "AARQ" && !aarq[0].HasError, "ACSE envelope");
        Check(Flatten(aarq[0].Fields).Any(f => f.Name == "DLMS" && f.Value == "Initiate request"), "nested user-information APDU mapping");
        var resetBlock = Analyze($"RX: {Hex(Block(valueBytes[..^3], 1, false))}\nTX: {Hex(acseWriter.WrittenSpan.ToArray())}\nRX: {Hex(Block(valueBytes[^3..], 2, true))}");
        Check(resetBlock.All(p => p.Kind != "Собранное DLMS-значение"), "orphan GET blocks crossed an association reset");

        // View model interactions use the same field models as the native tree/HEX view.
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace());
        inspector.SourceIndex = 1;
        inspector.InputText = $"TX: {Hex(request)}\nRX: {Hex(response)}";
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);
        Check(inspector.Packets.Count == 2 && inspector.SelectedPacket is not null, "text analysis VM");
        var vmObis = inspector.Fields.SelectMany(f => f.Flatten()).Single(f => f.Field.Name == "OBIS / logical name");
        inspector.HoverField(vmObis);
        Check(inspector.ByteRows.SelectMany(r => r.Bytes).Count(b => b.IsHighlighted) == 6, "field hover did not highlight all six bytes");
        var classByte = inspector.ByteRows.SelectMany(r => r.Bytes).Single(b => b.Offset == 4);
        inspector.HoverByte(classByte);
        Check(inspector.ActiveField?.Field.Name == "Class ID" && inspector.ActiveField.IsHighlighted, "byte hover did not highlight field/value");
        inspector.SelectByte(classByte); inspector.HoverByte(null);
        Check(inspector.SelectedField?.Field.Name == "Class ID" && inspector.FieldValue == "3", "click did not pin hovered byte field");
        inspector.SourceIndex = 0; inspector.SourceIndex = 1;
        Check(inspector.InputText.Contains("TX:") && inspector.SelectedField?.Field.Name == "Class ID", "source switch lost pasted log or selection");
        inspector.SelectedPacket = inspector.Packets[1];
        var responseFields = inspector.Fields.SelectMany(f => f.Flatten()).ToArray();
        Check(responseFields.Any(f => f.Field.Name == "OBIS из запроса" && f.Field.Value.Contains("1.0.32.7.0.255") && f.Field.Ranges.Count == 0),
            "response structure lost correlated OBIS or invented byte offsets for it");
        inspector.InputText = $"TX: {Hex(scalerRequest)}\nRX: C4 01 C1 00 02 02 0F FF 16 23\nTX: {Hex(request)}\nRX: {Hex(response)}";
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);
        inspector.SelectedPacket = inspector.Packets[^1];
        var scaledFields = inspector.Fields.SelectMany(f => f.Flatten()).ToArray();
        var interpreted = scaledFields.Single(f => f.Field.Name == "Значение с scaler/unit");
        Check(interpreted.Field.Value.StartsWith("230.1 В"), "scaled value missing from packet structure");
        Check(interpreted.Field.Ranges.SequenceEqual(scaledFields.Single(f => f.Field.Name == "Полученное значение").Field.Ranges),
            "interpreted value points outside its encoded value");
        inspector.InputText = "RX: " + Hex(corrupted);
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);
        Check(inspector.Fields.Any(f => f.Field.Name == "Диагностика" && f.Field.Value.Contains("HDLC")), "packet diagnostics missing from structure");

        // Every visible field range is within its original physical bytes, including nested/reassembled values.
        foreach (var packet in normal.Concat(segmented).Concat(blockResult).Concat(nestedResult).Concat(aarq))
            foreach (var field in Flatten(packet.Fields))
                Check(field.Ranges.All(r => r.Offset >= 0 && r.Length >= 0 && r.Offset + r.Length <= packet.Bytes.Length), "field range out of bounds");
        return checks;
    }
}
