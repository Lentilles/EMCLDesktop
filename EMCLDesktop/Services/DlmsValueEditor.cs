using System.Buffers;
using System.Globalization;
using System.Text.Json;
using EMCL.Core.Codecs;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;

namespace EMCLDesktop.Services;

/// <summary>Lossless editor input, independent of the shortened display text.</summary>
public static class DlmsValueEditor
{
    public static IReadOnlyList<DlmsType> Types { get; } = Enum.GetValues<DlmsType>();

    public static string Hint(DlmsType type) => type switch
    {
        DlmsType.Null => "Введите null.",
        DlmsType.Boolean => "true или false.",
        DlmsType.BitString => "Последовательность битов, например 10101.",
        DlmsType.OctetString => "Байты HEX, например 01 00 FF. Пустая строка — пустое значение.",
        DlmsType.DateTime => "12 байт COSEM date-time в HEX, без тега и длины.",
        DlmsType.Date => "5 байт COSEM date в HEX, без тега и длины.",
        DlmsType.Time => "4 байта COSEM time в HEX, без тега и длины.",
        DlmsType.VisibleString => "Текст ASCII без кавычек. Пробелы сохраняются.",
        DlmsType.Utf8String => "Текст UTF-8 без кавычек. Пробелы сохраняются.",
        DlmsType.Array or DlmsType.Structure =>
            "JSON: [{\"type\":\"Int8\",\"value\":-1},{\"type\":\"Enum\",\"value\":30}]. Вложенные коллекции используют такой же список.",
        DlmsType.CompactArray => "Полное значение A-XDR в HEX, включая тег 13 (CompactArray).",
        DlmsType.Float32 or DlmsType.Float64 => "Число с точкой, например 1.25. Без NaN и Infinity.",
        DlmsType.Bcd => "Байт BCD в десятичном виде: например 18 для HEX 12.",
        _ => "Целое десятичное число в диапазоне выбранного типа. Без пересчёта scaler/unit."
    };

    public static DlmsValue Parse(DlmsType type, string text, ProtocolLimits? limits = null)
    {
        var bounds = ProtocolLimits.Resolve(limits);
        if (text.Length > (long)bounds.MaxMessageBytes * 8)
            throw new FormatException("Значение превышает допустимый размер.");
        try
        {
            DlmsValue value;
            if (type is DlmsType.Array or DlmsType.Structure)
            {
                using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = bounds.MaxDepth });
                int remaining = bounds.MaxCollectionItems;
                value = ParseCollection(type, json.RootElement, bounds, ref remaining);
            }
            else
                value = ParseScalar(type, text, bounds);
            // The codec checks numeric ranges, exact date lengths, ASCII and complexity.
            _ = new AxdrCodec(bounds).EncodeBytes(value);
            return value;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or JsonException or InvalidOperationException)
        {
            throw new FormatException($"Некорректное значение {type}: {error.Message}", error);
        }
    }

    private static DlmsValue ParseCollection(DlmsType type, JsonElement array, ProtocolLimits limits, ref int remaining)
    {
        if (--remaining < 0 || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("Ожидается JSON-список значений в пределах лимита элементов.");
        var items = new List<DlmsValue>();
        foreach (var entry in array.EnumerateArray())
        {
            if (--remaining < 0) throw new FormatException("Превышен лимит элементов.");
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("type", out var tag) ||
                tag.ValueKind != JsonValueKind.String || !Enum.TryParse<DlmsType>(tag.GetString(), out var itemType) ||
                !Enum.IsDefined(itemType) || !entry.TryGetProperty("value", out var payload) ||
                entry.EnumerateObject().Count() != 2)
                throw new FormatException("Каждый элемент должен содержать только type (имя типа) и value.");
            items.Add(itemType is DlmsType.Array or DlmsType.Structure
                ? ParseCollection(itemType, payload, limits, ref remaining)
                : ParseScalar(itemType, payload.ValueKind == JsonValueKind.String
                    ? payload.GetString()! : payload.GetRawText(), limits));
        }
        return new DlmsValue(type, items);
    }

    private static DlmsValue ParseScalar(DlmsType type, string text, ProtocolLimits limits)
    {
        string trimmed = text.Trim();
        switch (type)
        {
            case DlmsType.Null:
                if (trimmed != "null") throw new FormatException("Ожидается null.");
                return DlmsValue.Null;
            case DlmsType.Boolean:
                return new(type, bool.Parse(trimmed));
            case DlmsType.VisibleString:
            case DlmsType.Utf8String:
                return new(type, text);
            case DlmsType.OctetString:
            case DlmsType.DateTime:
            case DlmsType.Date:
            case DlmsType.Time:
                return new(type, ParseHex(text));
            case DlmsType.BitString:
                if (trimmed.Any(c => c is not ('0' or '1')))
                    throw new FormatException("BitString должен содержать только 0 и 1.");
                byte[] bits = new byte[(trimmed.Length + 7) / 8];
                for (int i = 0; i < trimmed.Length; i++)
                    if (trimmed[i] == '1') bits[i / 8] |= (byte)(0x80 >> (i % 8));
                return new(type, new DlmsBitString(trimmed.Length, bits));
            case DlmsType.CompactArray:
                var bytes = ParseHex(text);
                var decoded = new AxdrCodec(limits).Decode(new ReadOnlySequence<byte>(bytes));
                if (decoded.Status != DecodeStatus.Done || decoded.Consumed != bytes.Length || decoded.Value?.Type != type)
                    throw new FormatException("Ожидается ровно одно полное значение CompactArray в A-XDR.");
                return decoded.Value;
            case DlmsType.Float32:
                var single = float.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!float.IsFinite(single)) throw new FormatException("Число должно быть конечным.");
                return new(type, single);
            case DlmsType.Float64:
                var number = double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number)) throw new FormatException("Число должно быть конечным.");
                return new(type, number);
            case DlmsType.UInt8:
            case DlmsType.UInt16:
            case DlmsType.UInt32:
            case DlmsType.UInt64:
            case DlmsType.Enum:
            case DlmsType.Bcd:
            case DlmsType.DeltaUInt8:
            case DlmsType.DeltaUInt16:
            case DlmsType.DeltaUInt32:
                return new(type, ulong.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture));
            case DlmsType.Int8:
            case DlmsType.Int16:
            case DlmsType.Int32:
            case DlmsType.Int64:
            case DlmsType.DeltaInt8:
            case DlmsType.DeltaInt16:
            case DlmsType.DeltaInt32:
                return new(type, long.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture));
            default:
                throw new FormatException("Выберите поддерживаемый тип значения.");
        }
    }

    private static byte[] ParseHex(string text) =>
        Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    public static string Format(DlmsValue value) => value.Type switch
    {
        DlmsType.Null => "null",
        DlmsType.Boolean => (bool)value.Value! ? "true" : "false",
        DlmsType.Array or DlmsType.Structure => JsonSerializer.Serialize(
            value.Items.Select(ToJson), new JsonSerializerOptions { WriteIndented = true, MaxDepth = 128 }),
        DlmsType.CompactArray => Convert.ToHexString(new AxdrCodec().EncodeBytes(value)),
        DlmsType.OctetString or DlmsType.DateTime or DlmsType.Date or DlmsType.Time => Convert.ToHexString(value.Bytes.AsSpan()),
        DlmsType.BitString => FormatBits((DlmsBitString)value.Value!),
        DlmsType.Float32 => ((float)value.Value!).ToString("R", CultureInfo.InvariantCulture),
        DlmsType.Float64 => ((double)value.Value!).ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? ""
    };

    private static object ToJson(DlmsValue value) => new
    {
        type = value.Type.ToString(),
        value = value.Type is DlmsType.Array or DlmsType.Structure
            ? (object)value.Items.Select(ToJson).ToArray() : Format(value)
    };

    private static string FormatBits(DlmsBitString bits) =>
        new(Enumerable.Range(0, bits.BitCount).Select(i =>
            (bits.Bytes[i / 8] & (0x80 >> (i % 8))) != 0 ? '1' : '0').ToArray());
}
