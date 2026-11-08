using System.Globalization;
using System.Text;
using EMCL.Core.Values;
using DateFields = EMCL.Core.Values.CosemDateTimeFields;

namespace EMCLDesktop.Services;

public sealed class OctetStringFormat
{
    private static readonly string[] weekdays = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];
    private readonly Encoding? encoding;

    private OctetStringFormat(string label, Encoding? encoding = null)
    {
        Label = label;
        this.encoding = encoding;
    }

    public string Label { get; }
    public bool CanParse => this != DlmsDateTime;
    public static OctetStringFormat Hex { get; } = new("HEX");
    public static OctetStringFormat DlmsDateTime { get; } = new("DLMS date-time");
    public static IReadOnlyList<OctetStringFormat> Options { get; } =
    [
        Hex,
        new("Windows-1251", CodePagesEncodingProvider.Instance.GetEncoding(1251,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!),
        new("UTF-8", new UTF8Encoding(false, true)),
        new("ASCII", Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback))
    ];
    public static IReadOnlyList<OctetStringFormat> ReadOptions { get; } = [.. Options, DlmsDateTime];

    public byte[] Parse(string text)
    {
        if (!CanParse) throw new FormatException("DLMS date-time доступен для просмотра. Для ввода байтов используйте HEX.");
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
        if (this == DlmsDateTime) return FormatDateTime(bytes);
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

    private static string? FormatDateTime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != DlmsValueSizes.DateTime) return null;

        var dateTime = new CosemDateTime(bytes);
        int year = (bytes[DateFields.YearHigh] << 8) | bytes[DateFields.YearLow];
        byte month = bytes[DateFields.Month];
        byte day = bytes[DateFields.Day];
        byte weekday = bytes[DateFields.DayOfWeek];
        byte hour = bytes[DateFields.Hour];
        byte minute = bytes[DateFields.Minute];
        byte second = bytes[DateFields.Second];
        byte hundredths = bytes[DateFields.Hundredths];
        short deviation = dateTime.Deviation;
        if (month is not (>= 1 and <= 12 or >= 0xFD) || day is not (>= 1 and <= 31 or >= 0xFD) ||
            weekday is not (>= 1 and <= 7 or DateFields.Unspecified) ||
            hour is > 23 and < DateFields.Unspecified || minute is > 59 and < DateFields.Unspecified ||
            second is > 59 and < DateFields.Unspecified || hundredths is > 99 and < DateFields.Unspecified ||
            deviation is not (>= -720 and <= 720 or DateFields.UnspecifiedDeviation))
            return null;

        int calendarYear = year == ushort.MaxValue ? 2000 : 2000 + year % 400;
        if (month <= 12 && day <= 31 && day > DateTime.DaysInMonth(calendarYear, month)) return null;

        string yearText = year == ushort.MaxValue ? "****" : year.ToString("D4", CultureInfo.InvariantCulture);
        string monthText = month switch
        {
            0xFD => "[конец летнего времени]",
            0xFE => "[начало летнего времени]",
            _ => DateTimeField(month)
        };
        string dayText = day switch
        {
            0xFD => "[предпоследний день]",
            0xFE => "[последний день]",
            _ => DateTimeField(day)
        };
        string fraction = hundredths == DateFields.Unspecified ? "" : $".{DateTimeField(hundredths)}";
        string dayOfWeek = weekday == DateFields.Unspecified ? "" : $" · {weekdays[weekday - 1]}";
        int offset = Math.Abs((int)deviation);
        string offsetText = deviation == DateFields.UnspecifiedDeviation ? "смещение UTC не задано"
            : FormattableString.Invariant($"UTC{(deviation > 0 ? '-' : '+')}{offset / 60:00}:{offset % 60:00}");
        return FormattableString.Invariant(
            $"{yearText}-{monthText}-{dayText} {DateTimeField(hour)}:{DateTimeField(minute)}:{DateTimeField(second)}{fraction}{dayOfWeek} · {offsetText} · статус 0x{dateTime.Status:X2}");
    }

    private static string DateTimeField(byte value) =>
        value == DateFields.Unspecified ? "**" : value.ToString("D2", CultureInfo.InvariantCulture);

    private static bool IsDisplayable(string text) =>
        !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    internal static byte[] ParseHex(string text) =>
        Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    public override string ToString() => Label;
}
