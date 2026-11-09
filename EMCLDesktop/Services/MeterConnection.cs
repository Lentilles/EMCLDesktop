using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using EMCL.Client.Association;
using EMCL.Client.Sessions;
using EMCL.Composition;
using EMCL.Core.Access;
using EMCL.Core.Codecs;
using EMCL.Core.Objects;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Framing;
using EMCL.Framing.Hdlc.Frames;
using EMCL.Objects.Association;
using EMCL.Objects.Definitions;
using EMCL.Protocol.Apdu;
using EMCL.Transport.Streams;
using EMCL.Transport.Channels;

namespace EMCLDesktop.Services;

public sealed record MeterConnectionOptions(
    string Host, int TcpPort, string Framing, ushort ClientAddress, ushort ServerAddress,
    ushort? ServerPhysicalAddress, bool ExtendedHdlcAddress,
    byte AssociationVersion, string AssociationObis,
    ClientAuthentication Authentication, string Password, string PasswordFormat,
    double ConnectTimeoutSeconds, double RequestTimeoutSeconds, double KeepAliveIntervalSeconds,
    ushort MaxReceivePduSize,
    DlmsConformance Conformance, bool IgnoreIdenticalObjectListEntries,
    ProtocolLimits Limits, string Transport = "TCP", SerialPortSettings? Serial = null)
{
    internal const double DefaultHdlcAcknowledgementTimeoutSeconds = 1;

    public int HdlcInformationFrameRetryCount { get; init; }
    public double HdlcAcknowledgementTimeoutSeconds { get; init; } = DefaultHdlcAcknowledgementTimeoutSeconds;
    public HdlcLinkParameters? HdlcParameters { get; init; }

    public ApduFraming CreateFraming(DiagnosticTrace? trace = null)
    {
        if (Framing == "Wrapper") return new ApduFraming.Wrapper(ClientAddress, ServerAddress);
        if (Framing != "HDLC") throw new ArgumentException("Выберите Wrapper или HDLC.");
        ValidateHdlcRecovery(HdlcInformationFrameRetryCount, HdlcAcknowledgementTimeoutSeconds, RequestTimeoutSeconds);
        _ = HdlcNegotiation.CreateSnrmInformation(HdlcParameters);
        return new ApduFraming.Hdlc(HdlcAddress.Client(checked((byte)ClientAddress)),
            HdlcAddress.Server(ServerAddress, ServerPhysicalAddress, ExtendedHdlcAddress),
            TimeSpan.FromSeconds(RequestTimeoutSeconds), trace is null ? null : trace.LogFrame)
        {
            LinkParameters = HdlcParameters,
            NegotiationObserver = trace is null ? null : trace.LogHdlcNegotiation,
            InformationFrameRetryCount = HdlcInformationFrameRetryCount,
            AcknowledgementTimeout = TimeSpan.FromSeconds(HdlcAcknowledgementTimeoutSeconds),
            LinkObserver = trace is null ? null : trace.LogHdlcLinkEvent
        };
    }

    public string Endpoint => Transport == "Serial" ? Serial?.PortName ?? "" : $"{Host}:{TcpPort}";

    internal static void ValidateHdlcRecovery(int retryCount, double acknowledgementTimeout, double idleTimeout)
    {
        if (retryCount is < 0 or > 1)
            throw new ArgumentException("Для HDLC выберите 0 или 1 повтор.");
        if (!double.IsFinite(acknowledgementTimeout) || acknowledgementTimeout < 0.001 ||
            acknowledgementTimeout * 1000 > uint.MaxValue - 1)
            throw new ArgumentException("Таймаут подтверждения HDLC должен быть не меньше 0,001 с и поддерживаться .NET.");
        // Leave time for the initial acknowledgement, the RR poll and the replay.
        if (retryCount > 0 && !(3 * acknowledgementTimeout < idleTimeout))
            throw new ArgumentException("При повторах HDLC таймаут «Без обмена» должен превышать три таймаута подтверждения.");
    }

    public IByteTransportFactory CreateTransportFactory()
    {
        var timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds);
        switch (Transport)
        {
            case "TCP":
                if (string.IsNullOrWhiteSpace(Host))
                    throw new ArgumentException("Укажите IP или имя хоста.");
                if (TcpPort is < 1 or > 65535)
                    throw new ArgumentException("TCP-порт должен быть от 1 до 65535.");
                return new TcpByteTransportFactory(Host, TcpPort, timeout);
            case "Serial":
                if (Serial is null || string.IsNullOrWhiteSpace(Serial.PortName))
                    throw new ArgumentException("Укажите имя Serial-порта.");
                Serial.Validate();
                return new SerialByteTransportFactory(Serial, timeout);
            default:
                throw new ArgumentException("Выберите TCP или Serial.");
        }
    }
}

public readonly record struct ObjectListProgress(int Current, int Total);

internal readonly record struct ProfileBufferProgress(int RowsRead, int? TotalRows, int BytesRead, int? TotalBytes);

internal readonly record struct ProfileBufferReadResult(DlmsType Type, ImmutableArray<DlmsValue> Rows);

public sealed class MeterConnection : IAsyncDisposable
{
    // EMCL currently applies RequestTimeout to the entire exchange. The byte transport
    // below enforces the user timeout between actual reads and writes instead.
    private static readonly TimeSpan SessionSafetyDeadline =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private readonly DlmsClientSession session;
    private readonly ClientAuthentication authentication;
    private readonly MeterConnectionOptions options;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource keepAliveStop = new();
    private readonly object disposeSync = new();
    private Task? disposeTask;
    private readonly Task keepAliveTask;
    private readonly LogicalName associationName;
    private readonly TimeSpan keepAliveInterval;
    private long lastActivityTimestamp;
    public ImportedAssociation? Association { get; private set; }
    public bool IsActive => session.State == ClientSessionState.Associated;
    public int SkippedDuplicateEntries { get; private set; }
    public event Action<MeterConnection, Exception>? KeepAliveFailed;

    private MeterConnection(DlmsClientSession session, ClientAuthentication authentication,
        LogicalName associationName, TimeSpan keepAliveInterval, MeterConnectionOptions options)
    {
        this.session = session;
        this.authentication = authentication;
        this.options = options;
        this.associationName = associationName;
        this.keepAliveInterval = keepAliveInterval;
        lastActivityTimestamp = Stopwatch.GetTimestamp();
        keepAliveTask = RunKeepAliveAsync();
    }

    public static async Task<MeterConnection> OpenAsync(MeterConnectionOptions options,
        DiagnosticTrace? trace = null,
        CancellationToken cancellationToken = default)
    {
        if (options.ClientAddress == 0 || options.ServerAddress == 0)
            throw new ArgumentException("Адреса клиента и сервера должны быть больше нуля.");
        if (options.Framing == "HDLC" && options.ExtendedHdlcAddress &&
            options.ServerPhysicalAddress is null)
            throw new ArgumentException("Для расширенного HDLC-адреса нужен физический адрес сервера.");
        if (options.AssociationVersion is not (1 or 3))
            throw new ArgumentException("Версия ассоциации должна быть 1 или 3.");
        if (!double.IsFinite(options.ConnectTimeoutSeconds) ||
            !double.IsFinite(options.RequestTimeoutSeconds) ||
            options.ConnectTimeoutSeconds <= 0 || options.RequestTimeoutSeconds <= 0 ||
            options.ConnectTimeoutSeconds * 1000 > uint.MaxValue - 1 ||
            options.RequestTimeoutSeconds * 1000 > uint.MaxValue - 1)
            throw new ArgumentException("Таймауты должны быть положительными и поддерживаться .NET.");
        if (!double.IsFinite(options.KeepAliveIntervalSeconds) ||
            options.KeepAliveIntervalSeconds < 1 || options.KeepAliveIntervalSeconds > 3600)
            throw new ArgumentException("Интервал KeepAlive должен быть от 1 до 3600 секунд.");
        options.Limits.Validate();
        if ((options.Conformance & DlmsConformance.Get) == 0 ||
            (options.Conformance & ~LnAssociationOptions.SupportedConformance) != 0)
            throw new ArgumentException("Conformance должен включать Get и только поддерживаемые службы.");
        if (options.Authentication == ClientAuthentication.High &&
            (options.Conformance & DlmsConformance.Action) == 0)
            throw new ArgumentException("Для High нужен Action в Conformance.");

        var idleTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        var framing = options.CreateFraming(trace);
        var password = Password(options);
        if (options.Authentication != ClientAuthentication.None && password.IsEmpty)
            throw new ArgumentException("Укажите пароль.");
        if (options.Authentication == ClientAuthentication.High &&
            password.Length != LnAssociationOptions.HighSecretLength)
            throw new ArgumentException("Для High нужен секрет длиной 16 байт.");

        var transportFactory = options.CreateTransportFactory();
        IApduChannel channel = await ApduChannels.ConnectAsync(
            new(new IdleTimeoutByteTransportFactory(transportFactory, idleTimeout, trace, options.Transport),
                framing, options.Limits),
            cancellationToken);
        if (trace is not null)
            channel = new TracingApduChannel(channel, trace);
        DlmsClientSession session;
        try
        {
            session = new DlmsClientSession(channel, new ClientSessionOptions
            {
                Limits = options.Limits,
                RequestTimeout = SessionSafetyDeadline,
                Association = new LnAssociationOptions
                {
                    Authentication = options.Authentication,
                    Password = password,
                    MaxReceivePduSize = options.MaxReceivePduSize,
                    Conformance = options.Conformance
                }
            });
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
        try
        {
            await session.AssociateAsync(cancellationToken);
            var associationName = LogicalName.Parse(options.AssociationObis);
            return new MeterConnection(session, options.Authentication,
                associationName, TimeSpan.FromSeconds(options.KeepAliveIntervalSeconds), options);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static ImmutableArray<byte> Password(MeterConnectionOptions options)
    {
        if (options.Authentication == ClientAuthentication.None)
            return [];
        return options.PasswordFormat switch
        {
            "UTF-8" => Encoding.UTF8.GetBytes(options.Password).ToImmutableArray(),
            "Hex" => Convert.FromHexString(options.Password).ToImmutableArray(),
            "Environment" => Encoding.UTF8.GetBytes(
                Environment.GetEnvironmentVariable(options.Password) ??
                throw new ArgumentException("Переменная окружения с паролем не найдена.")).ToImmutableArray(),
            _ => throw new ArgumentException("Выберите формат пароля.")
        };
    }

    public bool CanRead(AttributeAccess? access)
    {
        if (access is null) return false;
        if (options.AssociationVersion == 1)
        {
            var mode = (AttributeAccessModeV1)access.RawMode;
            return mode is AttributeAccessModeV1.ReadOnly or AttributeAccessModeV1.ReadWrite ||
                authentication != ClientAuthentication.None &&
                mode is AttributeAccessModeV1.AuthenticatedReadOnly or AttributeAccessModeV1.AuthenticatedReadWrite;
        }
        var flags = (AccessModeV3)access.RawMode;
        const AccessModeV3 protectedFlags = AccessModeV3.AuthenticatedRequest |
            AccessModeV3.EncryptedRequest | AccessModeV3.SignedRequest |
            AccessModeV3.AuthenticatedResponse | AccessModeV3.EncryptedResponse |
            AccessModeV3.SignedResponse;
        return flags.HasFlag(AccessModeV3.Read) && (flags & protectedFlags) == 0;
    }

    public static bool AllowsWrite(AttributeAccess? access, byte associationVersion,
        ClientAuthentication authentication)
    {
        if (access is null) return false;
        if (associationVersion == 1)
        {
            var mode = (AttributeAccessModeV1)access.RawMode;
            return mode is AttributeAccessModeV1.WriteOnly or AttributeAccessModeV1.ReadWrite ||
                authentication != ClientAuthentication.None &&
                mode is AttributeAccessModeV1.AuthenticatedWriteOnly or AttributeAccessModeV1.AuthenticatedReadWrite;
        }
        // Every other v3 bit requires message protection that this client cannot send.
        return associationVersion == 3 &&
            ((AccessModeV3)access.RawMode is AccessModeV3.Write or (AccessModeV3.Write | AccessModeV3.Read));
    }

    public string WriteUnavailableReason(ushort classId, LogicalName obis, sbyte index)
    {
        if (!IsActive) return "Подключитесь к счётчику для записи.";
        if ((session.NegotiatedParameters!.Conformance & DlmsConformance.Set) == 0)
            return "SET не согласован со счётчиком. Включите Set в параметрах и подключитесь повторно.";
        if (index == CosemObject.LogicalNameAttributeIndex)
            return "logical_name доступен только для чтения.";
        if (Association is null) return "Считайте дерево текущего соединения, чтобы проверить права записи.";
        if (!Association.Catalog.Objects.Any(obj => obj.LogicalName == obis && obj.Descriptor.Key.ClassId == classId) ||
            !Association.Access.Rights.TryGetValue(obis, out var rights))
            return "Объект отсутствует в дереве текущего соединения.";
        var access = rights.Attributes.FirstOrDefault(item => item.Index == index);
        return AllowsWrite(access, options.AssociationVersion, authentication) ? "" :
            "Ассоциация не разрешает запись либо требует неподдерживаемой защиты сообщений.";
    }

    public bool CanRead(LogicalName obis, sbyte index) =>
        IsActive && (session.NegotiatedParameters!.Conformance & DlmsConformance.Get) != 0 &&
        Association?.Access.Rights.TryGetValue(obis, out var rights) == true &&
        CanRead(rights.Attributes.FirstOrDefault(item => item.Index == index));

    public async Task WriteAsync(ushort classId, LogicalName obis, sbyte index, DlmsValue value,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string reason = WriteUnavailableReason(classId, obis, index);
            if (reason.Length != 0) throw new InvalidOperationException(reason);
            _ = new EMCL.Protocol.Axdr.AxdrCodec(options.Limits).EncodeBytes(value);
            // One SET exchange, using negotiated blocks when needed. No write or block is retried.
            // DlmsWriteUnconfirmedException means the final outcome must be reconciled by reading.
            await session.SetAsync(classId, obis, index, value, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            operationGate.Release();
        }
    }

    public string InvokeUnavailableReason(ushort classId, LogicalName obis, sbyte index)
    {
        if (!IsActive) return "Подключитесь к счётчику для вызова метода.";
        if ((session.NegotiatedParameters!.Conformance & DlmsConformance.Action) == 0)
            return "ACTION не согласован со счётчиком. Проверьте флаг Action в настройках соединения.";
        if (Association is not { } imported)
            return "Считайте дерево текущего соединения, чтобы проверить права вызова.";
        if (!imported.Catalog.Objects.Any(obj => obj.LogicalName == obis && obj.Descriptor.Key.ClassId == classId) ||
            !imported.Access.Rights.TryGetValue(obis, out var rights))
            return "Объект отсутствует в дереве текущего соединения.";

        var access = rights.Methods.FirstOrDefault(item => item.Index == index);
        bool allowed = (imported.Access.AssociationVersion, access?.RawMode) switch
        {
            (1, (byte)MethodAccessModeV1.Access) or (3, (byte)AccessModeV3.Invoke) => true,
            (1, (byte)MethodAccessModeV1.AuthenticatedAccess) => authentication != ClientAuthentication.None,
            _ => false
        };
        return allowed ? "" : "Ассоциация не разрешает вызов либо требует неподдерживаемой защиты сообщений.";
    }

    public async Task<DlmsValue?> InvokeAsync(ushort classId, LogicalName obis, sbyte index, DlmsValue? parameter,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string reason = InvokeUnavailableReason(classId, obis, index);
            if (reason.Length != 0) throw new InvalidOperationException(reason);
            try
            {
                return await session.ActionAsync(classId, obis, index, parameter, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<DlmsValue> ReadAsync(ushort classId, LogicalName obis, sbyte index,
        SelectiveAccess? selection = null, CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var value = await session.GetAsync(classId, obis, index, selection, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
            return value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<ProfileBufferReadResult> ReadProfileBufferAsync(LogicalName obis, SelectiveAccess? selection,
        IProgress<ProfileBufferProgress> progress, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = await session.OpenProfileBufferAsync(obis, selection, cancellationToken)
                .ConfigureAwait(false);
            var rows = ImmutableArray.CreateBuilder<DlmsValue>();
            int? totalRows = stream.TotalCount;
            int? totalBytes = stream.TotalBytes;
            progress.Report(new(0, totalRows, 0, totalBytes));
            var lastReport = Stopwatch.GetTimestamp();
            await foreach (var row in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(row);
                int count = stream.CurrentCount;
                int bytes = stream.CurrentBytes;
                if (count != 1 && count != totalRows && bytes != totalBytes &&
                    Stopwatch.GetElapsedTime(lastReport) < TimeSpan.FromMilliseconds(50))
                    continue;
                progress.Report(new(count, totalRows, bytes, totalBytes));
                lastReport = Stopwatch.GetTimestamp();
            }

            return new(totalBytes.HasValue ? DlmsType.CompactArray : DlmsType.Array, rows.ToImmutable());
        }
        finally
        {
            Volatile.Write(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
            operationGate.Release();
        }
    }

    public async Task<int> RefreshCatalogAsync(IProgress<ObjectListProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (imported, skipped) = await ReadCatalogAsync(session, options, associationName,
                progress, cancellationToken).ConfigureAwait(false);
            Association = imported;
            SkippedDuplicateEntries = skipped;
            Volatile.Write(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
            return imported.Catalog.Objects.Count();
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static async Task<(ImportedAssociation Association, int Skipped)> ReadCatalogAsync(
        DlmsClientSession session, MeterConnectionOptions options, LogicalName associationName,
        IProgress<ObjectListProgress>? progress, CancellationToken cancellationToken)
    {
        var importer = new EmclBuilder { Limits = options.Limits }.CreateImporter();
        await using var stream = await session.OpenObjectListAsync(importer,
            options.AssociationVersion, associationName, cancellationToken).ConfigureAwait(false);
        var entries = new List<DlmsValue>(stream.TotalCount);
        progress?.Report(new ObjectListProgress(0, stream.TotalCount));
        var lastReport = Stopwatch.GetTimestamp();
        await foreach (var entry in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(entry.RawValue);
            if (stream.CurrentCount != stream.TotalCount &&
                Stopwatch.GetElapsedTime(lastReport) < TimeSpan.FromMilliseconds(50))
                continue;
            progress?.Report(new ObjectListProgress(stream.CurrentCount, stream.TotalCount));
            lastReport = Stopwatch.GetTimestamp();
        }

        var list = DlmsValue.Array([.. entries]);
        int skipped = 0;
        if (options.IgnoreIdenticalObjectListEntries)
        {
            var filtered = IdenticalObjectListDuplicateFilter.Apply(list, options.Limits);
            list = filtered.Value;
            skipped = filtered.SkippedCount;
        }
        return (importer.Import(list, "meter", "current", options.AssociationVersion), skipped);
    }

    private async Task RunKeepAliveAsync()
    {
        var cancellationToken = keepAliveStop.Token;
        try
        {
            while (true)
            {
                await Task.Delay(keepAliveInterval, cancellationToken).ConfigureAwait(false);
                if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                    continue;
                try
                {
                    if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivityTimestamp)) < keepAliveInterval)
                        continue;
                    if (!IsActive)
                    {
                        ReportKeepAliveFailure(new IOException("Сессия счётчика закрыта."));
                        return;
                    }
                    await session.GetAsync(CosemClassIds.AssociationLn, associationName,
                        CosemObject.LogicalNameAttributeIndex, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref lastActivityTimestamp, Stopwatch.GetTimestamp());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    ReportKeepAliveFailure(error);
                    return;
                }
                finally
                {
                    operationGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void ReportKeepAliveFailure(Exception error)
    {
        try { KeepAliveFailed?.Invoke(this, error); }
        catch { /* A status subscriber must not fault connection cleanup. */ }
    }

    public ValueTask DisposeAsync() => DisposeAsync(CancellationToken.None);

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        lock (disposeSync)
            return new(disposeTask ??= DisposeCoreAsync(cancellationToken));
    }

    private async Task DisposeCoreAsync(CancellationToken cancellationToken)
    {
        keepAliveStop.Cancel();
        await keepAliveTask.ConfigureAwait(false);
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.State == ClientSessionState.Associated)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await session.ReleaseAsync(timeout.Token);
            }
        }
        catch (Exception)
        {
            // Closing the local transport is still required when release is not acknowledged.
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                operationGate.Release();
                operationGate.Dispose();
                keepAliveStop.Dispose();
            }
        }
    }
}
