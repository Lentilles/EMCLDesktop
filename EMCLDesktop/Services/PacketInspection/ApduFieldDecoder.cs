using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using EMCL.Core.Codecs;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Protocol.Axdr;

namespace EMCLDesktop.Services.PacketInspection;

internal sealed record ApduInspection(string Kind, InspectionField Field, DlmsApdu? Apdu,
    string Diagnostic = "", bool HasError = false, int BlockDataOffset = -1);

/// <summary>Uses EMCL for decoding; this reader only records wire boundaries for the inspector.</summary>
internal sealed class ApduFieldDecoder(MappedPayload payload)
{
    internal const int MaxFields = 10_000;
    private readonly byte[] bytes = payload.Bytes;
    private readonly List<InspectionField> fields = [];
    private int position, fieldCount;
    private int blockDataOffset = -1;

    public static ApduInspection Decode(MappedPayload payload)
    {
        var reader = new ApduFieldDecoder(payload);
        var result = new ApduCodec().Decode(new ReadOnlySequence<byte>(payload.Bytes));
        string kind = Name(payload.Bytes.FirstOrDefault());
        string diagnostic = result.Status switch
        {
            DecodeStatus.Done => result.Consumed == payload.Bytes.Length ? "" : "Лишние байты после APDU.",
            DecodeStatus.NeedMoreData => "Неполный DLMS APDU: не хватает байтов.",
            DecodeStatus.Unsupported => "Этот тип DLMS APDU пока не поддерживается декодером EMCL.",
            _ => $"Ошибка DLMS: {result.Error}"
        };
        if (kind == "Защищённый DLMS APDU")
            diagnostic = "Защищённый APDU: содержимое зашифровано. Нужны ключи и поддержка расшифровки в EMCL; показаны исходные байты.";
        try
        {
            if (result.Status == DecodeStatus.Done && result.Value is { } decoded)
                reader.ReadApdu(decoded);
            else
            {
                reader.Take("APDU tag", kind, 1);
                if (payload.Bytes.Length > 1)
                    reader.Take("Неразобранное содержимое", Convert.ToHexString(payload.Bytes.AsSpan(1)), payload.Bytes.Length - 1);
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostic = $"Не удалось разметить поля: {error.Message}";
        }
        return new(kind, payload.Field("DLMS", kind, 0, payload.Bytes.Length, reader.fields.ToArray()),
            result.Status == DecodeStatus.Done && result.Consumed == payload.Bytes.Length ? result.Value : null,
            diagnostic, result.Status is DecodeStatus.InvalidData or DecodeStatus.NeedMoreData ||
                        result.Status == DecodeStatus.Done && result.Consumed != payload.Bytes.Length,
            reader.blockDataOffset);
    }

    public static string Name(byte tag) => tag switch
    {
        0x60 => "AARQ", 0x61 => "AARE", 0x62 => "RLRQ", 0x63 => "RLRE",
        0xC0 => "GET request", 0xC1 => "SET request", 0xC3 => "ACTION request",
        0xC4 => "GET response", 0xC5 => "SET response", 0xC7 => "ACTION response",
        0x0F => "Data notification", 0x01 => "Initiate request", 0x08 => "Initiate response",
        0x21 or 0x25 or 0x28 or 0xC8 or 0xC9 or 0xCB or 0xCC or 0xCD or 0xCF or
        0xD0 or 0xD1 or 0xD3 or 0xD4 or 0xD5 or 0xD7 or 0xDB or 0xDC => "Защищённый DLMS APDU",
        _ => $"APDU 0x{tag:X2}"
    };

    private void ReadApdu(DlmsApdu apdu)
    {
        if (apdu is AcseApdu)
        {
            fields.Add(ReadBer(bytes.Length, 0));
            return;
        }
        Take("APDU tag", Name(bytes[0]), 1);
        if (apdu is LnRequest or LnResponse or GetNextRequest or GetBlockResponse)
        {
            Take("Вариант", $"{bytes[position]} ({(bytes[position] == 1 ? "normal" : "block / next")})", 1);
            int invokeOffset = position;
            byte invoke = bytes[position];
            fields.Add(payload.Field("Invoke ID / priority", $"0x{invoke:X2}", position++, 1,
                payload.Field("Invoke ID", $"{invoke & 15} · mask 0x0F", invokeOffset, 1),
                payload.Field("Service class", (invoke & 0x40) != 0 ? "confirmed · mask 0x40" : "unconfirmed · mask 0x40", invokeOffset, 1),
                payload.Field("Priority", (invoke & 0x80) != 0 ? "high · mask 0x80" : "normal · mask 0x80", invokeOffset, 1)));
        }
        switch (apdu)
        {
            case LnRequest request:
                Take("Class ID", request.Operation.ClassId.ToString(CultureInfo.InvariantCulture), 2);
                Take("OBIS / logical name", request.Operation.LogicalName.ToString(), 6);
                Take(request.Operation.Kind == CosemOperationKind.Invoke ? "Method ID" : "Attribute ID",
                    request.Operation.Index.ToString(CultureInfo.InvariantCulture), 1);
                Take(request.Operation.Kind == CosemOperationKind.Invoke ? "Параметры присутствуют" : "Selective access присутствует",
                    bytes[position] == 0 ? "нет" : "да", 1);
                if (request.Operation.Selection is { } selection)
                {
                    Take("Access selector", selection.Selector.ToString(CultureInfo.InvariantCulture), 1);
                    fields.Add(ReadValue("Параметры selective access", selection.Parameters));
                }
                if (request.Operation.Value is { } value) fields.Add(ReadValue("Переданное значение", value));
                break;
            case GetNextRequest next:
                Take("Block number", next.BlockNumber.ToString(CultureInfo.InvariantCulture), 4);
                break;
            case GetBlockResponse block:
                Take("Last block", block.LastBlock ? "да" : "нет", 1);
                Take("Block number", block.BlockNumber.ToString(CultureInfo.InvariantCulture), 4);
                Take("Result choice", block.Error.HasValue ? "data-access-result" : "raw-data", 1);
                if (block.Error is { } error) Take("Ошибка", error.ToString(), 1);
                else
                {
                    ReadLengthField(fields, "Длина блока");
                    blockDataOffset = position;
                    Take("Данные блока", $"{block.Data.Length} байт", block.Data.Length);
                }
                break;
            case LnResponse response:
                if (response.Kind == CosemOperationKind.Read)
                    Take("Result choice", response.Result.Value is null ? "data-access-result" : "data", 1);
                if (response.Kind != CosemOperationKind.Read || response.Result.Error != CosemResultCode.Success)
                    Take("Результат операции", response.Result.Error.ToString(), 1);
                if (response.Kind == CosemOperationKind.Invoke)
                {
                    bool present = bytes[position] != 0;
                    Take("Return parameters присутствуют", present ? "да" : "нет", 1);
                    if (present)
                    {
                        Take("Return choice", response.ReturnDataError.HasValue ? "data-access-result" : "data", 1);
                        if (response.ReturnDataError is { } returnedError) Take("Ошибка return data", returnedError.ToString(), 1);
                    }
                }
                if (response.Result.Value is { } returned) fields.Add(ReadValue("Полученное значение", returned));
                break;
            case DataNotification notification:
                Take("Long invoke ID / priority", $"0x{notification.LongInvokeIdAndPriority:X8}", 4);
                ReadLengthField(fields, "Длина date-time");
                Take("Date-time", Convert.ToHexString(notification.DateTime.AsSpan()), notification.DateTime.Length);
                fields.Add(ReadValue("Тело уведомления", notification.Body));
                break;
            case InitiateRequest initiate:
                Take("Dedicated key присутствует", bytes[position] == 0 ? "нет" : "да", 1);
                if (!initiate.DedicatedKey.IsEmpty)
                {
                    ReadLengthField(fields, "Длина dedicated key");
                    Take("Dedicated key", Convert.ToHexString(initiate.DedicatedKey.AsSpan()), initiate.DedicatedKey.Length);
                }
                Take("Response allowed присутствует", initiate.ResponseAllowed.HasValue ? "да" : "нет", 1);
                if (initiate.ResponseAllowed.HasValue) Take("Response allowed", initiate.ResponseAllowed.ToString()!, 1);
                ReadQos();
                ReadNegotiation(false);
                break;
            case InitiateResponse:
                ReadQos();
                ReadNegotiation(true);
                break;
        }
        if (position < bytes.Length) Take("Оставшиеся байты", Convert.ToHexString(bytes.AsSpan(position)), bytes.Length - position);
    }

    private void ReadQos()
    {
        bool present = bytes[position] != 0;
        Take("QoS присутствует", present ? "да" : "нет", 1);
        if (present) Take("QoS", ((sbyte)bytes[position]).ToString(CultureInfo.InvariantCulture), 1);
    }

    private void ReadNegotiation(bool response)
    {
        Take("DLMS version", bytes[position].ToString(CultureInfo.InvariantCulture), 1);
        Take("Conformance encoding", Convert.ToHexString(bytes.AsSpan(position, 4)), 4);
        Take("Conformance", $"0x{Convert.ToHexString(bytes.AsSpan(position, 3))}", 3);
        Take("Max receive PDU size", BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position)).ToString(CultureInfo.InvariantCulture), 2);
        if (response) Take("VAA name", $"0x{BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position)):X4}", 2);
    }

    private InspectionField ReadValue(string name, DlmsValue value, int depth = 0, bool tagged = true)
    {
        if (++fieldCount > MaxFields || depth > 64) throw new InvalidDataException("Достигнут предел отображения полей (10000 / глубина 64).");
        int start = position;
        var children = new List<InspectionField>();
        if (tagged) children.Add(payload.Field("Тип", $"{value.Type} (0x{bytes[position]:X2})", position++, 1));
        int valueStart = position;
        string summary;
        switch (value.Type)
        {
            case DlmsType.Array:
            case DlmsType.Structure:
                if (tagged) ReadLengthField(children, "Количество элементов");
                int index = 0;
                foreach (var item in value.Items) children.Add(ReadValue($"[{index++}]", item, depth + 1, tagged));
                summary = $"{value.Type} · {value.Items.Length} элементов";
                break;
            case DlmsType.CompactArray:
                var compact = (DlmsCompactArray)value.Value!;
                children.Add(ReadDescription(compact.Description, depth + 1));
                ReadLengthField(children, "Длина compact contents");
                int row = 0;
                foreach (var item in compact.Rows) children.Add(ReadValue($"Строка [{row++}]", item, depth + 1, false));
                summary = $"CompactArray · {compact.Rows.Length} строк";
                break;
            default:
                int length = value.Type switch
                {
                    DlmsType.Null => 0,
                    DlmsType.Boolean or DlmsType.Int8 or DlmsType.UInt8 or DlmsType.Enum or DlmsType.Bcd or DlmsType.DeltaInt8 or DlmsType.DeltaUInt8 => 1,
                    DlmsType.Int16 or DlmsType.UInt16 or DlmsType.DeltaInt16 or DlmsType.DeltaUInt16 => 2,
                    DlmsType.Int32 or DlmsType.UInt32 or DlmsType.Float32 or DlmsType.Time or DlmsType.DeltaInt32 or DlmsType.DeltaUInt32 => 4,
                    DlmsType.Int64 or DlmsType.UInt64 or DlmsType.Float64 => 8,
                    DlmsType.DateTime => 12,
                    DlmsType.Date => 5,
                    DlmsType.BitString => ReadLengthField(children, "Количество бит") is var bits ? (bits + 7) / 8 : 0,
                    DlmsType.OctetString or DlmsType.VisibleString or DlmsType.Utf8String => ReadLengthField(children, "Длина"),
                    _ => throw new InvalidDataException("Неизвестный тип значения.")
                };
                valueStart = position;
                Guard(length);
                position += length;
                summary = DlmsDisplay.Value(value);
                if (value.Type == DlmsType.OctetString && length == 6)
                    summary += $" · OBIS-кандидат {string.Join('.', bytes.AsSpan(valueStart, length).ToArray())}";
                children.Add(payload.Field("Значение", summary, valueStart, length));
                break;
        }
        return payload.Field(name, summary, start, position - start, children.ToArray());
    }

    private InspectionField ReadDescription(CompactTypeDescription description, int depth)
    {
        if (++fieldCount > MaxFields || depth > 64) throw new InvalidDataException("Слишком сложное описание compact array.");
        int start = position;
        Guard(1); position++;
        var children = new List<InspectionField>();
        if (description.Type == DlmsType.Array)
        {
            children.Add(payload.Field("Количество элементов", description.ElementCount.ToString(CultureInfo.InvariantCulture), position, 2));
            Guard(2); position += 2;
        }
        else if (description.Type == DlmsType.Structure) ReadLengthField(children, "Количество полей");
        foreach (var child in description.Children) children.Add(ReadDescription(child, depth + 1));
        return payload.Field("Описание типа", description.Type.ToString(), start, position - start, children.ToArray());
    }

    internal static InspectionField DecodeValue(MappedPayload payload, DlmsValue value)
    {
        var reader = new ApduFieldDecoder(payload);
        return reader.ReadValue("Собранное значение", value);
    }

    private InspectionField ReadBer(int end, int depth, bool userInformation = false)
    {
        if (++fieldCount > MaxFields || depth > 64) throw new InvalidDataException("Слишком сложный BER.");
        int start = position;
        Guard(1);
        byte tag = bytes[position++];
        var children = new List<InspectionField> { payload.Field("BER tag", $"0x{tag:X2}", start, 1) };
        int length = ReadLengthField(children, "BER length");
        int contentStart = position;
        if (length > end - position) throw new InvalidDataException("BER выходит за границы контейнера.");
        string name = depth == 0 ? Name(tag) : tag switch
        {
            0xA1 => "Application context / diagnostic choice", 0xA2 => "Association result / diagnostic choice",
            0xA3 => "Result source diagnostic", 0x8A => "ACSE requirements", 0x8B or 0x89 => "Authentication mechanism",
            0xAC => "Calling authentication", 0xAA => "Responding authentication", 0xBE => "User information",
            0x04 => "Octet string", 0x02 => "Integer", 0x06 => "Object identifier", _ => $"BER 0x{tag:X2}"
        };
        string summary = "";
        if ((tag & 0x20) != 0)
        {
            while (position < contentStart + length)
                children.Add(ReadBer(contentStart + length, depth + 1, userInformation || tag == 0xBE));
        }
        else
        {
            summary = Convert.ToHexString(bytes.AsSpan(position, Math.Min(length, 64))) + (length > 64 ? "…" : "");
            if (tag == 0x02 && length == 1) summary = bytes[position].ToString(CultureInfo.InvariantCulture);
            if (userInformation && tag == 0x04 && length > 0)
                children.Add(Decode(payload.Slice(position, length)).Field);
            else children.Add(payload.Field("Содержимое", summary, position, length));
            position += length;
        }
        return payload.Field(name, summary, start, position - start, children.ToArray());
    }

    private int ReadLengthField(List<InspectionField> destination, string name)
    {
        int start = position;
        Guard(1);
        int count = bytes[position++];
        if ((count & 0x80) != 0)
        {
            int octets = count & 0x7F;
            if (octets is 0 or > 4) throw new InvalidDataException("Недопустимая длина A-XDR / BER.");
            Guard(octets);
            count = 0;
            for (int i = 0; i < octets; i++) count = checked(count * 256 + bytes[position++]);
        }
        destination.Add(payload.Field(name, count.ToString(CultureInfo.InvariantCulture), start, position - start));
        return count;
    }

    private void Take(string name, string value, int length)
    {
        Guard(length);
        fields.Add(payload.Field(name, value, position, length));
        position += length;
    }

    private void Guard(int length)
    {
        if (length < 0 || length > bytes.Length - position) throw new InvalidDataException("Неполное поле.");
    }
}
