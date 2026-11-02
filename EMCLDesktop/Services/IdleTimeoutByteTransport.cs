using EMCL.Transport.Streams;

namespace EMCLDesktop.Services;

/// <summary>Limits each pending byte operation to the configured inactivity interval.</summary>
internal sealed class IdleTimeoutByteTransportFactory(
    IByteTransportFactory inner, TimeSpan idleTimeout, DiagnosticTrace? trace = null, string transportName = "TCP") : IByteTransportFactory
{
    public async ValueTask<IByteTransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var transport = await inner.ConnectAsync(cancellationToken);
        trace?.Add($"{transportName}-соединение установлено");
        return new IdleTimeoutByteTransport(transport, idleTimeout, trace, transportName);
    }
}

internal sealed class IdleTimeoutByteTransport : IByteTransport
{
    private readonly IByteTransport inner;
    private readonly TimeSpan idleTimeout;
    private readonly DiagnosticTrace? trace;
    private readonly string transportName;
    private int disposed;

    public IdleTimeoutByteTransport(IByteTransport inner, TimeSpan idleTimeout, DiagnosticTrace? trace = null,
        string transportName = "TCP")
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        this.inner = inner;
        this.idleTimeout = idleTimeout;
        this.trace = trace;
        this.transportName = transportName;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(idleTimeout);
        try
        {
            int received = await inner.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            trace?.Add(received == 0 ? $"{transportName} RX: соединение закрыто" : $"{transportName} RX: {received} байт");
            if (received > 0) trace?.LogBytes(transportName, "RX", buffer.Span[..received]);
            return received;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            trace?.Add($"{transportName} RX: нет байтов за {idleTimeout.TotalSeconds:g} с");
            throw new TimeoutException("Нет обмена данными со счётчиком за заданный интервал.");
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(idleTimeout);
        try
        {
            // Capture the attempted write before handing the buffer to the transport.
            // A failing stream can have sent a prefix without reporting its length.
            trace?.Add($"{transportName} TX: попытка передачи {buffer.Length} байт");
            trace?.LogBytes(transportName, "TX", buffer.Span);
            await inner.WriteAsync(buffer, deadline.Token).ConfigureAwait(false);
            trace?.Add($"{transportName} TX: запись {buffer.Length} байт завершена");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            trace?.Add($"{transportName} TX: таймаут передачи за {idleTimeout.TotalSeconds:g} с");
            throw new TimeoutException("Нет обмена данными со счётчиком за заданный интервал.");
        }
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref disposed, 1) == 0 ? inner.DisposeAsync() : ValueTask.CompletedTask;
}
