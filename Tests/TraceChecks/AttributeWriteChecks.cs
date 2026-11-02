using System.Buffers;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading.Channels;
using EMCL.Client.Association;
using EMCL.Client.Sessions;
using EMCL.Composition;
using EMCL.Core.Access;
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

internal static class AttributeWriteChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Attribute write: " + message);
            count++;
        }
        void Invalid(DlmsType type, string text)
        {
            try { DlmsValueEditor.Parse(type, text); }
            catch (FormatException) { count++; return; }
            throw new Exception($"Editor accepted invalid {type}: {text}");
        }
        var codec = new AxdrCodec();
        foreach (var (type, text) in new (DlmsType, string)[]
        {
            (DlmsType.Int8, "-128"), (DlmsType.UInt8, "255"), (DlmsType.Int16, "-32768"),
            (DlmsType.UInt16, "65535"), (DlmsType.Int32, "-2147483648"), (DlmsType.UInt32, "4294967295"),
            (DlmsType.Int64, "-9223372036854775808"), (DlmsType.UInt64, "18446744073709551615"),
            (DlmsType.Enum, "255"), (DlmsType.Boolean, "false"), (DlmsType.Null, "null"),
            (DlmsType.Float32, "1.25"), (DlmsType.Float64, "-2.5e30"),
            (DlmsType.BitString, "101010101"), (DlmsType.OctetString, "01 00 FF"),
            (DlmsType.OctetString, ""), (DlmsType.Utf8String, "  Значение\n "),
            (DlmsType.VisibleString, "  text  "), (DlmsType.DateTime, "07E80B02060A1E0000800000"),
            (DlmsType.Date, "07E80B0206"), (DlmsType.Time, "0A1E0000"),
            (DlmsType.CompactArray, "13120400010002"),
            (DlmsType.Structure, "[{\"type\":\"Int8\",\"value\":-3},{\"type\":\"Array\",\"value\":[{\"type\":\"UInt64\",\"value\":18446744073709551615},{\"type\":\"OctetString\",\"value\":\"00FF\"}]}]")
        })
        {
            var value = DlmsValueEditor.Parse(type, text);
            var reparsed = DlmsValueEditor.Parse(type, DlmsValueEditor.Format(value));
            Check(codec.EncodeBytes(value).SequenceEqual(codec.EncodeBytes(reparsed)), $"Round trip lost {type}");
        }
        foreach (var (type, text) in new (DlmsType, string)[]
        {
            (DlmsType.Int8, "128"), (DlmsType.UInt8, "-1"), (DlmsType.UInt16, "65536"),
            (DlmsType.Int32, "2147483648"), (DlmsType.UInt64, "18446744073709551616"),
            (DlmsType.Boolean, "2"), (DlmsType.BitString, "102"), (DlmsType.OctetString, "F"),
            (DlmsType.Date, "00"), (DlmsType.VisibleString, "Привет"), (DlmsType.Float32, "NaN"),
            (DlmsType.Float64, "Infinity"), (DlmsType.Float64, "1,25"), (DlmsType.Null, ""),
            (DlmsType.Array, "[{\"type\":\"UInt8\",\"value\":256}]"),
            (DlmsType.Array, "[{\"type\":\"Missing\",\"value\":1}]"),
            (DlmsType.CompactArray, "1101"), (DlmsType.CompactArray, "1312040001000200")
        }) Invalid(type, text);

        var attr = new AttributeViewModel(2, "value", null, "", true);
        Check(attr.WriteType is null && !attr.CanUseReadValue, "Unknown type was guessed");
        var large = DlmsValue.Octets(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        attr.RememberReadValue(large);
        attr.UseReadValue();
        Check(DlmsValueEditor.Parse(attr.WriteType!.Value, attr.WriteText).Bytes.SequenceEqual(large.Bytes),
            "Editor copied a shortened display value");
        attr.WriteText = "draft";
        attr.RememberReadValue(DlmsValue.Octets([1, 2]));
        Check(attr.WriteText == "draft", "A read overwrote the draft");
        attr.ForgetReadValue();
        Check(!attr.CanUseReadValue, "Stale pre-write value remained copyable");

        for (byte mode = 0; mode <= 6; mode++)
        {
            var access = new AttributeAccess(2, mode, []);
            Check(MeterConnection.AllowsWrite(access, 1, ClientAuthentication.None) == (mode is 2 or 3), "v1 anonymous rights");
            Check(MeterConnection.AllowsWrite(access, 1, ClientAuthentication.High) == (mode is 2 or 3 or 5 or 6), "v1 authenticated rights");
        }
        for (int mode = 0; mode <= 255; mode++)
            Check(MeterConnection.AllowsWrite(new AttributeAccess(2, (byte)mode, []), 3, ClientAuthentication.High) ==
                (mode is 2 or 3), "v3 protected or readonly write allowed");

        foreach (var outcome in new[] { "ok", "reject", "lost", "read-fails", "write-only", "no-set", "no-tree",
            "boundary", "blocks-ok", "blocks-reject-first", "blocks-reject-middle", "blocks-reject-last", "blocks-lost", "blocks-no-support" })
        {
            var channel = new MeterChannel(outcome);
            var session = new DlmsClientSession(channel, new ClientSessionOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(2),
                Association = new LnAssociationOptions { Authentication = ClientAuthentication.None }
            });
            await session.AssociateAsync();
            var name = LogicalName.Parse("1.0.0.2.0.255");
            var options = new MeterConnectionOptions("test", 1, "Wrapper", 16, 1, null, false, 1,
                "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 1, 1, 3600, 1024,
                LnAssociationOptions.SupportedConformance, false, new ProtocolLimits());
            var meter = (MeterConnection)Activator.CreateInstance(typeof(MeterConnection), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [session, ClientAuthentication.None, LogicalName.Parse(options.AssociationObis), TimeSpan.FromHours(1), options], null)!;
            if (outcome != "no-tree")
            {
                var entry = DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 1), DlmsValue.Number(DlmsType.UInt8, 0),
                    DlmsValue.Octets([1, 0, 0, 2, 0, 255]), DlmsValue.Structure(DlmsValue.Array(
                        DlmsValue.Structure(DlmsValue.Number(DlmsType.Int8, 2),
                            DlmsValue.Number(DlmsType.Enum, outcome == "write-only" ? 2 : 3), DlmsValue.Null)), DlmsValue.Array()));
                var imported = new EmclBuilder().CreateImporter().Import(DlmsValue.Array(entry), "test", "test", 1);
                typeof(MeterConnection).GetProperty(nameof(MeterConnection.Association))!.SetValue(meter, imported);
            }
            await using var vm = new MainViewModel();
            typeof(MainViewModel).GetField("connection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, meter);
            vm.IsConnected = true;
            var obj = new MeterObjectViewModel(name, 1, 0, "Data");
            bool largeWrite = outcome.StartsWith("blocks-") || outcome == "boundary";
            var written = largeWrite ? DlmsValue.Octets(Enumerable.Range(0, outcome == "boundary" ? 49 : 140).Select(i => (byte)i).ToArray())
                : DlmsValue.Number(DlmsType.UInt16, 77);
            var draft = DlmsValueEditor.Format(written);
            var attribute = new AttributeViewModel(2, "value", null, "", true)
                { WriteType = written.Type, WriteText = draft, Value = "Old read" };
            obj.Attributes.Add(attribute);
            vm.Objects.Add(obj);
            vm.SelectedObject = obj;
            Check(attribute.IsWriteEnabled == (outcome is not ("no-set" or "no-tree")), "Write availability ignored live association");
            await vm.WriteAttributeAsync(attribute);
            Check(!vm.IsBusy && attribute.WriteText == draft, "Write lost draft or busy state");
            Check(channel.Sets == (outcome is "no-set" or "no-tree" or "blocks-no-support" ? 0 : 1), "SET retried or bypassed a guard");
            if (outcome is "blocks-ok" or "boundary")
            {
                Check(!vm.HasStatusError && channel.WritesApplied == 1 && channel.Gets == 1 &&
                    attribute.LastReadValue is not null && codec.EncodeBytes(attribute.LastReadValue).SequenceEqual(codec.EncodeBytes(written)),
                    "Large SET was not applied exactly once and read back");
                Check(channel.BlockRequests == (outcome == "boundary" ? 0 : 3), "Incorrect block count or normal-PDU boundary");
            }
            if (outcome.StartsWith("blocks-reject"))
                Check(vm.HasStatusError && channel.WritesApplied == 0 && channel.Gets == 0 &&
                    attribute.WriteStatus.Contains("ReadWriteDenied") && vm.IsConnected,
                    "Block rejection lost meter result or faulted the connection");
            if (outcome == "blocks-lost")
                Check(vm.HasStatusError && channel.WritesApplied == 1 && channel.BlockRequests == 3 && channel.Gets == 0 &&
                    !vm.IsConnected && attribute.WriteStatus.Contains("не подтверждена"),
                    "Missing final confirmation retried an applied SET or reported a definite outcome");
            if (outcome == "blocks-no-support")
                Check(vm.HasStatusError && channel.BlockRequests == 0 && channel.WritesApplied == 0 && vm.IsConnected &&
                    attribute.WriteStatus.Contains("не выполнена"), "Unnegotiated blocks were sent or treated as an uncertain write");
            if (outcome == "ok")
                Check(channel.Gets == 1 && attribute.Value == "78" && !vm.HasStatusError,
                    "Write did not show the actual read-back value");
            if (outcome == "reject")
                Check(vm.HasStatusError && channel.Gets == 0 && attribute.Value == "Old read" &&
                    attribute.WriteStatus.Contains("ReadWriteDenied"), "Refusal lost the device error or changed read value");
            if (outcome == "lost")
                Check(vm.HasStatusError && !vm.IsConnected && attribute.WriteStatus.Contains("не подтверждена"),
                    "Lost reply was shown as a definite failure or success");
            if (outcome == "read-fails")
                Check(vm.HasStatusError && attribute.WriteStatus.Contains("Запись подтверждена, но") &&
                    attribute.LastReadValue is null, "Read-back failure masked confirmed SET");
            if (outcome == "write-only")
                Check(channel.Gets == 0 && !vm.HasStatusError && attribute.Value.Contains("не считано"),
                    "Write-only attribute was read or its draft presented as actual data");
        }
        return count;
    }

    private sealed class MeterChannel(string outcome) : IApduChannel
    {
        private readonly Channel<ReadOnlyMemory<byte>> replies = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly ApduCodec codec = new();
        public int Sets { get; private set; }
        public int Gets { get; private set; }
        public int BlockRequests { get; private set; }
        public int WritesApplied { get; private set; }
        private readonly MemoryStream blockData = new();
        private uint expectedBlock = 1;
        private DlmsValue? stored;

        private DlmsApdu? AcceptBlock(byte invoke, bool last, uint number, ImmutableArray<byte> data)
        {
            BlockRequests++;
            if (number != expectedBlock++) throw new Exception("SET block number repeated or skipped");
            blockData.Write(data.AsSpan());
            if (outcome == "blocks-reject-first" && number == 1 ||
                outcome == "blocks-reject-middle" && number == 2 ||
                outcome == "blocks-reject-last" && last)
                return new SetBlockResponse(invoke, number, CosemError.ReadWriteDenied);
            if (!last) return new SetBlockResponse(invoke, number);
            var decoded = new AxdrCodec().Decode(new ReadOnlySequence<byte>(blockData.ToArray()));
            if (decoded.Status != DecodeStatus.Done || decoded.Consumed != blockData.Length ||
                !decoded.Value!.Bytes.SequenceEqual(Enumerable.Range(0, 140).Select(i => (byte)i)))
                throw new Exception("SET blocks changed the encoded value");
            stored = decoded.Value;
            WritesApplied++;
            if (outcome == "blocks-lost") { replies.Writer.TryComplete(); return null; }
            return new SetBlockResponse(invoke, number, CosemError.Success);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken = default)
        {
            DlmsApdu? response;
            if (apdu.Span[0] == (byte)ApduTag.SetRequest && (outcome.StartsWith("blocks-") || outcome == "boundary") && apdu.Length > 64)
                throw new Exception("SET exceeded the server PDU");
            var decoded = codec.Decode(new ReadOnlySequence<byte>(apdu));
            switch (decoded.Value)
            {
                case AcseApdu { Kind: AcseKind.Aarq }:
                    var flags = LnAssociationOptions.SupportedConformance & ~DlmsConformance.PriorityManagement;
                    if (outcome == "no-set") flags &= ~DlmsConformance.Set;
                    if (outcome == "blocks-no-support") flags &= ~DlmsConformance.BlockTransferWithSet;
                    response = AcseApdu.Aare(codec.EncodeBytes(new InitiateResponse(null, DlmsNegotiation.Version6, (uint)flags, (ushort)(outcome.StartsWith("blocks-") || outcome == "boundary" ? 64 : 1024))));
                    break;
                case AcseApdu { Kind: AcseKind.Rlrq }:
                    response = AcseApdu.Release(response: true);
                    break;
                case SetFirstBlockRequest first:
                    Sets++;
                    if (first.Operation.ClassId != 1 || first.Operation.LogicalName.ToString() != "1.0.0.2.0.255" || first.Operation.Index != 2)
                        throw new Exception("SET block target changed");
                    response = AcceptBlock(first.InvokeIdAndPriority, first.LastBlock, first.BlockNumber, first.Data);
                    break;
                case SetBlockRequest block:
                    response = AcceptBlock(block.InvokeIdAndPriority, block.LastBlock, block.BlockNumber, block.Data);
                    break;
                case LnRequest request:
                    if (request.Operation.Kind == CosemOperationKind.Write)
                    {
                        Sets++;
                        if (outcome == "boundary")
                        {
                            stored = request.Operation.Value;
                            WritesApplied++;
                            response = new LnResponse(CosemOperationKind.Write, request.InvokeIdAndPriority, OperationResult.Ok());
                            break;
                        }
                        if (request.Operation.Value?.Type != DlmsType.UInt16 || request.Operation.Value.AsUInt64() != 77 ||
                            request.Operation.LogicalName.ToString() != "1.0.0.2.0.255" || request.Operation.Index != 2)
                            throw new Exception("SET target or wire value changed");
                        if (outcome == "lost") throw new IOException("Connection lost after sending SET");
                        response = new LnResponse(request.Operation.Kind, request.InvokeIdAndPriority,
                            outcome == "reject" ? new OperationResult(CosemError.ReadWriteDenied) : OperationResult.Ok());
                    }
                    else
                    {
                        Gets++;
                        response = new LnResponse(request.Operation.Kind, request.InvokeIdAndPriority,
                            outcome == "read-fails" ? new OperationResult(CosemError.ReadWriteDenied) :
                                OperationResult.Ok(stored ?? DlmsValue.Number(DlmsType.UInt16, 78)));
                    }
                    break;
                default: throw new Exception("Unexpected APDU in write checks");
            }
            if (response is not null) replies.Writer.TryWrite(codec.EncodeBytes(response));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            await replies.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync() { replies.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
