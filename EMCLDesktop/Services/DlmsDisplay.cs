using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EMCL.Core.Access;
using EMCL.Core.Codecs;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;

namespace EMCLDesktop.Services;

internal static class DlmsDisplay
{
    private static readonly AxdrCodec numericCodec = new();

    public static string AttributeAccess(AttributeAccess? access, byte version)
    {
        if (access is null) return "Не указан ассоциацией";
        if (version == 1)
            return $"{(Enum.IsDefined((AttributeAccessModeV1)access.RawMode) ? ((AttributeAccessModeV1)access.RawMode).ToString() : "Неизвестный")} (код {access.RawMode})" +
                Selectors(access.Selectors);
        var flags = (AccessModeV3)access.RawMode;
        var names = new List<string>();
        if (flags.HasFlag(AccessModeV3.Read)) names.Add("чтение");
        if (flags.HasFlag(AccessModeV3.Write)) names.Add("запись");
        AddProtection(names, flags);
        return $"{(names.Count == 0 ? "нет доступа" : string.Join(", ", names))} (0x{access.RawMode:X2})" +
            Selectors(access.Selectors);
    }

    public static string MethodAccess(MethodAccess? access, byte version)
    {
        if (access is null) return "Не указан ассоциацией";
        if (version == 1)
            return $"{(Enum.IsDefined((MethodAccessModeV1)access.RawMode) ? ((MethodAccessModeV1)access.RawMode).ToString() : "Неизвестный")} (код {access.RawMode})";
        var flags = (AccessModeV3)access.RawMode;
        var names = new List<string>();
        if (flags.HasFlag(AccessModeV3.Invoke)) names.Add("вызов");
        AddProtection(names, flags);
        return $"{(names.Count == 0 ? "нет доступа" : string.Join(", ", names))} (0x{access.RawMode:X2})";
    }

    private static string Selectors(ImmutableArray<byte> selectors) =>
        selectors.IsDefaultOrEmpty ? "" : $"; селекторы: {string.Join(", ", selectors)}";

    private static void AddProtection(List<string> names, AccessModeV3 flags)
    {
        if (flags.HasFlag(AccessModeV3.AuthenticatedRequest)) names.Add("аутентификация запроса");
        if (flags.HasFlag(AccessModeV3.EncryptedRequest)) names.Add("шифрование запроса");
        if (flags.HasFlag(AccessModeV3.SignedRequest)) names.Add("подпись запроса");
        if (flags.HasFlag(AccessModeV3.AuthenticatedResponse)) names.Add("аутентификация ответа");
        if (flags.HasFlag(AccessModeV3.EncryptedResponse)) names.Add("шифрование ответа");
        if (flags.HasFlag(AccessModeV3.SignedResponse)) names.Add("подпись ответа");
    }

    public static string Value(DlmsValue value)
    {
        var output = new StringBuilder();
        int shown = 0;
        AppendValue(output, value, 0, null, ref shown);
        return output.ToString();
    }

    internal static string Collection(DlmsType type, ImmutableArray<DlmsValue> items)
    {
        var output = new StringBuilder();
        int shown = 1;
        AppendCollection(output, type, items, 0, ref shown);
        return output.ToString();
    }

    internal static ImmutableArray<DlmsValue> CollectionItems(DlmsValue value) => value.Type switch
    {
        DlmsType.Array or DlmsType.Structure => value.Items,
        DlmsType.CompactArray => ((DlmsCompactArray)value.Value!).Rows,
        _ => default
    };

    internal static string CollectionTitle(DlmsType type, int count) => $"{type} ({count} {ElementWord(count)})";

    internal static bool IsNumeric(DlmsType? type) => type is
        DlmsType.Int8 or DlmsType.Int16 or DlmsType.Int32 or DlmsType.Int64 or
        DlmsType.UInt8 or DlmsType.UInt16 or DlmsType.UInt32 or DlmsType.UInt64 or
        DlmsType.DeltaInt8 or DlmsType.DeltaInt16 or DlmsType.DeltaInt32 or
        DlmsType.DeltaUInt8 or DlmsType.DeltaUInt16 or DlmsType.DeltaUInt32 or
        DlmsType.Float32 or DlmsType.Float64 or DlmsType.Bcd or DlmsType.Enum;

    // Skip the A-XDR tag and keep the payload's width, byte order and bit pattern.
    internal static string NumericHex(DlmsValue value) =>
        $"0x{Convert.ToHexString(numericCodec.EncodeBytes(value).AsSpan(1))}";

    internal static string OctetPreview(DlmsValue octets, OctetStringFormat? format, string hex)
    {
        if (format is null || format == OctetStringFormat.Hex) return hex;
        if (format.Format(octets.Bytes.AsSpan()) is not { } text)
            return format == OctetStringFormat.DlmsDateTime
                ? $"Данные не соответствуют формату DLMS date-time. HEX\n{hex}"
                : $"{format}: значение не представимо как текст. HEX\n{hex}";

        const int previewLength = 256;
        if (text.Length <= previewLength) return text;
        int end = char.IsHighSurrogate(text[previewLength - 1]) ? previewLength - 1 : previewLength;
        return $"{text[..end]}… ({octets.Bytes.Length} байт)";
    }

    private const int MaxDisplayedValues = 5000;

    private static void AppendValue(StringBuilder output, DlmsValue value, int depth,
        int? index, ref int shown)
    {
        if (output.Length > 0) output.AppendLine();
        output.Append(' ', depth * 2);
        if (index is not null) output.Append('[').Append(index.Value).Append("] ");
        if (++shown > MaxDisplayedValues)
        {
            output.Append("… показано первых ").Append(MaxDisplayedValues).Append(" значений");
            return;
        }

        var items = CollectionItems(value);
        if (!items.IsDefault)
        {
            AppendCollection(output, value.Type, items, depth, ref shown);
            return;
        }

        if (depth > 0) output.Append(value.Type).Append(": ");
        if (value.Type == DlmsType.Null)
            output.Append("null");
        else if (value.Value is ImmutableArray<byte> bytes)
        {
            var displayed = bytes.Take(64).ToArray();
            output.Append(Convert.ToHexString(displayed));
            if (bytes.Length > displayed.Length)
                output.Append("… (").Append(bytes.Length).Append(" байт)");
        }
        else
            output.Append(Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? "null");
    }

    private static void AppendCollection(StringBuilder output, DlmsType type, ImmutableArray<DlmsValue> items,
        int depth, ref int shown)
    {
        const int maxDepth = 64;
        output.Append(CollectionTitle(type, items.Length));
        if (depth >= maxDepth)
        {
            output.Append(" · достигнута максимальная глубина отображения");
            return;
        }
        int itemIndex = 0;
        for (; itemIndex < items.Length && shown < MaxDisplayedValues; itemIndex++)
            AppendValue(output, items[itemIndex], depth + 1, itemIndex, ref shown);
        if (itemIndex < items.Length)
            output.AppendLine().Append(' ', (depth + 1) * 2)
                .Append("… достигнут предел отображения ").Append(MaxDisplayedValues).Append(" значений");
    }

    private static string ElementWord(int count) =>
        count % 100 is >= 11 and <= 14 ? "элементов" : (count % 10) switch
        {
            1 => "элемент",
            >= 2 and <= 4 => "элемента",
            _ => "элементов"
        };
}
