using System.Text.RegularExpressions;
using EMCLDesktop.Services;
using EMCL.Framing;
using EMCL.Transport.Streams;
using Xunit;

namespace EMCLDesktop.Tests;

public class DiagnosticTraceTests
{
    private static readonly byte[] Tx = [0x60, 9, 0xAC, 7, 0x80, 5, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5];
    private static readonly byte[] Rx = [0xFF, 0xDB, 0xAA, 0x00, 0x7E];

    [Fact]
    public void Snapshot_DefaultFormat_ShowsDiagnosticsWithoutHex()
    {
        // Arrange
        var trace = CreateTrace();

        // Act
        var snapshot = trace.Snapshot();

        // Assert
        Assert.Equal(TraceDisplayFormat.Verbal, trace.DisplayFormat);
        Assert.Contains("Описание обмена", snapshot);
        Assert.True(Lines(trace).Any(s => s.Contains("ошибки контрольной суммы") && s.Contains("Invalid HDLC FCS")), "HDLC recovery events lost their reasons, frame sequence or attempt");
        Assert.True(Lines(trace).Any(s => s.Contains("запрос RR после таймаута") && s.Contains("N(S)=3, попытка 1")), "HDLC recovery events lost their reasons, frame sequence or attempt");
        Assert.True(Lines(trace).Any(s => s.Contains("повтор I-кадра N(S)=3 по ответу RR, попытка 1")), "HDLC recovery events lost their reasons, frame sequence or attempt");
        Assert.DoesNotContain(" HEX ", snapshot);
        Assert.Empty(Tokens(trace));
    }

    [Fact]
    public void DisplayFormat_VerbalWithHex_CombinesDiagnosticsAndUnmaskedBytesInOrder()
    {
        // Arrange
        var trace = CreateTrace();

        // Act
        trace.DisplayFormat = TraceDisplayFormat.VerbalWithHex;

        // Assert
        Assert.Contains("Описание обмена", trace.Snapshot());
        Assert.Contains("TCP TX HEX 11B +000000:", trace.Snapshot());
        Assert.Equal(Hex([.. Tx, .. Rx]), Tokens(trace));
        Assert.DoesNotContain("**", trace.Snapshot());
        var mixed = Lines(trace);
        Assert.True(Array.FindIndex(mixed, s => s.Contains("Описание обмена")) < Array.FindIndex(mixed, s => s.Contains("TCP TX HEX")), "Mixed mode reordered messages");
        Assert.True(Array.FindIndex(mixed, s => s.Contains("TCP TX HEX")) < Array.FindIndex(mixed, s => s.Contains("Получен ответ")), "Mixed mode reordered bytes");
    }

    [Fact]
    public void DisplayFormat_HexOnly_ShowsOnlyUnmaskedDirectionalBytes()
    {
        // Arrange
        var trace = CreateTrace();

        // Act
        trace.DisplayFormat = TraceDisplayFormat.HexOnly;

        // Assert
        Assert.Equal(new[] { "TX: " + string.Join(' ', Hex(Tx)), "RX: " + string.Join(' ', Hex(Rx)) }, Lines(trace));
        Assert.True(Lines(trace).All(s => Regex.IsMatch(s, @"^(TX|RX): [0-9A-F]{2}( [0-9A-F]{2})*$")), "Pure HEX has extra text");
        Assert.Equal(Hex([.. Tx, .. Rx]), Tokens(trace));
    }

    [Fact]
    public void DisplayFormat_ChangedBackToVerbal_HidesCapturedBytes()
    {
        // Arrange
        var trace = CreateTrace();
        trace.DisplayFormat = TraceDisplayFormat.VerbalWithHex;

        // Act
        trace.DisplayFormat = TraceDisplayFormat.Verbal;

        // Assert
        Assert.Contains("Описание обмена", trace.Snapshot());
        Assert.Empty(Tokens(trace));
    }

    [Fact]
    public void BeginConnection_NewSession_PreservesPreviousCaptureHistory()
    {
        // Arrange
        var trace = CreateTrace();
        trace.DisplayFormat = TraceDisplayFormat.HexOnly;

        // Act
        trace.BeginConnection("Wrapper");
        trace.LogTcpBytes("TX", [1, 2]);

        // Assert
        Assert.Equal(Hex([.. Tx, .. Rx, 1, 2]), Tokens(trace));
    }

    [Fact]
    public void Clear_ExistingHistory_RemovesEntriesAndNotifiesWithoutChangingFormat()
    {
        // Arrange
        var trace = CreateTrace();
        trace.DisplayFormat = TraceDisplayFormat.HexOnly;
        bool clearNotified = false;
        trace.Changed += () => clearNotified = trace.Snapshot() == "";

        // Act
        trace.Clear();

        // Assert
        Assert.True(clearNotified, "Clearing the trace did not notify the display");
        Assert.Equal(TraceDisplayFormat.HexOnly, trace.DisplayFormat);
        Assert.Equal("", trace.Snapshot());
        Assert.Empty(trace.CapturesAfter(0));
    }

    [Fact]
    public void DisplayFormat_ChangedAfterClear_DoesNotRestoreClearedEntries()
    {
        // Arrange
        var trace = CreateTrace();
        trace.DisplayFormat = TraceDisplayFormat.HexOnly;
        trace.Clear();

        // Act
        trace.DisplayFormat = TraceDisplayFormat.Verbal;

        // Assert
        Assert.Empty(trace.Snapshot());
        Assert.Empty(trace.CapturesAfter(0));
    }

    [Fact]
    public void Clear_DuringConnection_PreservesSessionAndCaptureCursor()
    {
        // Arrange
        var trace = CreateTrace();
        trace.BeginConnection("Wrapper");
        trace.LogTcpBytes("TX", [1, 2]);
        var lastCapture = trace.CapturesAfter(0)[^1];

        // Act
        trace.Clear();
        trace.LogTcpBytes("RX", Rx);

        // Assert
        var nextCapture = Assert.Single(trace.CapturesAfter(lastCapture.Id));
        Assert.True(nextCapture.SessionId == lastCapture.SessionId, "Clearing the trace interrupted capture or changed its connection metadata");
        Assert.True(nextCapture.Framing == lastCapture.Framing, "Clearing the trace interrupted capture or changed its connection metadata");
    }

    [Fact]
    public void LogTcpBytes_LargeChunk_WrapsHexAndPreservesBytes()
    {
        // Arrange
        byte[] large = Enumerable.Range(0, 150).Select(n => (byte)n).ToArray();
        var chunks = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };

        // Act
        chunks.LogTcpBytes("TX", large);

        // Assert
        Assert.Equal(3, Lines(chunks).Length);
        Assert.True(Lines(chunks).All(s => s.StartsWith("TX: ")), "Wrapped line lost direction");
        Assert.Equal(Hex(large), Tokens(chunks));
    }

    [Fact]
    public void DisplayFormat_LargeChunkInVerbalWithHex_ShowsOffsetsAndPreservesBytes()
    {
        // Arrange
        byte[] large = Enumerable.Range(0, 150).Select(n => (byte)n).ToArray();
        var chunks = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        chunks.LogTcpBytes("TX", large);

        // Act
        chunks.DisplayFormat = TraceDisplayFormat.VerbalWithHex;

        // Assert
        Assert.Contains("+000040:", chunks.Snapshot());
        Assert.Contains("+000080:", chunks.Snapshot());
        Assert.Equal(Hex(large), Tokens(chunks));
    }

    [Fact]
    public void LogTcpBytes_SingleByteChunks_PreservesEveryByte()
    {
        // Arrange
        var fragmented = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };

        // Act
        foreach (byte value in Tx) fragmented.LogTcpBytes("TX", [value]);

        // Assert
        Assert.Equal(Hex(Tx), Tokens(fragmented));
    }

    [Fact]
    public void LogTcpBytes_EmptyRead_DoesNotCreateHexLine()
    {
        // Arrange
        var fragmented = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        foreach (byte value in Tx) fragmented.LogTcpBytes("TX", [value]);

        // Act
        fragmented.LogTcpBytes("RX", []);

        // Assert
        Assert.Equal(Tx.Length, Lines(fragmented).Length);
    }

    [Fact]
    public void LogTcpBytes_ReusedBuffer_PreservesCapturedBytes()
    {
        // Arrange
        var ownership = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        var buffer = Tx.ToArray();

        // Act
        ownership.LogTcpBytes("TX", buffer);
        Array.Fill(buffer, (byte)0);

        // Assert
        Assert.Equal(Hex(Tx), Tokens(ownership));
    }

    [Fact]
    public void Add_TraceBeyondFormerSizeLimit_RetainsOldestEntry()
    {
        // Arrange
        var ownership = new DiagnosticTrace();
        ownership.Add("BEGIN-RETENTION");

        // Act
        ownership.Add(new string('x', 2_100_000));

        // Assert
        Assert.Contains("BEGIN-RETENTION", ownership.Snapshot());
    }

    [Fact]
    public void Snapshot_HexOnlyWithNoBytes_OmitsMessages()
    {
        // Arrange
        var empty = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        empty.Add("No bytes");

        // Act
        var snapshot = empty.Snapshot();

        // Assert
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task ReadAsync_BufferLargerThanPayload_CapturesOnlyReceivedBytes()
    {
        // Arrange
        var ioTrace = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        await using var transport = new IdleTimeoutByteTransport(new FakeTransport(Rx), TimeSpan.FromSeconds(1), ioTrace);
        var received = new byte[Rx.Length + 10];

        // Act
        int count = await transport.ReadAsync(received);
        Array.Fill(received, (byte)0);

        // Assert
        Assert.Equal(Rx.Length, count);
        Assert.Equal(Hex(Rx), Tokens(ioTrace));
    }

    [Fact]
    public async Task WriteAsync_FailedWrite_PropagatesErrorAndPreservesAttemptInTrace()
    {
        // Arrange
        var ioTrace = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
        await using var transport = new IdleTimeoutByteTransport(new FakeTransport(Rx) { FailWrite = true }, TimeSpan.FromSeconds(1), ioTrace);

        // Act
        var exception = await Record.ExceptionAsync(() => transport.WriteAsync(Tx).AsTask());

        // Assert
        Assert.IsType<IOException>(exception);
        Assert.Equal(Hex(Tx), Tokens(ioTrace));
        Assert.True(Lines(ioTrace).All(s => s.StartsWith("TX: ") || s.StartsWith("RX: ")), "Pure HEX includes transport descriptions");
        ioTrace.DisplayFormat = TraceDisplayFormat.VerbalWithHex;
        Assert.Contains("попытка передачи", ioTrace.Snapshot());
        Assert.DoesNotContain("запись", ioTrace.Snapshot());
    }

    [Fact]
    public async Task LogTcpBytes_MixedTransports_PreservesTransportLabelsAndBytes()
    {
        // Arrange
        var serialTrace = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.VerbalWithHex };

        // Act
        await using (var transport = new IdleTimeoutByteTransport(new FakeTransport(Rx), TimeSpan.FromSeconds(1), serialTrace, "Serial"))
        {
            await transport.ReadAsync(new byte[Rx.Length]);
            await transport.WriteAsync(Tx);
        }
        serialTrace.LogTcpBytes("TX", [1]);

        // Assert
        Assert.Contains("Serial RX HEX", serialTrace.Snapshot());
        Assert.Contains("Serial TX HEX", serialTrace.Snapshot());
        Assert.Contains("TCP TX HEX", serialTrace.Snapshot());
        Assert.Equal(Hex([.. Rx, .. Tx, 1]), Tokens(serialTrace));
    }

    private static DiagnosticTrace CreateTrace()
    {
        var trace = new DiagnosticTrace();
        trace.BeginConnection("HDLC");
        trace.Add("Описание обмена");
        trace.LogHdlcLinkEvent(new HdlcLinkEvent.ChecksumError("Invalid HDLC FCS."));
        trace.LogHdlcLinkEvent(new HdlcLinkEvent.RecoveryPoll(3, 1));
        trace.LogHdlcLinkEvent(new HdlcLinkEvent.Retransmission(3, 1));
        trace.LogTcpBytes("TX", Tx);
        trace.Add("Получен ответ");
        trace.LogTcpBytes("RX", Rx);
        return trace;
    }

    private static string[] Lines(DiagnosticTrace trace) => trace.Snapshot()
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    private static string[] Tokens(DiagnosticTrace trace) => Lines(trace)
        .Select(line => Regex.Match(line, @"(?: HEX \d+B \+[0-9A-F]+: |^(?:TX|RX): )(.*)"))
        .Where(match => match.Success)
        .SelectMany(match => match.Groups[1].Value.Split(' ')).ToArray();
    private static string[] Hex(byte[] bytes) => bytes.Select(b => b.ToString("X2")).ToArray();
}

sealed class FakeTransport(byte[] received) : IByteTransport
{
    public bool FailWrite { get; set; }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        received.CopyTo(buffer);
        return ValueTask.FromResult(received.Length);
    }
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (FailWrite) throw new IOException("Simulated partial write failure");
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
