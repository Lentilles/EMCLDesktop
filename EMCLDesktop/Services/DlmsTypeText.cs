using EMCL.Core.Values;

namespace EMCLDesktop.Services;

internal static class DlmsTypeText
{
    public static DlmsType? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        string name = text.Split(' ', 2)[0];
        if (!Enum.TryParse<DlmsType>(name, ignoreCase: true, out var type) || !Enum.IsDefined(type))
            return null;
        // Accept names and the name/tag form saved after reading a value, never arbitrary labels or tags.
        return text.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase) ||
               text.Equals(Format(type), StringComparison.OrdinalIgnoreCase) ? type : null;
    }

    public static string Format(DlmsType type) => $"{type} ({(byte)type})";
}
