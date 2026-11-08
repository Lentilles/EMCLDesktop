using System.Text;

namespace EMCLDesktop.Services;

public sealed class OctetStringFormat
{
    private readonly Encoding? encoding;

    private OctetStringFormat(string label, Encoding? encoding = null)
    {
        Label = label;
        this.encoding = encoding;
    }

    public string Label { get; }
    public static OctetStringFormat Hex { get; } = new("HEX");
    public static IReadOnlyList<OctetStringFormat> Options { get; } =
    [
        Hex,
        new("Windows-1251", CodePagesEncodingProvider.Instance.GetEncoding(1251,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!),
        new("UTF-8", new UTF8Encoding(false, true)),
        new("ASCII", Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback))
    ];

    public byte[] Parse(string text)
    {
        if (encoding is null) return ParseHex(text);
        try
        {
            if (!IsDisplayable(text))
                throw new FormatException("Для управляющих байтов используйте HEX.");
            return encoding.GetBytes(text);
        }
        catch (EncoderFallbackException error)
        {
            throw new FormatException($"Текст содержит символы вне кодировки {Label}.", error);
        }
    }

    public string? Format(ReadOnlySpan<byte> bytes)
    {
        if (encoding is null) return Convert.ToHexString(bytes);

        try
        {
            string text = encoding.GetString(bytes);
            return IsDisplayable(text) && bytes.SequenceEqual(encoding.GetBytes(text)) ? text : null;
        }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsDisplayable(string text) =>
        !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    internal static byte[] ParseHex(string text) =>
        Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    public override string ToString() => Label;
}
