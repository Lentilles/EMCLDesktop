using System.Buffers;
using System.Reflection;
using System.Threading.Channels;
using EMCL.Client.Association;
using EMCL.Client.Sessions;
using EMCL.Composition;
using EMCL.Core.Codecs;
using EMCL.Core.Objects;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Protocol.Axdr;
using EMCL.Transport.Channels;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public class CancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("profile")]
    [InlineData("attribute")]
    [InlineData("attributes")]
    [InlineData("tree")]
    [InlineData("write")]
    [InlineData("readback")]
    [InlineData("invoke")]
    [InlineData("disconnect")]
    public async Task CancelOperationCommand_PendingMeterOperation_DisablesCancellationAndReleasesSession(string mode)
    {
        // Arrange
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);

        // Act
        bool wasInProgress = vm.IsOperationInProgress;
        bool wasCancelable = vm.CancelOperationCommand.CanExecute(null);
        vm.CancelOperationCommand.Execute(null);
        bool remainsCancelable = vm.CancelOperationCommand.CanExecute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.True(wasInProgress);
        Assert.True(wasCancelable);
        Assert.False(remainsCancelable);
        Assert.True(!vm.IsBusy && !vm.IsReading && !vm.IsOperationInProgress && !vm.IsTreeReadInProgress &&
            !vm.IsProfileReadInProgress && vm.CanEditConfiguration && vm.CanSelectObjects,
            $"{mode}: progress or UI locks remained after cancellation");
        Assert.True(!vm.IsConnected && channel.Disposed, $"{mode}: an interrupted exchange was reused or its transport leaked");
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("attribute")]
    [InlineData("attributes")]
    [InlineData("tree")]
    public async Task CancelOperationCommand_PendingRead_ReportsCancellationAndStopsRequests(string mode)
    {
        // Arrange
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);

        // Act
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.True(!vm.HasStatusError && vm.Status.Contains("отменено") && !vm.Status.Contains("потеряно"),
            $"{mode}: user cancellation was reported as a connection error");
        Assert.True(channel.Gets == 1 && channel.NextBlocks <= 1, $"{mode}: read requests continued after cancellation");
    }

    [Fact]
    public async Task CancelOperationCommand_PartialProfile_DoesNotPublishIncompleteTable()
    {
        // Arrange
        const string mode = "profile";
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);
        var obj = vm.Objects.Single();

        // Act
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.Null(obj.ProfileTable);
        Assert.Contains("отменено", obj.ProfileTableStatus);
    }

    [Fact]
    public async Task CancelOperationCommand_GroupedRead_PreservesCompletedAndUnreadAttributes()
    {
        // Arrange
        const string mode = "attributes";
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);
        var obj = vm.Objects.Single();
        var later = obj.Attributes.Single(item => item.Index == 4);

        // Act
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.True(obj.Attributes.Single(item => item.Index == 1).Value == obj.Obis, "Canceling grouped reads discarded completed attributes or read later attributes");
        Assert.Equal("Не считано", later.Value);
        Assert.Contains("1 из 3", vm.Status);
    }

    [Fact]
    public async Task CancelOperationCommand_PendingCatalogRead_PreservesPreviousObjects()
    {
        // Arrange
        const string mode = "tree";
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);
        var obj = vm.Objects.Single();

        // Act
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.True(vm.Objects.Single() == obj, "Canceled tree reading discarded the previous catalog");
    }

    [Fact]
    public async Task CancelOperationCommand_SetAwaitingReply_ReportsUnconfirmedWriteWithoutRetry()
    {
        // Arrange
        const string mode = "write";
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);
        var attribute = vm.Objects.Single().Attributes.Single(item => item.Index == 2);

        // Act
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.True(vm.HasStatusError, "Canceling a sent SET retried the write or claimed it was not applied");
        Assert.Contains("не подтверждена", attribute.WriteStatus);
        Assert.Equal(1, channel.Writes);
        Assert.Equal(0, channel.Gets);
    }

    [Fact]
    public async Task CancelOperationCommand_ReadbackAfterConfirmedSet_PreservesWriteConfirmation()
    {
        // Arrange
        const string mode = "readback";
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);
        var attribute = vm.Objects.Single().Attributes.Single(item => item.Index == 2);

        // Act
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.False(vm.HasStatusError, "Canceling read-back hid an already confirmed SET or retried it");
        Assert.Equal("Запись подтверждена. Перечитывание отменено.", attribute.WriteStatus);
        Assert.Equal(1, channel.Writes);
    }

    [Fact]
    public async Task CancelOperationCommand_ActionAwaitingReply_ReportsUnconfirmedInvocationWithoutRetry()
    {
        // Arrange
        const string mode = "invoke";
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);
        var method = vm.Objects.Single().Methods.Single();

        // Act
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.True(vm.HasStatusError, "Canceling an attempted ACTION claimed it was not executed or retried it");
        Assert.Contains("не подтверждён", method.InvokeStatus);
        Assert.Equal(1, channel.Actions);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("attribute")]
    [InlineData("attributes")]
    [InlineData("tree")]
    [InlineData("write")]
    [InlineData("readback")]
    [InlineData("invoke")]
    [InlineData("disconnect")]
    public async Task SaveConfigurationAsync_AfterMeterOperationCancellation_StartsNormally(string mode)
    {
        // Arrange
        var (vm, channel) = await CreateConnectedViewModelAsync(mode);
        await using var viewModel = vm;
        var work = StartMeterOperation(vm, mode);
        await channel.Waiting.Task.WaitAsync(Deadline);
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Act
        await using var nextSave = new MemoryStream();
        await vm.SaveConfigurationAsync(nextSave);

        // Assert
        Assert.True(nextSave.Length > 0 && !vm.HasStatusError && !vm.IsOperationInProgress,
            $"{mode}: canceling one operation canceled later operations as well");
    }

    [Theory]
    [InlineData("load")]
    [InlineData("save")]
    [InlineData("trace")]
    public async Task CancelOperationCommand_BlockedFileOperation_ReleasesBusyStateWithoutError(string mode)
    {
        // Arrange
        await using var vm = CreateViewModel();
        await using var blocked = new WaitingStream();
        var work = StartFileOperation(vm, blocked, mode);
        await blocked.Waiting.Task.WaitAsync(Deadline);

        // Act
        bool wasCancelable = vm.CancelOperationCommand.CanExecute(null);
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Assert
        Assert.True(wasCancelable);
        Assert.True(!vm.IsBusy && !vm.IsOperationInProgress && !vm.HasStatusError && vm.Status.Contains("отменен"),
            $"{mode}: file cancellation left an error or a busy state");
    }

    [Theory]
    [InlineData("load")]
    [InlineData("save")]
    [InlineData("trace")]
    public async Task LoadConfigurationAsync_AfterFileOperationCancellation_LoadsNormally(string mode)
    {
        // Arrange
        await using var vm = CreateViewModel();
        await using var source = new MemoryStream();
        await vm.SaveConfigurationAsync(source);
        source.Position = 0;
        await using var blocked = new WaitingStream();
        var work = StartFileOperation(vm, blocked, mode);
        await blocked.Waiting.Task.WaitAsync(Deadline);
        vm.CancelOperationCommand.Execute(null);
        await work.WaitAsync(Deadline);

        // Act
        await vm.LoadConfigurationAsync(source);

        // Assert
        Assert.True(!vm.HasStatusError && vm.Host == "127.0.0.1", $"{mode}: configuration could not be loaded after cancellation");
    }

    [Fact]
    public async Task AnalyzeTextCancelCommand_PendingAnalysis_StopsAndKeepsPreviousPacket()
    {
        // Arrange
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace()) { SourceIndex = 1, InputText = "RX: C4 01 C1 00 12 08 FD" };
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);
        var prior = inspector.Packets.Single();
        inspector.InputText = string.Concat(Enumerable.Repeat("unrelated line\n", 200000));
        var analyzing = inspector.AnalyzeTextCommand.ExecuteAsync(null);

        // Act
        inspector.AnalyzeTextCancelCommand.Execute(null);
        await analyzing.WaitAsync(Deadline);

        // Assert
        Assert.False(inspector.IsAnalyzing, "Canceling log analysis discarded the previous result or left analysis running");
        Assert.Contains("отменён", inspector.AnalysisStatus);
        Assert.True(inspector.Packets.Single() == prior, "Canceling log analysis discarded the previous result or left analysis running");
    }

    [Fact]
    public async Task AnalyzeTextCommand_AfterCanceledAnalysis_CanAnalyzeAgain()
    {
        // Arrange
        using var inspector = new PacketInspectorViewModel(new DiagnosticTrace()) { SourceIndex = 1, InputText = "RX: C4 01 C1 00 12 08 FD" };
        inspector.InputText = string.Concat(Enumerable.Repeat("unrelated line\n", 200000));
        var analyzing = inspector.AnalyzeTextCommand.ExecuteAsync(null);
        inspector.AnalyzeTextCancelCommand.Execute(null);
        await analyzing.WaitAsync(Deadline);
        inspector.InputText = "RX: C4 01 C1 00 12 08 FD";

        // Act
        await inspector.AnalyzeTextCommand.ExecuteAsync(null);

        // Assert
        Assert.False(inspector.IsAnalyzing, "Log analysis could not restart after cancellation");
        Assert.DoesNotContain("отменён", inspector.AnalysisStatus);
    }

    private static async Task<(MainViewModel ViewModel, WaitingMeterChannel Channel)> CreateConnectedViewModelAsync(string mode)
    {
        var channel = new WaitingMeterChannel(mode);
        var session = new DlmsClientSession(channel, new ClientSessionOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
            Association = new LnAssociationOptions { Authentication = ClientAuthentication.None }
        });
        await session.AssociateAsync();
        var options = new MeterConnectionOptions("test", 1, "Wrapper", 16, 1, null, false, 1,
            "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 1, 30, 3600, 1024,
            LnAssociationOptions.SupportedConformance, false, new ProtocolLimits());
        var meter = (MeterConnection)Activator.CreateInstance(typeof(MeterConnection), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [session, ClientAuthentication.None, LogicalName.Parse(options.AssociationObis), TimeSpan.FromHours(1), options], null)!;
        var imported = new EmclBuilder().CreateImporter().Import(DlmsValue.Array(DlmsValue.Structure(
            DlmsValue.Number(DlmsType.UInt16, 7), DlmsValue.Number(DlmsType.UInt8, 1),
            DlmsValue.Octets([1, 0, 99, 1, 0, 255]), DlmsValue.Structure(DlmsValue.Array(
                DlmsValue.Structure(DlmsValue.Number(DlmsType.Int8, 2), DlmsValue.Number(DlmsType.Enum, 3), DlmsValue.Null)),
                DlmsValue.Array(DlmsValue.Structure(DlmsValue.Number(DlmsType.Int8, 1), DlmsValue.Number(DlmsType.Enum, 1)))))),
            "test", "test", 1);
        typeof(MeterConnection).GetProperty(nameof(MeterConnection.Association))!.SetValue(meter, imported);
        var vm = CreateViewModel();
        typeof(MainViewModel).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, meter);
        vm.IsConnected = true;
        var obj = new MeterObjectViewModel(LogicalName.Parse("1.0.99.1.0.255"), 7, 1, "Profile Generic")
            { ProfileCaptureObjects = DlmsValue.Array() };
        var attribute = new AttributeViewModel(2, "buffer", null, "", true)
            { WriteType = DlmsType.UInt16, WriteText = "77", Value = "Previously read" };
        var later = new AttributeViewModel(4, "capture_period", "UInt32", "", true);
        obj.Attributes.Add(new AttributeViewModel(1, "logical_name", "OctetString", "", true));
        obj.Attributes.Add(attribute);
        obj.Attributes.Add(later);
        obj.Methods.Add(new MethodViewModel(1, "reset", null, ""));
        vm.Objects.Add(obj);
        vm.SelectedObject = obj;
        return (vm, channel);
    }

    private static MainViewModel CreateViewModel() =>
        new() { Host = "127.0.0.1", TcpPort = "4059", Framing = "Wrapper" };

    private static Task StartMeterOperation(MainViewModel vm, string mode)
    {
        var obj = vm.Objects.Single();
        return mode switch
        {
            "profile" => vm.ReadProfileAsync(obj),
            "attribute" => vm.ReadAttributeAsync(obj.Attributes.Single(item => item.Index == 4)),
            "attributes" => vm.ReadSelectedObjectCommand.ExecuteAsync(null),
            "tree" => vm.ReadTreeCommand.ExecuteAsync(null),
            "write" or "readback" => vm.WriteAttributeAsync(obj.Attributes.Single(item => item.Index == 2)),
            "invoke" => vm.InvokeMethodAsync(obj.Methods.Single()),
            "disconnect" => vm.DisconnectCommand.ExecuteAsync(null),
            _ => throw new Exception(mode)
        };
    }

    private static Task StartFileOperation(MainViewModel vm, Stream stream, string mode) => mode switch
    {
        "load" => vm.LoadConfigurationAsync(stream),
        "save" => vm.SaveConfigurationAsync(stream),
        "trace" => vm.SaveTraceAsync(stream),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private sealed class WaitingMeterChannel(string mode) : IApduChannel
    {
        private readonly ApduCodec codec = new();
        private readonly Channel<ReadOnlyMemory<byte>> responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Gets { get; private set; }
        public int Writes { get; private set; }
        public int Actions { get; private set; }
        public int NextBlocks { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken = default)
        {
            DlmsApdu? reply = null;
            var request = codec.Decode(new ReadOnlySequence<byte>(apdu)).Value;
            switch (request)
            {
                case AcseApdu { Kind: AcseKind.Aarq }:
                    reply = AcseApdu.Aare(codec.EncodeBytes(new InitiateResponse(null, DlmsNegotiation.Version6,
                        (uint)(LnAssociationOptions.SupportedConformance & ~DlmsConformance.PriorityManagement), 1024)));
                    break;
                case AcseApdu { Kind: AcseKind.Rlrq }:
                    if (mode == "disconnect") Waiting.TrySetResult();
                    else reply = AcseApdu.Release(response: true);
                    break;
                case LnRequest ln when ln.Operation.Kind == CosemOperationKind.Read:
                    Gets++;
                    if (mode is "profile" or "attributes")
                    {
                        var bytes = new AxdrCodec().EncodeBytes(DlmsValue.Array(Enumerable.Range(0, 2000)
                            .Select(i => DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, i))).ToArray()));
                        reply = new GetBlockResponse(ln.InvokeIdAndPriority, false, 1, [.. bytes.AsSpan(0, 9)]);
                    }
                    else if (mode == "tree") reply = new GetBlockResponse(ln.InvokeIdAndPriority, false, 1, [1, 1]);
                    else Waiting.TrySetResult();
                    break;
                case GetNextRequest:
                    NextBlocks++;
                    Waiting.TrySetResult();
                    break;
                case LnRequest ln when ln.Operation.Kind == CosemOperationKind.Write:
                    Writes++;
                    if (mode == "readback") reply = new LnResponse(ln.Operation.Kind, ln.InvokeIdAndPriority, OperationResult.Ok());
                    else Waiting.TrySetResult();
                    break;
                case LnRequest ln when ln.Operation.Kind == CosemOperationKind.Invoke:
                    Actions++;
                    Waiting.TrySetResult();
                    break;
                default: throw new Exception($"Unexpected cancellation-test request: {request}");
            }
            if (reply is not null) responses.Writer.TryWrite(codec.EncodeBytes(reply));
            return ValueTask.CompletedTask;
        }
        public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            await responses.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class WaitingStream : MemoryStream
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
