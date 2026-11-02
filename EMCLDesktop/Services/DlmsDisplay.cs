using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EMCL.Core.Access;
using EMCL.Core.Values;

namespace EMCLDesktop.Services;

internal static class DlmsDisplay
{
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

    private static void AppendValue(StringBuilder output, DlmsValue value, int depth,
        int? index, ref int shown)
    {
        const int maxValues = 5000;
        const int maxDepth = 64;
        if (output.Length > 0) output.AppendLine();
        output.Append(' ', depth * 2);
        if (index is not null) output.Append('[').Append(index.Value).Append("] ");
        if (++shown > maxValues)
        {
            output.Append("… показано первых ").Append(maxValues).Append(" значений");
            return;
        }

        if (value.Type is DlmsType.Array or DlmsType.Structure or DlmsType.CompactArray)
        {
            var items = value.Type == DlmsType.CompactArray
                ? ((DlmsCompactArray)value.Value!).Rows
                : value.Items;
            output.Append(value.Type).Append(" (").Append(items.Length).Append(' ')
                .Append(ElementWord(items.Length)).Append(')');
            if (depth >= maxDepth)
            {
                output.Append(" · достигнута максимальная глубина отображения");
                return;
            }
            int itemIndex = 0;
            for (; itemIndex < items.Length && shown < maxValues; itemIndex++)
                AppendValue(output, items[itemIndex], depth + 1, itemIndex, ref shown);
            if (itemIndex < items.Length)
                output.AppendLine().Append(' ', (depth + 1) * 2)
                    .Append("… достигнут предел отображения ").Append(maxValues).Append(" значений");
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

    private static string ElementWord(int count) =>
        count % 100 is >= 11 and <= 14 ? "элементов" : (count % 10) switch
        {
            1 => "элемент",
            >= 2 and <= 4 => "элемента",
            _ => "элементов"
        };
}
