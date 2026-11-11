using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using EMCL.Core.Codecs;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Framing.Hdlc.Frames;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Protocol.Axdr;

namespace EMCLDesktop.Services.PacketInspection;

/// <summary>Passive, incremental analysis. Never sends data or changes a meter session.</summary>
public sealed class PacketAnalyzer
{
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxPackets = 50_000;
    private readonly List<InspectedPacket> packets = [];
    private readonly Dictionary<string, StreamBuffer> streams = [];
    private readonly Dictionary<string, PendingRequest> requests = [];
    private readonly Dictionary<string, SegmentTransfer> segments = [];
    private readonly Dictionary<string, BlockTransfer> blocks = [];
    private readonly Dictionary<string, (sbyte Scaler, byte Unit)> scalers = [];
    private long nextId;

    private sealed class StreamBuffer
    {
        public byte[] Bytes = [];
        public List<SourcePart> Sources = [];
        public long PendingId;
    }
    private sealed record SourcePart(int Offset, int Length, InspectionInput Input);
    private sealed record PendingRequest(long PacketId, InspectionTarget Target, string Signature, bool Ambiguous = false);
    private sealed class SegmentTransfer
    {
        public List<(InspectedPacket Packet, MappedPayload Payload)> Parts = [];
        public int ExpectedSequence, ByteCount;
    }
    private sealed class BlockTransfer
    {
        public List<(InspectedPacket Packet, MappedPayload Payload)> Parts = [];
        public uint Expected = 1;
        public int ByteCount;
    }

    public static IReadOnlyList<InspectedPacket> AnalyzeText(string text, out int ignoredLines, CancellationToken cancellationToken = default)
    {
        var analyzer = new PacketAnalyzer();
        ignoredLines = 0;
        foreach (var (runs, isByteDump) in LogTextExtractor.ReadLines(text, cancellationToken))
        {
            bool found = false;
            foreach (var input in runs)
                found |= analyzer.Feed(input, fromText: true, isByteDump);
            if (!found) ignoredLines++;
        }
        analyzer.Finish();
        return analyzer.Snapshot();
    }

    public IReadOnlyList<InspectedPacket> Snapshot() => packets.ToArray();

    public void Feed(InspectionInput input) => Feed(input, fromText: false, isByteDump: false);

    private bool Feed(InspectionInput input, bool fromText, bool isByteDump)
    {
        if (input.Bytes.Length == 0) return false;
        string key = $"{input.SessionId}|{input.Direction}|{input.Framing}";
        if (!streams.TryGetValue(key, out var stream)) streams[key] = stream = new();
        if (fromText && stream.Bytes is [0x7E] && input.Bytes[0] != 0x7E && (input.Bytes[0] & 0xF0) != 0xA0)
            Consume(stream, 1);
        if (fromText && stream.Bytes.Length > 0 && !isByteDump && input.Bytes.Length < 4 &&
            input.Bytes[0] != 0x7E && !IsWrapperStart(input.Bytes))
            return false;
        if (fromText && stream.PendingId != 0 && stream.Bytes[0] is not (0 or 0x7E) &&
            IsValidFrameStart(input.Bytes, includeWrapper: true))
            FlushIncomplete(stream);
        // An explicit APDU label delimits a new message, unlike arbitrary transport fragments.
        if (input.ExplicitApdu && stream.Bytes.Length > 0) FlushIncomplete(stream);
        int oldLength = stream.Bytes.Length;
        if (oldLength + input.Bytes.Length > MaxMessageBytes + 65536)
        {
            FlushIncomplete(stream, "Превышен предел буфера пакета (1 МиБ).");
            oldLength = 0;
        }
        stream.Sources.Add(new(oldLength, input.Bytes.Length, input));
        stream.Bytes = [.. stream.Bytes, .. input.Bytes];
        int offset = 0;
        bool found = false, aligned = true;
        while (offset < stream.Bytes.Length)
        {
            var remaining = stream.Bytes.AsSpan(offset);
            if (remaining[0] == 0x7E && (remaining.Length == 1 || remaining[1] == 0x7E))
            {
                if (remaining.Length == 1) break; // May be the opening flag of the next frame.
                offset++;
                continue;
            }
            if (remaining[0] == 0x7E)
            {
                if (remaining.Length < 3 && (!fromText || (remaining[1] & 0xF0) == 0xA0)) break;
                if (fromText && (remaining[1] & 0xF0) != 0xA0)
                {
                    offset++;
                    continue;
                }
                int length = ((remaining[1] & 7) << 8) | remaining[2];
                if ((remaining[1] & 0xF0) == 0xA0 && length >= 7)
                {
                    found = aligned = true;
                    if (remaining.Length < length + 2 || remaining[length + 1] != 0x7E)
                    {
                        int recovered = FindValidFrameStart(stream.Bytes.AsMemory(offset), includeWrapper: fromText);
                        if (recovered > 0)
                        {
                            Put(MakePacket(stream, offset, recovered, "HDLC") with
                            {
                                Kind = "HDLC · повреждённая длина / обрыв", HasError = true,
                                Diagnostic = "Перед следующим корректным кадром обнаружены неполные или повреждённые данные.",
                                Fields = [InspectionField.At("Повреждённый фрагмент", "Граница восстановлена по следующему корректному кадру", 0, recovered)]
                            });
                            offset += recovered;
                            continue;
                        }
                        if (remaining.Length < length + 2) break;
                    }
                    var packet = MakePacket(stream, offset, length + 2, "HDLC");
                    ReadHdlc(packet, input.SessionId);
                    offset += length + 1; // Closing flag can also open the next frame.
                    continue;
                }
                if (fromText && !isByteDump)
                {
                    offset++;
                    aligned = false;
                    continue;
                }
                int next = remaining[1..].IndexOf((byte)0x7E);
                int recoveredStart = fromText ? FindValidFrameStart(stream.Bytes.AsMemory(offset), includeWrapper: true) : -1;
                int badLength = recoveredStart > 0 ? recoveredStart : next >= 0 ? next + 1 : remaining.Length;
                Put(MakePacket(stream, offset, badLength, "HDLC") with
                {
                    Kind = "HDLC · неверный заголовок", HasError = true,
                    Diagnostic = "Ожидался HDLC format type 3 с допустимой длиной.",
                    Fields = [InspectionField.At("Неразобранный HDLC", "Неверный заголовок", 0, badLength)]
                });
                found = true;
                offset += badLength;
                continue;
            }
            bool wrapperHint = input.Framing == "Wrapper";
            if (wrapperHint || IsWrapperStart(remaining) || remaining.Length == 1 && remaining[0] == 0 &&
                (!fromText || aligned && isByteDump && !found))
            {
                if (remaining.Length < 8 && (!fromText || aligned && isByteDump)) break;
                if (remaining.Length < 8) { offset++; aligned = false; continue; }
                int length = BinaryPrimitives.ReadUInt16BigEndian(remaining[6..]);
                if (IsWrapperStart(remaining) && (!fromText || length > 0 && (
                    aligned && isByteDump || remaining.Length > 8 && IsApduStart(remaining[8]))))
                {
                    if (remaining.Length < 8 + length)
                    {
                        int recovered = fromText ? FindValidFrameStart(stream.Bytes.AsMemory(offset), includeWrapper: true) : -1;
                        if (recovered < 0) break;
                        if (aligned && isByteDump || stream.PendingId != 0)
                        {
                            Put(MakePacket(stream, offset, recovered, "Wrapper") with
                            {
                                Kind = "Wrapper · повреждённая длина / обрыв", HasError = true, IsIncomplete = true,
                                Diagnostic = "Граница восстановлена по следующему корректному кадру.",
                                Fields = [InspectionField.At("Доступные байты", "Неполный пакет", 0, recovered)]
                            });
                            found = true;
                        }
                        offset += recovered;
                        aligned = true;
                        continue;
                    }
                    ReadWrapper(MakePacket(stream, offset, 8 + length, "Wrapper"), input.SessionId);
                    found = aligned = true;
                    offset += 8 + length;
                    continue;
                }
            }
            if (input.Framing is not ("HDLC" or "Wrapper") && (!fromText || aligned))
            {
                bool startsLlc = remaining[0] is 0xE6 or 0xFF;
                if (startsLlc && remaining.Length < 4 && !fromText) break;
                bool hasLlc = remaining.Length >= 4 && LlcDirection(remaining) != "?";
                if (hasLlc || IsApduStart(remaining[0]))
                {
                    int headerLength = hasLlc ? 3 : 0;
                    var decoded = new ApduCodec().Decode(new ReadOnlySequence<byte>(stream.Bytes.AsMemory(offset + headerLength)));
                    bool allowPartial = isByteDump || stream.PendingId != 0;
                    if (fromText && decoded.Status != DecodeStatus.Done && (!allowPartial || found))
                    {
                        offset++;
                        aligned = false;
                        continue;
                    }
                    if (decoded.Status == DecodeStatus.NeedMoreData) break;
                    int count = decoded.Status == DecodeStatus.Done ? checked((int)decoded.Consumed) + headerLength : remaining.Length;
                    var packet = MakePacket(stream, offset, count, hasLlc ? "LLC / APDU" : "APDU");
                    var payload = MappedPayload.Contiguous(packet.Bytes, 0, count);
                    string scope = Scope(input.SessionId, "APDU");
                    if (hasLlc) ReadLlc(packet, payload, scope, input.Direction, []);
                    else ReadApdu(packet, payload, scope, input.Direction, []);
                    found = true;
                    offset += count;
                    continue;
                }
            }
            if (fromText)
            {
                aligned = false;
                offset++;
                continue;
            }
            int noiseLength = 1;
            while (noiseLength < remaining.Length && remaining[noiseLength] != 0x7E &&
                   !(remaining[noiseLength] == 0 && noiseLength + 1 < remaining.Length && remaining[noiseLength + 1] == 1)) noiseLength++;
            Put(MakePacket(stream, offset, noiseLength, "?") with
            {
                Kind = "Нераспознанные байты", HasError = true,
                Diagnostic = "Не удалось определить границы HDLC, Wrapper или DLMS APDU.",
                Fields = [InspectionField.At("Исходные байты", "Неизвестный формат", 0, noiseLength)]
            });
            offset += noiseLength;
        }
        Consume(stream, offset);
        if (fromText && stream.Bytes.Length == 1 && stream.Bytes[0] == 0x7E)
            return found || isByteDump;
        if (stream.Bytes.Length > 1 || stream.Bytes.Length == 1 && stream.Bytes[0] != 0x7E)
        {
            var pending = MakePacket(stream, 0, stream.Bytes.Length, input.Framing);
            stream.PendingId = pending.Id;
            Put(pending with
            {
                Kind = "Неполный пакет", IsIncomplete = true,
                Diagnostic = "Не хватает байтов; ожидается продолжение.",
                Fields = [InspectionField.At("Доступные байты", "Неполный пакет", 0, pending.Bytes.Length)]
            });
            found = true;
        }
        return found;
    }

    public void Finish()
    {
        foreach (var stream in streams.Values) FlushIncomplete(stream);
        foreach (var transfer in segments.Values)
            foreach (var part in transfer.Parts) Annotate(part.Packet.Id, "Не хватает заключительного HDLC-сегмента.", true);
        foreach (var transfer in blocks.Values)
            foreach (var part in transfer.Parts) Annotate(part.Packet.Id, "Не хватает заключительного DLMS-блока.", true);
        foreach (var request in requests.Values)
            Annotate(request.PacketId, "Ответ в логе не найден.");
    }

    private void ReadHdlc(InspectedPacket packet, int session)
    {
        var bytes = packet.Bytes;
        var result = new HdlcCodec().Decode(new ReadOnlySequence<byte>(bytes));
        var fields = new List<InspectionField>
        {
            InspectionField.At("Начальный флаг", "7E", 0, 1),
            InspectionField.At("Frame format", $"длина {bytes.Length - 2} · segmented={((bytes[1] & 8) != 0)}", 1, 2,
                InspectionField.At("Segmentation bit", $"{((bytes[1] & 8) != 0)} · mask 0x08", 1, 1))
        };
        int position = 3;
        int destinationStart = position;
        var destination = ReadAddress(bytes, ref position);
        fields.Add(InspectionField.At("Destination", destination.Display, destinationStart, position - destinationStart));
        int sourceStart = position;
        var source = ReadAddress(bytes, ref position);
        fields.Add(InspectionField.At("Source", source.Display, sourceStart, position - sourceStart));
        if (position >= bytes.Length - 3)
        {
            Put(packet with { Kind = "HDLC · ошибка", Fields = fields, HasError = true, Diagnostic = "Неполный заголовок HDLC." });
            return;
        }
        byte control = bytes[position];
        string controlName = ControlName(control);
        fields.Add(InspectionField.At("Control", $"0x{control:X2} · {controlName}", position, 1));
        position++;
        int available = bytes.Length - position - 3;
        int infoStart = position;
        if (available > 2)
        {
            bool validHcs = ReadCrc(bytes, position) == HdlcChecksum.Compute(bytes.AsSpan(1, position - 1));
            fields.Add(InspectionField.At("HCS", validHcs ? "OK" : "CRC не совпадает", position, 2));
            infoStart = position + 2;
            fields.Add(InspectionField.At("Information", $"{available - 2} байт", infoStart, available - 2));
        }
        int fcs = bytes.Length - 3;
        bool validFcs = ReadCrc(bytes, fcs) == HdlcChecksum.Compute(bytes.AsSpan(1, fcs - 1));
        fields.Add(InspectionField.At("FCS", validFcs ? "OK" : "CRC не совпадает", fcs, 2));
        fields.Add(InspectionField.At("Конечный флаг", $"{bytes[^1]:X2}", bytes.Length - 1, 1));
        var hdlc = InspectionField.At("HDLC", controlName, 0, bytes.Length, fields.ToArray());
        packet = packet with { Kind = $"HDLC {controlName}", Fields = [hdlc] };
        if (result.Status != DecodeStatus.Done || result.Value is not { } frame)
        {
            ClearScope(Scope(session, $"HDLC:{Pair(source.Key, destination.Key)}"));
            Put(packet with { HasError = true, Diagnostic = $"HDLC: {result.Error ?? result.Status.ToString()}" });
            return;
        }
        string scope = Scope(session, $"HDLC:{Pair(source.Key, destination.Key)}");
        string segmentKey = $"{scope}|{source.Key}";
        segments.TryGetValue(segmentKey, out var transfer);
        if (packet.Direction == "?")
        {
            string direction = (control & 0xEF) switch
            {
                0x83 or 0x43 => "TX", // SNRM / DISC
                0x63 or 0x0F or 0x87 => "RX", // UA / DM / FRMR
                _ => "?"
            };
            if (frame.IsInformation || frame.IsUnnumberedInformation)
                direction = transfer is null ? LlcDirection(frame.Information.AsSpan()) : transfer.Parts[0].Packet.Direction;
            if (direction == "?")
            {
                if (frame.Source.Length == 1 && frame.Destination.Length > 1) direction = "TX";
                else if (frame.Destination.Length == 1 && frame.Source.Length > 1) direction = "RX";
            }
            packet = packet with { Direction = direction };
        }
        if (!frame.IsInformation && !frame.IsUnnumberedInformation)
        {
            if ((control & 0xEF) is 0x83 or 0x43 or 0x63)
                ClearScope(scope);
            if (!frame.Information.IsEmpty)
            {
                var parameters = ReadUaParameters(packet.Bytes, infoStart, frame.Information.Length);
                packet = packet with { Fields = [hdlc, parameters] };
            }
            Put(packet);
            return;
        }
        var payload = MappedPayload.Contiguous(bytes, infoStart, frame.Information.Length);
        if (transfer is null && !frame.Segmented)
        {
            ReadLlc(packet, payload, scope, source.Key, [hdlc]);
            return;
        }
        if (transfer is null) segments[segmentKey] = transfer = new();
        if (transfer.Parts.Count > 0 && frame.SendSequence != transfer.ExpectedSequence)
        {
            // A captured retransmission is not a second segment.
            var previous = transfer.Parts[^1];
            if (frame.SendSequence == (transfer.ExpectedSequence + 7) % 8 && payload.Bytes.AsSpan().SequenceEqual(previous.Payload.Bytes))
            {
                Put(packet with { Diagnostic = $"Повтор HDLC-сегмента № {previous.Packet.Id}.", RelatedPacketIds = [previous.Packet.Id] });
                return;
            }
            foreach (var part in transfer.Parts) Annotate(part.Packet.Id, "Нарушен порядок HDLC-сегментов.", true);
            segments.Remove(segmentKey);
            Put(packet with { HasError = true, Diagnostic = "Нарушен порядок HDLC-сегментов; APDU не собран." });
            return;
        }
        transfer.ByteCount += payload.Bytes.Length;
        if (transfer.ByteCount > MaxMessageBytes || transfer.Parts.Count >= 4096)
        {
            segments.Remove(segmentKey);
            Put(packet with { HasError = true, Diagnostic = "Превышен предел сборки HDLC (1 МиБ / 4096 сегментов)." });
            return;
        }
        transfer.ExpectedSequence = (frame.SendSequence + 1) & 7;
        packet = packet with { Diagnostic = frame.Segmented ? "HDLC-сегмент; ожидается продолжение." : "Последний HDLC-сегмент.", IsIncomplete = frame.Segmented };
        Put(packet);
        transfer.Parts.Add((packet, payload));
        if (frame.Segmented) return;
        segments.Remove(segmentKey);
        var (assembled, mapped) = Combine(transfer.Parts, "Собранный HDLC APDU");
        ReadLlc(assembled, mapped, scope, source.Key, assembled.Fields);
        LinkParts(transfer.Parts.Select(p => p.Packet.Id), assembled.Id, "HDLC APDU собран");
    }

    private void ReadWrapper(InspectedPacket packet, int session)
    {
        var bytes = packet.Bytes;
        ushort source = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));
        ushort destination = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        var header = InspectionField.At("Wrapper", "version 1", 0, 8,
            InspectionField.At("Version", "1", 0, 2),
            InspectionField.At("Source wPort", source.ToString(CultureInfo.InvariantCulture), 2, 2),
            InspectionField.At("Destination wPort", destination.ToString(CultureInfo.InvariantCulture), 4, 2),
            InspectionField.At("Payload length", (bytes.Length - 8).ToString(CultureInfo.InvariantCulture), 6, 2));
        ReadApdu(packet, MappedPayload.Contiguous(bytes, 8, bytes.Length - 8), Scope(session, $"Wrapper:{Pair(source.ToString(), destination.ToString())}"),
            source.ToString(CultureInfo.InvariantCulture), [header]);
    }

    private void ReadLlc(InspectedPacket packet, MappedPayload payload, string scope, string sender, IReadOnlyList<InspectionField> outer)
    {
        string direction = LlcDirection(payload.Bytes);
        if (direction == "?")
        {
            Put(packet with { HasError = true, Diagnostic = "Нет корректного LLC-заголовка; возможно, начало APDU отсутствует в логе.", Fields = outer });
            return;
        }
        if (packet.Direction == "?") packet = packet with { Direction = direction };
        var llc = payload.Field("LLC", payload.Bytes[1] == 0xE7 ? "response" : "command", 0, 3,
            payload.Field("Destination LSAP", $"0x{payload.Bytes[0]:X2}", 0, 1),
            payload.Field("Source LSAP", $"0x{payload.Bytes[1]:X2}", 1, 1),
            payload.Field("Quality", "0x00", 2, 1));
        ReadApdu(packet, payload.Slice(3, payload.Bytes.Length - 3), scope, sender, [.. outer, llc]);
    }

    private void ReadApdu(InspectedPacket packet, MappedPayload payload, string scope, string sender, IReadOnlyList<InspectionField> outer)
    {
        if (payload.Bytes.Length == 0)
        {
            Put(packet with { HasError = true, Diagnostic = "Пустой APDU.", Fields = outer });
            return;
        }
        var inspection = ApduFieldDecoder.Decode(payload);
        if (packet.Direction == "?") packet = packet with { Direction = ApduDirection(payload.Bytes[0]) };
        if (sender == "?") sender = packet.Direction;
        packet = packet with { Kind = inspection.Kind, Fields = [.. outer, inspection.Field],
            Diagnostic = inspection.Diagnostic, HasError = inspection.HasError };
        switch (inspection.Apdu)
        {
            case AcseApdu acse:
                ClearScope(scope);
                if (acse.Kind == AcseKind.Aare)
                    packet = packet with { Diagnostic = $"Association result: {acse.Result?.ToString() ?? "нет"}; diagnostic: {acse.Diagnostic?.Code.ToString() ?? "нет"}",
                        HasError = acse.Result is not (null or 0) };
                break;
            case LnRequest request:
                var op = request.Operation;
                var target = new InspectionTarget(op.ClassId, op.LogicalName.ToString(), op.Index, op.Kind == CosemOperationKind.Invoke);
                string requestKey = RequestKey(scope, op.Kind, request.InvokeIdAndPriority);
                string signature = Convert.ToHexString(payload.Bytes);
                bool ambiguous = requests.TryGetValue(requestKey, out var existing) &&
                    (existing.Ambiguous || existing.Signature != signature || requestSenders.GetValueOrDefault(requestKey) != sender);
                if (existing is not null)
                    Annotate(existing.PacketId, ambiguous ? "Invoke ID повторно использован до ответа; связь неоднозначна." : "Запрос повторён до получения ответа.");
                requests[requestKey] = new(packet.Id, target, signature, ambiguous);
                packet = packet with { Target = target, DataValue = op.Value,
                    ValueText = op.Value is null ? "" : DlmsDisplay.Value(op.Value),
                    Diagnostic = ambiguous ? "Неоднозначный Invoke ID: ответ не будет привязан к OBIS." : packet.Diagnostic };
                // sender is scoped alongside invoke IDs, so traffic from a different peer cannot match.
                requestSenders[requestKey] = sender;
                if (op.Kind == CosemOperationKind.Write && target.ClassId is 3 or 4 or 5 &&
                    target.Index == (target.ClassId == 5 ? 4 : 3))
                    scalers.Remove($"{scope}|{target.ClassId}|{target.Obis}");
                blocks.Remove(requestKey);
                break;
            case LnResponse response:
                string responseKey = RequestKey(scope, response.Kind, response.InvokeIdAndPriority);
                packet = MatchResponse(packet, responseKey, sender, true);
                packet = packet with { DataValue = response.Result.Value,
                    ValueText = response.Result.Value is { } value ? DlmsDisplay.Value(value) : response.Result.Error.ToString(),
                    HasError = packet.HasError || response.Result.Error != CosemResultCode.Success || response.ReturnDataError.HasValue };
                if (response.Result.Error != CosemResultCode.Success)
                    packet = packet with { Diagnostic = Join(packet.Diagnostic, $"Ошибка прибора: {response.Result.Error}") };
                if (response.ReturnDataError is { } returnedError)
                    packet = packet with { Diagnostic = Join(packet.Diagnostic, $"Ошибка return data: {returnedError}") };
                packet = InterpretValue(packet, scope);
                break;
            case GetNextRequest next:
                string nextKey = RequestKey(scope, CosemOperationKind.Read, next.InvokeIdAndPriority);
                if (requests.TryGetValue(nextKey, out var ongoing) && !ongoing.Ambiguous)
                    packet = packet with { Target = ongoing.Target, RelatedPacketIds = [ongoing.PacketId] };
                break;
            case GetBlockResponse block:
                string blockKey = RequestKey(scope, CosemOperationKind.Read, block.InvokeIdAndPriority);
                packet = MatchResponse(packet, blockKey, sender, false);
                Put(packet);
                ReadBlock(packet, block, inspection.BlockDataOffset, payload, blockKey, scope);
                return;
            case DataNotification notification:
                packet = packet with { DataValue = notification.Body, ValueText = DlmsDisplay.Value(notification.Body),
                    Diagnostic = "Уведомление: OBIS определяется только при наличии описания структуры данных." };
                break;
        }
        Put(packet);
    }

    private readonly Dictionary<string, string> requestSenders = [];

    private InspectedPacket MatchResponse(InspectedPacket packet, string key, string sender, bool complete)
    {
        if (!requests.TryGetValue(key, out var request))
            return packet with { Diagnostic = Join(packet.Diagnostic, "Запрос не найден; OBIS неизвестен.") };
        if (requestSenders.GetValueOrDefault(key) is { } originalSender && sender != "?" && originalSender == sender)
            return packet with { Diagnostic = Join(packet.Diagnostic, "Ответ имеет то же направление / источник, что и запрос; связь не установлена.") };
        if (complete) { requests.Remove(key); requestSenders.Remove(key); }
        if (request.Ambiguous)
            return packet with { Diagnostic = Join(packet.Diagnostic, "Неоднозначный Invoke ID; OBIS не определён.") };
        Link(request.PacketId, packet.Id);
        return packet with { Target = request.Target, RelatedPacketIds = [request.PacketId] };
    }

    private void ReadBlock(InspectedPacket packet, GetBlockResponse block, int dataOffset, MappedPayload payload, string key, string scope)
    {
        if (block.Error.HasValue)
        {
            Annotate(packet.Id, $"Ошибка блока: {block.Error}", true);
            blocks.Remove(key); requests.Remove(key); requestSenders.Remove(key);
            return;
        }
        if (!blocks.TryGetValue(key, out var transfer))
        {
            if (block.BlockNumber != 1)
            {
                Annotate(packet.Id, "Начало блочного ответа отсутствует; значение не собрано.", true);
                return;
            }
            blocks[key] = transfer = new();
        }
        if (block.BlockNumber != transfer.Expected || dataOffset < 0 || transfer.ByteCount + block.Data.Length > MaxMessageBytes || transfer.Parts.Count >= 4096)
        {
            if (transfer.Parts.Count > 0 && block.BlockNumber == transfer.Expected - 1 &&
                block.Data.AsSpan().SequenceEqual(transfer.Parts[^1].Payload.Bytes))
            {
                Annotate(packet.Id, $"Повтор DLMS-блока из пакета № {transfer.Parts[^1].Packet.Id}.");
                return;
            }
            foreach (var part in transfer.Parts) Annotate(part.Packet.Id, "Сборка прервана: нарушен порядок DLMS-блоков.", true);
            blocks.Remove(key); requests.Remove(key); requestSenders.Remove(key);
            Annotate(packet.Id, "Нарушен порядок или превышен предел DLMS-блоков; значение не собрано.", true);
            return;
        }
        transfer.ByteCount += block.Data.Length;
        transfer.Expected++;
        transfer.Parts.Add((packet, payload.Slice(dataOffset, block.Data.Length)));
        if (!block.LastBlock)
        {
            Annotate(packet.Id, $"DLMS-блок {block.BlockNumber}; ожидается продолжение.", incomplete: true);
            return;
        }
        blocks.Remove(key); requests.Remove(key); requestSenders.Remove(key);
        var (assembled, mapped) = Combine(transfer.Parts, "Собранное DLMS-значение");
        var decoded = new AxdrCodec().Decode(new ReadOnlySequence<byte>(mapped.Bytes));
        if (decoded.Status == DecodeStatus.Done && decoded.Consumed == mapped.Bytes.Length && decoded.Value is { } value)
        {
            try
            {
                assembled = assembled with { Fields = [.. assembled.Fields, ApduFieldDecoder.DecodeValue(mapped, value)] };
            }
            catch (InvalidDataException error)
            {
                assembled = assembled with { Diagnostic = error.Message };
            }
            assembled = InterpretValue(assembled with { Target = packet.Target, DataValue = value, ValueText = DlmsDisplay.Value(value) }, scope);
        }
        else assembled = assembled with { HasError = true, Diagnostic = $"Не удалось декодировать собранное значение: {decoded.Status} {decoded.Error}" };
        Put(assembled);
        LinkParts(transfer.Parts.Select(p => p.Packet.Id), assembled.Id, "DLMS-значение собрано");
    }

    private InspectedPacket InterpretValue(InspectedPacket packet, string scope)
    {
        if (packet.Target is not { } target || packet.DataValue is not { } value || packet.HasError) return packet;
        string key = $"{scope}|{target.ClassId}|{target.Obis}";
        int scalerAttribute = target.ClassId == 5 ? 4 : 3;
        if (target.ClassId is 3 or 4 or 5 && target.Index == scalerAttribute &&
            value.Type == DlmsType.Structure && value.Items.Length == 2 &&
            value.Items[0].Type == DlmsType.Int8 && value.Items[1].Type == DlmsType.Enum)
            scalers[key] = ((sbyte)value.Items[0].AsInt64(), (byte)value.Items[1].AsUInt64());
        if ((target.Index == 2 || target.ClassId == 5 && target.Index == 3) && target.ClassId is 3 or 4 or 5 && scalers.TryGetValue(key, out var scaler) &&
            value.Type is DlmsType.Int8 or DlmsType.UInt8 or DlmsType.Int16 or DlmsType.UInt16 or DlmsType.Int32 or DlmsType.UInt32 or DlmsType.Int64 or DlmsType.UInt64 or DlmsType.Float32 or DlmsType.Float64)
        {
            double number = Convert.ToDouble(value.Value, CultureInfo.InvariantCulture) * Math.Pow(10, scaler.Scaler);
            string unit = scaler.Unit switch { 27 => "Вт", 28 => "ВА", 29 => "вар", 30 => "Вт·ч", 31 => "ВА·ч", 32 => "вар·ч", 33 => "А", 35 => "В", 44 => "Гц", _ => $"unit={scaler.Unit}" };
            packet = packet with { ValueText = $"{number.ToString("G12", CultureInfo.InvariantCulture)} {unit}\nИсходное: {DlmsDisplay.Value(value)} · scaler {scaler.Scaler} (из предыдущего ответа)" };
        }
        return packet;
    }

    private (InspectedPacket Packet, MappedPayload Payload) Combine(List<(InspectedPacket Packet, MappedPayload Payload)> parts, string kind)
    {
        int total = parts.Sum(p => p.Packet.Bytes.Length);
        var raw = new byte[total];
        var data = new List<byte>();
        var offsets = new List<int>();
        var fields = new List<InspectionField>();
        int shift = 0;
        foreach (var part in parts)
        {
            part.Packet.Bytes.CopyTo(raw, shift);
            data.AddRange(part.Payload.Bytes);
            offsets.AddRange(part.Payload.Offsets.Select(offset => offset + shift));
            fields.Add(InspectionField.At($"Исходный пакет № {part.Packet.Id}", part.Packet.Kind, shift, part.Packet.Bytes.Length));
            shift += part.Packet.Bytes.Length;
        }
        long id = NewId();
        var first = parts[0].Packet;
        var packet = new InspectedPacket(id, first.Direction, first.Timestamp, kind, raw,
            string.Join("; ", parts.Select(p => p.Packet.Origin).Distinct()),
            string.Join(Environment.NewLine, parts.Select(p => p.Packet.SourceText).Distinct()))
        {
            Kind = kind, Fields = fields, RelatedPacketIds = parts.Select(p => p.Packet.Id).ToArray(),
            Diagnostic = "Байты исходных пакетов показаны последовательно; поля могут занимать несколько диапазонов."
        };
        return (packet, new(data.ToArray(), offsets.ToArray()));
    }

    private void LinkParts(IEnumerable<long> ids, long assembledId, string message)
    {
        foreach (long id in ids)
        {
            var packet = Get(id);
            Put(packet with { RelatedPacketIds = packet.RelatedPacketIds.Append(assembledId).Distinct().ToArray(),
                IsIncomplete = false, Diagnostic = $"{message} в № {assembledId}." });
        }
    }

    private void ClearScope(string scope)
    {
        foreach (var key in blocks.Keys.Where(k => k.StartsWith(scope + "|", StringComparison.Ordinal)).ToArray())
        {
            foreach (var part in blocks[key].Parts) Annotate(part.Packet.Id, "Сборка DLMS-блоков прервана сменой ассоциации.", true);
            blocks.Remove(key);
        }
        foreach (var key in requests.Keys.Where(k => k.StartsWith(scope + "|", StringComparison.Ordinal)).ToArray())
        {
            Annotate(requests[key].PacketId, "Ассоциация / HDLC-соединение изменилось до получения ответа.");
            requests.Remove(key); requestSenders.Remove(key); blocks.Remove(key);
        }
        foreach (var key in scalers.Keys.Where(k => k.StartsWith(scope + "|", StringComparison.Ordinal)).ToArray()) scalers.Remove(key);
        foreach (var key in segments.Keys.Where(k => k.StartsWith(scope + "|", StringComparison.Ordinal)).ToArray())
        {
            foreach (var part in segments[key].Parts) Annotate(part.Packet.Id, "HDLC-сборка прервана переустановкой соединения.", true);
            segments.Remove(key);
        }
    }

    private InspectedPacket MakePacket(StreamBuffer stream, int offset, int length, string framing)
    {
        var sources = stream.Sources.Where(s => s.Offset < offset + length && s.Offset + s.Length > offset).ToArray();
        var input = sources[0].Input;
        long id = stream.PendingId != 0 ? stream.PendingId : NewId();
        stream.PendingId = 0;
        return new(id, input.Direction, input.Timestamp, framing, stream.Bytes.AsSpan(offset, length).ToArray(),
            string.Join("; ", sources.Select(s => s.Input.Origin).Distinct()),
            string.Join(Environment.NewLine, sources.Select(s => s.Input.SourceText).Distinct()));
    }

    private static void Consume(StreamBuffer stream, int length)
    {
        if (length == 0) return;
        stream.Bytes = stream.Bytes.AsSpan(length).ToArray();
        stream.Sources = stream.Sources.Where(s => s.Offset + s.Length > length)
            .Select(s => new SourcePart(Math.Max(0, s.Offset - length), s.Offset < length ? s.Length + s.Offset - length : s.Length, s.Input)).ToList();
    }

    private void FlushIncomplete(StreamBuffer stream, string reason = "Лог закончился до завершения пакета.")
    {
        if (stream.PendingId != 0) Annotate(stream.PendingId, reason, true, true);
        stream.PendingId = 0; stream.Bytes = []; stream.Sources.Clear();
    }

    private long NewId()
    {
        if (nextId >= MaxPackets) throw new FormatException("Достигнут предел инспектора (50000 пакетов). Разделите лог на части.");
        return ++nextId;
    }
    private InspectedPacket Get(long id) => packets[checked((int)id - 1)];
    private void Put(InspectedPacket packet)
    {
        int index = checked((int)packet.Id - 1);
        if (index == packets.Count) packets.Add(packet);
        else if (index < packets.Count) packets[index] = packet;
        else throw new InvalidOperationException("Нарушен порядок пакетов инспектора.");
    }
    private void Annotate(long id, string message, bool error = false, bool incomplete = false)
    {
        var packet = Get(id);
        Put(packet with { Diagnostic = Join(packet.Diagnostic, message), HasError = packet.HasError || error,
            IsIncomplete = packet.IsIncomplete || incomplete });
    }
    private void Link(long id, long related)
    {
        var packet = Get(id);
        Put(packet with { RelatedPacketIds = packet.RelatedPacketIds.Append(related).Distinct().ToArray() });
    }
    private static string Join(string first, string second) => string.IsNullOrEmpty(first) ? second : $"{first} {second}";
    private static string Scope(int session, string channel) => $"{session}:{channel}";
    private static string Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? $"{a}/{b}" : $"{b}/{a}";
    private static string RequestKey(string scope, CosemOperationKind kind, byte invoke) => $"{scope}|{kind}|{invoke & 15}";
    private static bool IsWrapperStart(ReadOnlySpan<byte> bytes) => bytes is [0, 1, ..];
    private static string LlcDirection(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xE6 or 0xFF, 0xE6, 0, ..] => "TX",
        [0xE6 or 0xFF, 0xE7, 0, ..] => "RX",
        _ => "?"
    };
    private static string ApduDirection(byte tag) => tag switch
    {
        0x01 or 0x60 or 0x62 or 0xC0 or 0xC1 or 0xC3 or 0x21 or 0xC8 or 0xC9 or 0xCB or 0xD0 or 0xD1 or 0xD3 => "TX",
        0x08 or 0x0F or 0x61 or 0x63 or 0xC4 or 0xC5 or 0xC7 or 0x28 or 0xCC or 0xCD or 0xCF or 0xD4 or 0xD5 or 0xD7 => "RX",
        _ => "?"
    };
    private static bool IsApduStart(byte tag) => ApduDirection(tag) != "?" || tag is 0x25 or 0xDB or 0xDC;
    private static int FindValidFrameStart(ReadOnlyMemory<byte> bytes, bool includeWrapper)
    {
        for (int i = 1; i < bytes.Length - 3; i++)
            if (IsValidFrameStart(bytes[i..], includeWrapper)) return i;
        return -1;
    }
    private static bool IsValidFrameStart(ReadOnlyMemory<byte> bytes, bool includeWrapper)
    {
        var data = bytes.Span;
        if (data.Length >= 9 && data[0] == 0x7E && (data[1] & 0xF0) == 0xA0)
        {
            int length = ((data[1] & 7) << 8) | data[2];
            return length >= 7 && length + 2 <= data.Length && data[length + 1] == 0x7E &&
                new HdlcCodec().Decode(new ReadOnlySequence<byte>(bytes[..(length + 2)])).Status == DecodeStatus.Done;
        }
        if (!includeWrapper || data.Length <= 8 || !IsWrapperStart(data) || !IsApduStart(data[8])) return false;
        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        if (payloadLength == 0 || payloadLength > data.Length - 8) return false;
        var decoded = new ApduCodec().Decode(new ReadOnlySequence<byte>(bytes.Slice(8, payloadLength)));
        return decoded.Status == DecodeStatus.Done && decoded.Consumed == payloadLength;
    }
    private static ushort ReadCrc(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static string ControlName(byte control) => (control & 0xEF) switch
    {
        0x83 => "SNRM", 0x63 => "UA", 0x43 => "DISC", 0x0F => "DM", 0x87 => "FRMR",
        _ when (control & 1) == 0 => $"I · N(S)={(control >> 1) & 7} · N(R)={(control >> 5) & 7}",
        _ when (control & 0x0F) == 1 => $"RR · N(R)={(control >> 5) & 7}", _ => $"0x{control:X2}"
    };

    private static (string Display, string Key) ReadAddress(byte[] bytes, ref int position)
    {
        int start = position;
        uint value = 0;
        bool complete = false;
        while (position < bytes.Length - 3 && position - start < 4)
        {
            byte current = bytes[position++];
            value = value << 7 | (uint)(current >> 1);
            if ((current & 1) != 0) { complete = true; break; }
        }
        int length = position - start;
        string key = length switch
        {
            2 => $"{value >> 7}.{value & 127}",
            4 => $"{value >> 14}.{value & 16383}",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };
        return ($"{value} / {length} байт{(length is 2 or 4 ? $" · logical/physical {key}" : "")}{(complete ? "" : " · адрес неполный")}", key);
    }

    private static InspectionField ReadUaParameters(byte[] bytes, int start, int length)
    {
        var children = new List<InspectionField>();
        if (length >= 3 && bytes[start] == 0x81 && bytes[start + 1] == 0x80)
        {
            int position = start + 3;
            while (position + 2 <= start + length)
            {
                int fieldStart = position;
                byte tag = bytes[position++];
                int size = bytes[position++];
                if (size is < 1 or > 4 || size > start + length - position) break;
                uint value = 0;
                for (int i = 0; i < size; i++) value = value << 8 | bytes[position++];
                string name = tag switch { 5 => "maxTx", 6 => "maxRx", 7 => "windowTx", 8 => "windowRx", _ => $"Parameter {tag:X2}" };
                children.Add(InspectionField.At(name, value.ToString(CultureInfo.InvariantCulture), fieldStart, size + 2));
            }
        }
        return InspectionField.At("Параметры HDLC", $"{length} байт", start, length, children.ToArray());
    }
}
