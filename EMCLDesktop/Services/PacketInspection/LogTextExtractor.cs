using System.Text.RegularExpressions;

namespace EMCLDesktop.Services.PacketInspection;

/// <summary>Finds HEX runs in source order. Packet boundaries are determined by the byte parser.</summary>
internal static partial class LogTextExtractor
{
    public const int MaxTextCharacters = 16 * 1024 * 1024;

    [GeneratedRegex(@"\bHEX\s+\d+B\s+\+[0-9A-F]+:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TraceHeader();
    [GeneratedRegex(@"(?<![\w])(?:0x[0-9a-f]{2}|(?:[0-9a-f]{2})+)(?![\w])(?:[ \t,;:\-]+(?:0x[0-9a-f]{2}|(?:[0-9a-f]{2})+)(?![\w]))*", RegexOptions.IgnoreCase)]
    private static partial Regex ByteRun();
    [GeneratedRegex(@"^\s*\+?[0-9a-f]{4,8}\s*:\s+", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPrefix();
    [GeneratedRegex(@"(?<![\w:])(?:(?:\d{4}-\d{2}-\d{2}|\d{2}[./]\d{2}[./]\d{4})[ T])?\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:\s?[+-]\d{2}:\d{2}|Z)?(?![\w:])")]
    private static partial Regex TimePattern();
    [GeneratedRegex(@"(?<=[0-9a-f]{2})\s+\|[^|]*\|\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AsciiColumn();

    public static IEnumerable<(InspectionInput[] Runs, bool IsByteDump)> ReadLines(string text, CancellationToken cancellationToken)
    {
        if (text.Length > MaxTextCharacters)
            throw new FormatException("Лог слишком большой: вставьте до 16 миллионов символов за один раз.");
        int lineNumber = 0, session = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var timestamp = TimePattern().Match(line);
            string data = TimePattern().Replace(line, " | ");
            string message = data.Trim(' ', '\t', '|');
            if (message.StartsWith("Начало нового подключения (", StringComparison.OrdinalIgnoreCase) && message.EndsWith(')'))
            {
                session++;
                continue;
            }
            data = AsciiColumn().Replace(data, "");
            data = TraceHeader().Replace(data, " | ");
            data = OffsetPrefix().Replace(data, "");

            var runs = ByteRun().Matches(data);
            var inputs = new InspectionInput[runs.Count];
            bool isByteDump = false;
            for (int i = 0; i < runs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var run = runs[i];
                inputs[i] = new(DecodeHex(run.Value), Timestamp: timestamp.Value, Origin: $"Строка {lineNumber}",
                    SourceText: line, SessionId: session);
                if (runs.Count == 1)
                {
                    var prefix = data[..run.Index].TrimEnd();
                    var suffix = data[(run.Index + run.Length)..].Trim(' ', '\t', ',', ';', ']', '>', ')', '|', '"');
                    isByteDump = suffix.Length == 0 && (prefix.Trim(' ', '\t', '|').Length == 0 ||
                        prefix[^1] is ':' or '[' or '>' or '(' or '=' or '"' or '|');
                }
            }
            yield return (inputs, isByteDump);
        }
    }

    private static byte[] DecodeHex(string text)
    {
        if (text.All(Uri.IsHexDigit)) return Convert.FromHexString(text);

        var bytes = new List<byte>();
        for (int i = 0; i < text.Length;)
        {
            if (!Uri.IsHexDigit(text[i])) { i++; continue; }
            if (text[i] == '0' && text[i + 1] is 'x' or 'X') i += 2;
            bytes.Add((byte)((Uri.FromHex(text[i]) << 4) | Uri.FromHex(text[i + 1])));
            i += 2;
        }
        return bytes.ToArray();
    }
}
