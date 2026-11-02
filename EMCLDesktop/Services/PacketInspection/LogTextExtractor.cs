using System.Globalization;
using System.Text.RegularExpressions;

namespace EMCLDesktop.Services.PacketInspection;

/// <summary>Extracts byte runs, never interpreting timestamps, byte counts or HEX offsets as payload.</summary>
public static partial class LogTextExtractor
{
    public const int MaxTextCharacters = 16 * 1024 * 1024;

    [GeneratedRegex(@"\b(TX|RX|SEND|SENT|RECV|RECEIVE|RECEIVED|OUT|IN)\b|(--?>)|(<--?)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectionPattern();
    [GeneratedRegex(@"\bHEX\s+\d+B\s+\+[0-9A-F]+:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TraceHeader();
    [GeneratedRegex(@"(?<![\da-z])(?:0x)?[0-9a-f]{2}(?:(?:[\s,;:\-]+)(?:0x)?[0-9a-f]{2})*(?![\da-z])|(?<![\da-z])(?:[0-9a-f]{2}){4,}(?![\da-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ByteRun();
    [GeneratedRegex(@"(?:0x)?([0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ByteToken();
    [GeneratedRegex(@"^\s*(?:\+?[0-9a-f]{4,8})\s*:\s+", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPrefix();
    [GeneratedRegex(@"\b\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?\b")]
    private static partial Regex TimePattern();

    public static IReadOnlyList<InspectionInput> Extract(string text, out int ignoredLines)
    {
        if (text.Length > MaxTextCharacters)
            throw new FormatException("Лог слишком большой: вставьте до 16 миллионов символов за один раз.");
        var inputs = new List<InspectionInput>();
        ignoredLines = 0;
        int lineNumber = 0, session = 0;
        string previousDirection = "?";
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Contains("Начало нового подключения", StringComparison.OrdinalIgnoreCase))
            {
                session++;
                previousDirection = "?";
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var directions = DirectionPattern().Matches(line);
            bool found = false;
            // A logger can put a request and response on the same line.
            int parts = Math.Max(1, directions.Count);
            for (int part = 0; part < parts; part++)
            {
                var directionMatch = directions.Count > 0 ? directions[part] : null;
                int start = directionMatch?.Index + directionMatch?.Length ?? 0;
                int end = part + 1 < directions.Count ? directions[part + 1].Index : line.Length;
                string fragment = line[start..end];
                string direction = directionMatch is null ? previousDirection : NormalizeDirection(directionMatch.Value);
                var header = TraceHeader().Match(fragment);
                if (header.Success) fragment = fragment[(header.Index + header.Length)..];
                else fragment = OffsetPrefix().Replace(fragment, "");
                int ascii = fragment.IndexOf('|');
                if (ascii >= 0) fragment = fragment[..ascii];
                var runs = ByteRun().Matches(fragment).Cast<Match>()
                    .Select(match => (Match: match, Bytes: DecodeHex(match.Value)))
                    .OrderByDescending(run => run.Bytes.Length).ToArray();
                if (runs.Length == 0) continue;
                var run = runs[0];
                string outside = fragment.Remove(run.Match.Index, run.Match.Length).Trim(' ', '\t', ':', '[', ']', '<', '>', '=');
                bool pure = outside.Length == 0;
                // A short run inside prose is commonly a date, count, address or checksum.
                if (!pure && run.Bytes.Length < 4 && !header.Success) continue;
                if (!pure && directionMatch is null && run.Bytes.Length < 6) continue;
                bool explicitApdu = fragment[..run.Match.Index].Contains("APDU", StringComparison.OrdinalIgnoreCase);
                string framing = explicitApdu ? "APDU" : "Auto";
                inputs.Add(new(run.Bytes, direction, framing, TimePattern().Match(line).Value,
                    $"Строка {lineNumber}", line, session, explicitApdu));
                previousDirection = direction;
                found = true;
            }
            if (!found) ignoredLines++;
        }
        return inputs;
    }

    private static string NormalizeDirection(string value) => value.ToUpperInvariant() switch
    {
        "TX" or "SEND" or "SENT" or "OUT" or "->" or "-->" => "TX",
        _ => "RX"
    };

    private static byte[] DecodeHex(string text)
    {
        if (!text.Contains("0x", StringComparison.OrdinalIgnoreCase) && text.All(Uri.IsHexDigit))
            return Convert.FromHexString(text);
        return ByteToken().Matches(text).Select(match => byte.Parse(match.Groups[1].Value,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
    }
}
