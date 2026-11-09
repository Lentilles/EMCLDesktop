using System.Buffers;
using System.Reflection;
using System.Text.Json;
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

internal static class CancellationChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Cancellation: " + message);
            checks++;
        }
        var deadline = TimeSpan.FromSeconds(5);
        foreach (string mode in new[] { "profile", "attribute", "attributes", "tree", "write", "readback", "invoke", "disconnect" })
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
            await using var vm = new MainViewModel { Host = "127.0.0.1", TcpPort = "4059", Framing = "Wrapper" };
            typeof(MainViewModel).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, meter);
            vm.IsConnected = true;
            var obj = new MeterObjectViewModel(LogicalName.Parse("1.0.99.1.0.255"), 7, 1, "Profile Generic")
                { ProfileCaptureObjects = DlmsValue.Array() };
            var attribute = new AttributeViewModel(2, "buffer", null, "", true)
                { WriteType = DlmsType.UInt16, WriteText = "77", Value = "Previously read" };
            var later = new AttributeViewModel(4, "capture_period", "UInt32", "", true);
            obj.Attributes.Add(new AttributeViewModel(1, "logical_name", "OctetString", "", true));
            obj.Attributes.Add(attribute); obj.Attributes.Add(later);
            var method = new MethodViewModel(1, "reset", null, ""); obj.Methods.Add(method);
            vm.Objects.Add(obj); vm.SelectedObject = obj;
            Task work = mode switch
            {
                "profile" => vm.ReadProfileAsync(obj),
                "attribute" => vm.ReadAttributeAsync(later),
                "attributes" => vm.ReadSelectedObjectCommand.ExecuteAsync(null),
                "tree" => vm.ReadTreeCommand.ExecuteAsync(null),
                "write" or "readback" => vm.WriteAttributeAsync(attribute),
                "invoke" => vm.InvokeMethodAsync(method),
                "disconnect" => vm.DisconnectCommand.ExecuteAsync(null),
                _ => throw new Exception(mode)
            };
            await channel.Waiting.Task.WaitAsync(deadline);
            Check(vm.IsOperationInProgress && vm.CancelOperationCommand.CanExecute(null), $"{mode}: no active cancel command");
            vm.CancelOperationCommand.Execute(null);
            Check(!vm.CancelOperationCommand.CanExecute(null), $"{mode}: cancel remained enabled after requesting cancellation");
            await work.WaitAsync(deadline);
            Check(!vm.IsBusy && !vm.IsReading && !vm.IsOperationInProgress && !vm.IsTreeReadInProgress &&
                !vm.IsProfileReadInProgress && vm.CanEditConfiguration && vm.CanSelectObjects,
                $"{mode}: progress or UI locks remained after cancellation");
            Check(!vm.IsConnected && channel.Disposed, $"{mode}: an interrupted exchange was reused or its transport leaked");
            if (mode is "profile" or "attribute" or "attributes" or "tree")
            {
                Check(!vm.HasStatusError && vm.Status.Contains("отменено") && !vm.Status.Contains("потеряно"),
                    $"{mode}: user cancellation was reported as a connection error");
                Check(channel.Gets == 1 && channel.NextBlocks <= 1, $"{mode}: read requests continued after cancellation");
            }
            if (mode == "profile")
                Check(obj.ProfileTable is null && obj.ProfileTableStatus.Contains("отменено"),
                    "A canceled 2000-row profile was presented as complete");
            if (mode == "attributes")
                Check(obj.Attributes.Single(item => item.Index == 1).Value == obj.Obis &&
                    later.Value == "Не считано" && vm.Status.Contains("1 из 3"),
                    "Canceling grouped reads discarded completed attributes or read later attributes");
            if (mode == "tree")
                Check(vm.Objects.Single() == obj, "Canceled tree reading discarded the previous catalog");
            if (mode == "write")
                Check(vm.HasStatusError && attribute.WriteStatus.Contains("не подтверждена") && channel.Writes == 1 && channel.Gets == 0,
                    "Canceling a sent SET retried the write or claimed it was not applied");
            if (mode == "readback")
                Check(!vm.HasStatusError && attribute.WriteStatus == "Запись подтверждена. Перечитывание отменено." && channel.Writes == 1,
                    "Canceling read-back hid an already confirmed SET or retried it");
            if (mode == "invoke")
                Check(vm.HasStatusError && method.InvokeStatus.Contains("не подтверждён") && channel.Actions == 1,
                    "Canceling an attempted ACTION claimed it was not executed or retried it");
            await using var nextSave = new MemoryStream();
            await vm.SaveConfigurationAsync(nextSave);
            Check(nextSave.Length > 0 && !vm.HasStatusError && !vm.IsOperationInProgress,
                $"{mode}: canceling one operation canceled later operations as well");
        }

        await using (var vm = new MainViewModel { Host = "127.0.0.1", TcpPort = "4059", Framing = "Wrapper" })
        {
            await using var source = new MemoryStream();
            await vm.SaveConfigurationAsync(source);
            foreach (string mode in new[] { "load", "save", "trace" })
            {
                await using var blocked = new WaitingStream();
                Task work = mode switch
                {
                    "load" => vm.LoadConfigurationAsync(blocked),
                    "save" => vm.SaveConfigurationAsync(blocked),
                    _ => vm.SaveTraceAsync(blocked)
                };
                await blocked.Waiting.Task.WaitAsync(deadline);
                Check(vm.CancelOperationCommand.CanExecute(null), $"{mode}: file operation could not be canceled");
                vm.CancelOperationCommand.Execute(null);
                await work.WaitAsync(deadline);
                Check(!vm.IsBusy && !vm.IsOperationInProgress && !vm.HasStatusError && vm.Status.Contains("отменен"),
                    $"{mode}: file cancellation left an error or a busy state");
                source.Position = 0;
                await vm.LoadConfigurationAsync(source);
                Check(!vm.HasStatusError && vm.Host == "127.0.0.1", $"{mode}: configuration could not be loaded after cancellation");
            }
        }
        using (var inspector = new PacketInspectorViewModel(new DiagnosticTrace()) { SourceIndex = 1, InputText = "RX: C4 01 C1 00 12 08 FD" })
        {
            await inspector.AnalyzeTextCommand.ExecuteAsync(null);
            var prior = inspector.Packets.Single();
            inspector.InputText = string.Concat(Enumerable.Repeat("unrelated line\n", 200000));
            var analyzing = inspector.AnalyzeTextCommand.ExecuteAsync(null);
            inspector.AnalyzeTextCancelCommand.Execute(null);
            await analyzing.WaitAsync(deadline);
            Check(!inspector.IsAnalyzing && inspector.AnalysisStatus.Contains("отменён") && inspector.Packets.Single() == prior,
                "Canceling log analysis discarded the previous result or left analysis running");
            inspector.InputText = "RX: C4 01 C1 00 12 08 FD";
            await inspector.AnalyzeTextCommand.ExecuteAsync(null);
            Check(!inspector.IsAnalyzing && !inspector.AnalysisStatus.Contains("отменён"), "Log analysis could not restart after cancellation");
        }
        return checks;
    }

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
                    NextBlocks++; Waiting.TrySetResult();
                    break;
                case LnRequest ln when ln.Operation.Kind == CosemOperationKind.Write:
                    Writes++;
                    if (mode == "readback") reply = new LnResponse(ln.Operation.Kind, ln.InvokeIdAndPriority, OperationResult.Ok());
                    else Waiting.TrySetResult();
                    break;
                case LnRequest ln when ln.Operation.Kind == CosemOperationKind.Invoke:
                    Actions++; Waiting.TrySetResult();
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
