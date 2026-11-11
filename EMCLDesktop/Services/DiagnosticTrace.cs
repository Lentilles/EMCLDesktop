using System.Buffers;
using System.Text;
using EMCL.Core.Codecs;
using EMCL.Framing;
using EMCL.Framing.Hdlc.Frames;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Transport.Channels;

namespace EMCLDesktop.Services;

public enum TraceDisplayFormat
{
    Verbal,
    VerbalWithHex,
    HexOnly
}

public sealed record TraceCapture(long Id, int SessionId, DateTimeOffset Timestamp, string Transport,
    string Direction, string Framing, ReadOnlyMemory<byte> Bytes);

/// <summary>Captures the exchange independently of its display format.</summary>
public sealed class DiagnosticTrace
{
    private const int BytesPerLine = 64;
    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private int displayFormat;
    private long captureId;
    private long clearedThroughCaptureId;
    private int sessionId;
    private string framing = "Auto";

    private sealed record Entry(DateTimeOffset Timestamp, string? Message,
        string? Direction = null, byte[]? Bytes = null, string Transport = "TCP",
        long CaptureId = 0, int SessionId = 0, string Framing = "Auto");

    public event Action? Changed;
    public int CurrentSessionId { get { lock (gate) return sessionId; } }
    internal long ClearedThroughCaptureId { get { lock (gate) return clearedThroughCaptureId; } }

    public TraceDisplayFormat DisplayFormat
    {
        get => (TraceDisplayFormat)Volatile.Read(ref displayFormat);
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref displayFormat, (int)value);
            NotifyChanged();
        }
    }

    public void BeginConnection(string framing)
    {
        lock (gate)
        {
            sessionId++;
            this.framing = framing;
            entries.Add(new(DateTimeOffset.Now, $"Начало нового подключения ({framing})"));
        }
        NotifyChanged();
    }

    // Raw captures are independent of display/export settings; decoding never runs on transport callbacks.
    public IReadOnlyList<TraceCapture> CapturesAfter(long afterId)
    {
        lock (gate)
            return entries.Where(e => e.Bytes is not null && e.CaptureId > afterId)
                .Select(e => new TraceCapture(e.CaptureId, e.SessionId, e.Timestamp, e.Transport,
                    e.Direction!, e.Framing, e.Bytes!)).ToArray();
    }

    public void Add(string message)
    {
        lock (gate) entries.Add(new(DateTimeOffset.Now, message));
        NotifyChanged();
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            clearedThroughCaptureId = captureId;
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* A diagnostic display must not interrupt protocol exchange. */ }
    }

    public string Snapshot()
    {
        Entry[] snapshot;
        lock (gate) snapshot = entries.ToArray();
        var format = DisplayFormat;
        var output = new StringBuilder();
        foreach (var entry in snapshot)
        {
            if (entry.Bytes is null)
            {
                if (format != TraceDisplayFormat.HexOnly)
                    output.AppendLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {entry.Message}");
                continue;
            }
            if (format == TraceDisplayFormat.Verbal) continue;
            var bytes = entry.Bytes;
            for (int offset = 0; offset < bytes.Length; offset += BytesPerLine)
            {
                if (format == TraceDisplayFormat.HexOnly)
                    output.Append($"{entry.Direction}: ");
                else
                    output.Append($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {entry.Transport} {entry.Direction} HEX {bytes.Length}B +{offset:X6}: ");
                int end = Math.Min(bytes.Length, offset + BytesPerLine);
                for (int i = offset; i < end; i++)
                {
                    if (i != offset) output.Append(' ');
                    output.Append(bytes[i].ToString("X2"));
                }
                output.AppendLine();
            }
        }
        return output.ToString();
    }

    public void LogTcpBytes(string direction, ReadOnlySpan<byte> bytes) => LogBytes("TCP", direction, bytes);

    public void LogBytes(string transport, string direction, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        lock (gate) entries.Add(new(DateTimeOffset.Now, null, direction, bytes.ToArray(), transport,
            ++captureId, sessionId, framing));
        NotifyChanged();
    }

    public void LogFrame(HdlcFrame frame, bool received)
    {
        string kind = frame.Control switch
        {
            HdlcControl.Snrm => "SNRM",
            HdlcControl.Ua => "UA",
            HdlcControl.Dm => "DM",
            HdlcControl.Disc => "DISC",
            HdlcControl.Frmr => "FRMR",
            _ when frame.IsInformation => $"I N(S)={frame.SendSequence} N(R)={frame.ReceiveSequence}",
            _ when (frame.Control & HdlcControl.SupervisoryTypeMask) == HdlcControl.ReceiveReady
                => $"RR N(R)={frame.ReceiveSequence}",
            _ => $"control=0x{frame.Control:X2}"
        };
        Add($"{(received ? "RX" : "TX")} HDLC {kind}, " +
            $"source={frame.Source.Value}/{frame.Source.Length}B, " +
            $"destination={frame.Destination.Value}/{frame.Destination.Length}B, " +
            $"information={frame.Information.Length}B, segmented={frame.Segmented}" +
            UaParameters(frame));
    }

    public void LogHdlcNegotiation(HdlcNegotiationResult result)
    {
        var proposed = result.Proposed;
        var received = result.Received;
        var effective = result.Effective;
        string proposal = proposed is null ? "SNRM без параметров (локально 128 байт)"
            : $"SNRM клиента: MAX TX={proposed.MaximumTransmitInformationBytes}, MAX RX={proposed.MaximumReceiveInformationBytes} байт";
        Add($"HDLC согласование: {proposal}; UA счётчика: MAX TX={received?.MaximumTransmitInformationBytes?.ToString() ?? "не указан"}, " +
            $"MAX RX={received?.MaximumReceiveInformationBytes?.ToString() ?? "не указан"}; " +
            $"итог клиента: MAX TX={effective.MaximumTransmitInformationBytes}, MAX RX={effective.MaximumReceiveInformationBytes} байт, " +
            $"окна TX={effective.TransmitWindowFrames}, RX={effective.ReceiveWindowFrames} кадров.");
    }

    public void LogHdlcLinkEvent(HdlcLinkEvent linkEvent)
    {
        switch (linkEvent)
        {
            case HdlcLinkEvent.ChecksumError checksum:
                Add($"RX HDLC: кадр отброшен из-за ошибки контрольной суммы. {checksum.Error}");
                break;
            case HdlcLinkEvent.RecoveryPoll poll:
                Add($"TX HDLC: запрос RR после таймаута подтверждения I-кадра N(S)={poll.SendSequence}, попытка {poll.Attempt}.");
                break;
            case HdlcLinkEvent.Retransmission retry:
                Add($"TX HDLC: повтор I-кадра N(S)={retry.SendSequence} по ответу RR, попытка {retry.Attempt}.");
                break;
        }
    }

    private static string UaParameters(HdlcFrame frame)
    {
        if (frame.Control != HdlcControl.Ua || frame.Information.IsDefaultOrEmpty)
            return "";

        var data = frame.Information.AsSpan();
        if (data.Length < 3 || data[0] != 0x81 || data[1] != 0x80 ||
            data[2] != data.Length - 3)
            return ", UA parameters=unrecognized";

        var values = new List<string>();
        for (int offset = 3; offset < data.Length;)
        {
            if (data.Length - offset < 2)
                return ", UA parameters=invalid";
            byte id = data[offset++];
            int length = data[offset++];
            if (length is < 1 or > 4 || length > data.Length - offset)
                return ", UA parameters=invalid";

            uint value = 0;
            for (int i = 0; i < length; i++)
                value = value << 8 | data[offset++];
            string name = id switch
            {
                0x05 => "maxTx",
                0x06 => "maxRx",
                0x07 => "windowTx",
                0x08 => "windowRx",
                _ => $"parameter0x{id:X2}"
            };
            values.Add($"{name}={value}");
        }
        return ", UA parameters: " + string.Join(", ", values);
    }

    public void LogApdu(string direction, ReadOnlyMemory<byte> apdu)
    {
        var bytes = apdu.Span;
        string kind = bytes.IsEmpty ? "Empty" : Enum.GetName((AcseKind)bytes[0])
            ?? Enum.GetName((ApduTag)bytes[0]) ?? $"tag=0x{bytes[0]:X2}";
        string details = "";
        if (!bytes.IsEmpty && bytes[0] == (byte)AcseKind.Aare)
        {
            try
            {
                var decoded = new AcseCodec(new ProtocolLimits()).Decode(new ReadOnlySequence<byte>(apdu));
                if (decoded.Status == DecodeStatus.Done && decoded.Value is { } response)
                {
                    var diagnostic = response.Diagnostic;
                    var source = diagnostic?.SourceTag switch
                    {
                        AcseFieldTags.DiagnosticServiceUser => "user",
                        AcseFieldTags.DiagnosticServiceProvider => "provider",
                        _ => "unknown"
                    };
                    var reason = diagnostic?.SourceTag switch
                    {
                        AcseFieldTags.DiagnosticServiceUser =>
                            Enum.GetName((AcseServiceUserDiagnostic)diagnostic.Code),
                        AcseFieldTags.DiagnosticServiceProvider =>
                            Enum.GetName((AcseServiceProviderDiagnostic)diagnostic.Code),
                        _ => null
                    };
                    details = $", result={response.Result?.ToString() ?? "missing"}, " +
                              $"diagnostic={diagnostic?.Code.ToString() ?? "missing"} " +
                              $"({source}:{reason ?? "unknown"})";
                }
            }
            catch (Exception)
            {
                details = ", AARE decode failed";
            }
        }
        Add($"{direction} APDU {kind}, length={bytes.Length}B{details}");
    }
}

internal sealed class TracingApduChannel(IApduChannel inner, DiagnosticTrace trace)
    : IApduChannel
{
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var apdu = await inner.ReceiveAsync(cancellationToken);
        if (apdu.HasValue) trace.LogApdu("RX", apdu.Value);
        return apdu;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken = default)
    {
        trace.LogApdu("TX", apdu);
        await inner.SendAsync(apdu, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
