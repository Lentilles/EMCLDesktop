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
using Xunit;

namespace EMCLDesktop.Tests;

public class AttributeWriteTests
{
    [Theory]
    [InlineData("ok", 1)]
    [InlineData("octet-text", 1)]
    [InlineData("reject", 1)]
    [InlineData("lost", 1)]
    [InlineData("read-fails", 1)]
    [InlineData("write-only", 1)]
    [InlineData("no-get", 1)]
    [InlineData("no-set", 0)]
    [InlineData("no-tree", 0)]
    [InlineData("boundary", 1)]
    [InlineData("boundary-plus", 1)]
    [InlineData("blocks-ok", 1)]
    [InlineData("blocks-reject-first", 1)]
    [InlineData("blocks-reject-middle", 1)]
    [InlineData("blocks-reject-last", 1)]
    [InlineData("blocks-lost", 1)]
    [InlineData("blocks-no-support", 0)]
    [InlineData("blocks-limit", 0)]
    [InlineData("blocks-too-small", 0)]
    [InlineData("blocks-bad-number", 1)]
    [InlineData("blocks-bad-invoke", 1)]
    [InlineData("blocks-bad-final", 1)]
    [InlineData("blocks-cancel", 1)]
    [InlineData("blocks-timeout", 1)]
    [InlineData("cancel-before", 0)]
    public async Task WriteAttributeAsync_VariousOutcomes_PreservesDraftAndNeverSendsExtraSet(string outcome, int expectedSets)
    {
        // Arrange
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.False(vm.IsBusy);
        Assert.Equal(draft, attribute.WriteText);
        Assert.Equal(expectedSets, channel.Sets);
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("no-set", false)]
    [InlineData("no-tree", false)]
    public async Task IsWriteEnabled_NegotiatedSetAndLiveCatalog_RequiresBoth(string outcome, bool expected)
    {
        // Arrange
        const string obis = "1.0.0.2.0.255";

        // Act
        var (vm, _, attribute, _, _) = await CreateWriteScenarioAsync(outcome, obis);
        await using var viewModel = vm;

        // Assert
        Assert.Equal(expected, attribute.IsWriteEnabled);
    }

    [Theory]
    [InlineData("boundary", 0)]
    [InlineData("boundary-plus", 2)]
    [InlineData("blocks-ok", 3)]
    public async Task WriteAttributeAsync_PduBoundary_UsesExpectedBlocksAndReadsBackAppliedValue(string outcome, int expectedBlocks)
    {
        // Arrange
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;
        var codec = new AxdrCodec();

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.False(vm.HasStatusError, "Large SET was not applied exactly once and read back");
        Assert.Equal(1, channel.WritesApplied);
        Assert.Equal(1, channel.Gets);
        Assert.NotNull(attribute.LastReadValue);
        Assert.Equal(codec.EncodeBytes(written), codec.EncodeBytes(attribute.LastReadValue));
        Assert.Equal(expectedBlocks, channel.BlockRequests);
    }

    [Theory]
    [InlineData("blocks-reject-first")]
    [InlineData("blocks-reject-middle")]
    [InlineData("blocks-reject-last")]
    public async Task WriteAttributeAsync_RejectedBlock_ReportsMeterResultAndKeepsConnection(string outcome)
    {
        // Arrange
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.True(vm.HasStatusError, "Block rejection lost meter result or faulted the connection");
        Assert.Equal(0, channel.WritesApplied);
        Assert.Equal(0, channel.Gets);
        Assert.Contains("ReadWriteDenied", attribute.WriteStatus);
        Assert.True(vm.IsConnected, "Block rejection lost meter result or faulted the connection");
    }

    [Theory]
    [InlineData("blocks-lost", 3, 1)]
    [InlineData("blocks-bad-final", 3, 1)]
    [InlineData("blocks-bad-number", 1, 0)]
    [InlineData("blocks-bad-invoke", 1, 0)]
    [InlineData("blocks-cancel", 2, 0)]
    [InlineData("blocks-timeout", 2, 0)]
    public async Task WriteAttributeAsync_MissingBlockConfirmation_DoesNotRetryAndReportsUnconfirmedWrite(string outcome, int expectedBlocks, int expectedApplied)
    {
        // Arrange
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.True(vm.HasStatusError, "Missing final confirmation retried an applied SET or reported a definite outcome");
        Assert.True(channel.WritesApplied == expectedApplied, "Missing final confirmation retried an applied SET or reported a definite outcome");
        Assert.True(channel.BlockRequests == expectedBlocks, "Missing final confirmation retried an applied SET or reported a definite outcome");
        Assert.Equal(0, channel.Gets);
        Assert.False(vm.IsConnected, "Missing final confirmation retried an applied SET or reported a definite outcome");
        Assert.Contains("не подтверждена", attribute.WriteStatus);
        Assert.Null(attribute.LastReadValue);
    }

    [Theory]
    [InlineData("blocks-bad-final")]
    [InlineData("blocks-bad-number")]
    [InlineData("blocks-bad-invoke")]
    public async Task WriteAttributeAsync_InvalidBlockReply_PreservesProtocolError(string outcome)
    {
        // Arrange
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.DoesNotContain("The SET outcome is unconfirmed", attribute.WriteStatus);
    }

    [Theory]
    [InlineData("blocks-no-support")]
    [InlineData("blocks-limit")]
    [InlineData("blocks-too-small")]
    public async Task WriteAttributeAsync_BlockPreflightFails_SendsNothingAndKeepsLastRead(string outcome)
    {
        // Arrange
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.True(vm.HasStatusError, "A failed preflight sent SET or discarded the last read");
        Assert.Equal(0, channel.BlockRequests);
        Assert.Equal(0, channel.WritesApplied);
        Assert.True(vm.IsConnected, "A failed preflight sent SET or discarded the last read");
        Assert.Contains("не выполнена", attribute.WriteStatus);
        Assert.Equal(11UL, attribute.LastReadValue?.AsUInt64());
    }

    [Fact]
    public async Task WriteAttributeAsync_CanceledBeforeSend_KeepsConnectionAndLastRead()
    {
        // Arrange
        const string outcome = "cancel-before";
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.False(vm.HasStatusError, "Cancellation before sending was reported as an uncertain write");
        Assert.Equal(0, channel.Gets);
        Assert.True(vm.IsConnected, "Cancellation before sending was reported as an uncertain write");
        Assert.Contains("до отправки", attribute.WriteStatus);
        Assert.Equal(11UL, attribute.LastReadValue?.AsUInt64());
    }

    [Fact]
    public async Task WriteAttributeAsync_SuccessfulSet_ShowsActualReadback()
    {
        // Arrange
        const string outcome = "ok";
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.Equal(1, channel.Gets);
        Assert.Equal($"78{Environment.NewLine}0x004E", attribute.Value);
        Assert.Equal("78", attribute.DisplayedValue);
        Assert.Equal("0x004E", attribute.NumericHexValue);
        Assert.False(vm.HasStatusError, "Write did not show the actual read-back value");
    }

    [Fact]
    public async Task WriteAttributeAsync_Windows1251Draft_SendsEncodedOctetsAndShowsDecodedReadback()
    {
        // Arrange
        const string outcome = "octet-text";
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.Equal(1, channel.Gets);
        Assert.Equal("DF", attribute.Value);
        Assert.Equal("Я", attribute.DisplayedValue);
        Assert.False(vm.HasStatusError, "SET sent an incorrect OctetString type or encoding");
    }

    [Fact]
    public async Task WriteAttributeAsync_MeterRejectsSet_ReportsErrorAndPreservesPreviousValue()
    {
        // Arrange
        const string outcome = "reject";
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.True(vm.HasStatusError, "Refusal lost the device error or changed read value");
        Assert.Equal(0, channel.Gets);
        Assert.Equal("Old read", attribute.Value);
        Assert.Contains("ReadWriteDenied", attribute.WriteStatus);
    }

    [Fact]
    public async Task WriteAttributeAsync_SetReplyLost_ReportsUnconfirmedWriteAndClosesConnection()
    {
        // Arrange
        const string outcome = "lost";
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.True(vm.HasStatusError, "Lost reply was shown as a definite failure or success");
        Assert.False(vm.IsConnected, "Lost reply was shown as a definite failure or success");
        Assert.Contains("не подтверждена", attribute.WriteStatus);
    }

    [Fact]
    public async Task WriteAttributeAsync_ReadbackFails_PreservesSetConfirmation()
    {
        // Arrange
        const string outcome = "read-fails";
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.True(vm.HasStatusError, "Read-back failure masked confirmed SET");
        Assert.Contains("Запись подтверждена, но", attribute.WriteStatus);
        Assert.Null(attribute.LastReadValue);
    }

    [Theory]
    [InlineData("write-only")]
    [InlineData("no-get")]
    public async Task WriteAttributeAsync_ReadUnavailable_SkipsReadback(string outcome)
    {
        // Arrange
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;

        // Act
        await vm.WriteAttributeAsync(attribute);

        // Assert
        Assert.Equal(0, channel.Gets);
        Assert.False(vm.HasStatusError, "Read-back was attempted without read access or negotiated GET");
        Assert.Contains("не считано", attribute.Value);
    }

    [Theory]
    [InlineData(0, ClientAuthentication.None, false)]
    [InlineData(1, ClientAuthentication.None, false)]
    [InlineData(2, ClientAuthentication.None, true)]
    [InlineData(3, ClientAuthentication.None, true)]
    [InlineData(4, ClientAuthentication.None, false)]
    [InlineData(5, ClientAuthentication.None, false)]
    [InlineData(6, ClientAuthentication.None, false)]
    [InlineData(0, ClientAuthentication.High, false)]
    [InlineData(1, ClientAuthentication.High, false)]
    [InlineData(2, ClientAuthentication.High, true)]
    [InlineData(3, ClientAuthentication.High, true)]
    [InlineData(4, ClientAuthentication.High, false)]
    [InlineData(5, ClientAuthentication.High, true)]
    [InlineData(6, ClientAuthentication.High, true)]
    public void AllowsWrite_VersionOneAccessMode_RespectsAuthentication(byte mode, ClientAuthentication authentication, bool expected)
    {
        // Arrange
        var access = new AttributeAccess(2, mode, []);

        // Act
        var allowed = MeterConnection.AllowsWrite(access, 1, authentication);

        // Assert
        Assert.Equal(expected, allowed);
    }

    [Fact]
    public void AllowsWrite_VersionThreeAccessModes_OnlyAllowsUnprotectedWritableModes()
    {
        // Arrange
        var modes = Enumerable.Range(0, 256).Select(mode => (byte)mode);

        // Act
        var allowed = modes.Where(mode => MeterConnection.AllowsWrite(
            new AttributeAccess(2, mode, []), 3, ClientAuthentication.High)).ToArray();

        // Assert
        Assert.Equal(new byte[] { 2, 3 }, allowed);
    }

    [Fact]
    public async Task Constructor_NewConnection_EnablesSetBlockTransfer()
    {
        // Arrange
        const DlmsConformance capability = DlmsConformance.BlockTransferWithSet;

        // Act
        await using var settings = new MainViewModel();

        // Assert
        Assert.True(settings.ConformanceOptions.Single(option => option.Flag == capability).IsSelected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadConfigurationAsync_SavedSetBlockCapability_RestoresSelection(bool enabled)
    {
        // Arrange
        await using var settings = new MainViewModel { Host = "localhost", TcpPort = "4059" };
        await using var restored = new MainViewModel();
        var option = settings.ConformanceOptions.Single(o => o.Flag == DlmsConformance.BlockTransferWithSet);
        option.IsSelected = enabled;
        await using var json = new MemoryStream();
        await settings.SaveConfigurationAsync(json);
        json.Position = 0;

        // Act
        await restored.LoadConfigurationAsync(json);

        // Assert
        Assert.False(restored.HasStatusError, "Saved SET block capability was not restored");
        Assert.True(restored.ConformanceOptions.Single(o => o.Flag == DlmsConformance.BlockTransferWithSet).IsSelected == enabled, "Saved SET block capability was not restored");
    }

    [Theory]
    [InlineData("batch-ok", false)]
    [InlineData("batch-reject", true)]
    public async Task WritePendingCommand_BatchResult_ClearsConfirmedWritesAndStopsOnRejection(string outcome, bool rejected)
    {
        // Arrange
        var (vm, channel, attribute, _, _) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;
        var obj = vm.Objects.Single();
        var structure = new AttributeViewModel(3, "structure", "Structure", "", true) { IsWriteEnabled = true };
        var later = new AttributeViewModel(4, "later", "UInt16", "", true) { IsWriteEnabled = true };
        obj.Attributes.Add(structure);
        obj.Attributes.Add(later);
        var original = DlmsValue.Structure(DlmsValue.Array(DlmsValue.Number(DlmsType.UInt16, 11)));
        structure.RememberReadValue(original);
        var editor = new AttributeDialogViewModel(vm, obj, structure, edit: false);
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();
        editor.BeginEdit();
        editor.LeafText = "77";
        editor.Apply();
        foreach (var scalar in new[] { attribute, later })
        {
            var scalarEditor = new AttributeDialogViewModel(vm, obj, scalar, edit: true);
            scalarEditor.Editor.WriteText = "77";
            scalarEditor.Apply();
        }

        // Act
        await vm.WritePendingCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsBusy, "Batch replayed a confirmed SET, dropped an unconfirmed value, or continued after rejection");
        Assert.False(attribute.HasPendingValue, "Batch replayed a confirmed SET, dropped an unconfirmed value, or continued after rejection");
        Assert.True(structure.HasPendingValue == rejected, "Batch replayed a confirmed SET, dropped an unconfirmed value, or continued after rejection");
        Assert.True(later.HasPendingValue == rejected, "Batch replayed a confirmed SET, dropped an unconfirmed value, or continued after rejection");
        Assert.Equal(rejected ? new sbyte[] { 2, 3 } : new sbyte[] { 2, 3, 4 }, channel.WriteIndexes);
        Assert.True(vm.HasStatusError == rejected, "Batch status lost confirmed write count");
        Assert.Contains(rejected ? "1 из 3" : "3 из 3", vm.Status);
    }

    [Fact]
    public async Task Apply_ScalarAndNestedDrafts_StagesThreeValuesWithoutWriting()
    {
        // Arrange
        const string outcome = "batch-ok";
        var (vm, channel, attribute, _, _) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;
        var obj = vm.Objects.Single();
        var structure = new AttributeViewModel(3, "structure", "Structure", "", true) { IsWriteEnabled = true };
        var later = new AttributeViewModel(4, "later", "UInt16", "", true) { IsWriteEnabled = true };
        obj.Attributes.Add(structure);
        obj.Attributes.Add(later);
        var original = DlmsValue.Structure(DlmsValue.Array(DlmsValue.Number(DlmsType.UInt16, 11)));
        structure.RememberReadValue(original);
        var editor = new AttributeDialogViewModel(vm, obj, structure, edit: false);
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();
        editor.BeginEdit();
        editor.LeafText = "77";

        // Act
        editor.Apply();
        foreach (var scalar in new[] { attribute, later })
        {
            var scalarEditor = new AttributeDialogViewModel(vm, obj, scalar, edit: true);
            scalarEditor.Editor.WriteText = "77";
            scalarEditor.Apply();
        }
        var pending = new[] { attribute, structure, later };

        // Assert
        Assert.Equal(0, channel.Sets);
        Assert.True(vm.WritePendingCommand.CanExecute(null), "Staging performed a write or failed to update the action");
        Assert.EndsWith("(3)", vm.WritePendingText);
        Assert.All(pending, item => Assert.True(item.HasPendingValue));
    }

    [Fact]
    public void IncludeParameter_NullArgument_HidesValueInput()
    {
        // Arrange
        const string type = "Null";

        // Act
        var method = new MethodViewModel(1, "null_argument", type, "");

        // Assert
        Assert.True(method.IncludeParameter, "Null still displayed a value input");
        Assert.False(method.ShowParameterValue, "Null still displayed a value input");
    }

    [Theory]
    [InlineData(true, DlmsType.Null)]
    [InlineData(false, null)]
    public async Task InvokeMethodAsync_NullParameterSelection_DistinguishesExplicitNullFromOmittedArgument(bool includeParameter, DlmsType? expected)
    {
        // Arrange
        const string outcome = "batch-ok";
        var (vm, channel, attribute, written, draft) = await CreateWriteScenarioAsync(outcome);
        await using var viewModel = vm;
        var method = new MethodViewModel(1, "null_argument", "Null", "") { IncludeParameter = includeParameter };
        vm.Objects.Single().Methods.Add(method);

        // Act
        await vm.InvokeMethodAsync(method);

        // Assert
        var parameter = Assert.Single(channel.ActionParameters);
        Assert.Equal(expected, parameter?.Type);
        Assert.False(vm.HasStatusError);
    }

    private static async Task<(MainViewModel ViewModel, MeterChannel Channel, AttributeViewModel Attribute,
        DlmsValue Written, string Draft)> CreateWriteScenarioAsync(string outcome, string obis = "1.0.0.2.0.255")
    {
        bool batch = outcome.StartsWith("batch-", StringComparison.Ordinal);
        var channel = new MeterChannel(outcome);
        var transferLimits = new ProtocolLimits { MaxBlockCount = outcome == "blocks-limit" ? 2 : 4096 };
        var session = new DlmsClientSession(channel, new ClientSessionOptions
        {
            Limits = transferLimits,
            RequestTimeout = TimeSpan.FromMilliseconds(outcome == "blocks-timeout" ? 250 : 2000),
            Association = new LnAssociationOptions { Authentication = ClientAuthentication.None }
        });
        await session.AssociateAsync();
        var name = LogicalName.Parse(obis);
        var options = new MeterConnectionOptions("test", 1, "Wrapper", 16, 1, null, false, 1,
            "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 1, 1, 3600, 1024,
            LnAssociationOptions.SupportedConformance, false, transferLimits);
        var meter = (MeterConnection)Activator.CreateInstance(typeof(MeterConnection), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [session, ClientAuthentication.None, LogicalName.Parse(options.AssociationObis), TimeSpan.FromHours(1), options], null)!;
        if (outcome != "no-tree")
        {
            DlmsValue Access(int index) => DlmsValue.Structure(DlmsValue.Number(DlmsType.Int8, index),
                DlmsValue.Number(DlmsType.Enum, outcome == "write-only" ? 2 : 3), DlmsValue.Null);
            var entry = DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 1), DlmsValue.Number(DlmsType.UInt8, 0),
                DlmsValue.Octets(name.ToBytes()), DlmsValue.Structure(
                    batch ? DlmsValue.Array(Access(2), Access(3), Access(4)) : DlmsValue.Array(Access(2)),
                    batch ? DlmsValue.Array(DlmsValue.Structure(DlmsValue.Number(DlmsType.Int8, 1),
                        DlmsValue.Number(DlmsType.Enum, 1))) : DlmsValue.Array()));
            var imported = new EmclBuilder().CreateImporter().Import(DlmsValue.Array(entry), "test", "test", 1);
            typeof(MeterConnection).GetProperty(nameof(MeterConnection.Association))!.SetValue(meter, imported);
        }
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("connection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, meter);
        void CancelWrite() => ((CancellationTokenSource)typeof(MainViewModel)
            .GetField("shutdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Cancel();
        channel.CancelWrite = CancelWrite;
        vm.IsConnected = true;
        var obj = new MeterObjectViewModel(name, 1, 0, "Data");
        var written = outcome == "octet-text" ? DlmsValue.Octets([0xDF]) :
            channel.UsesSmallPdu ? DlmsValue.Octets(Enumerable.Range(0, channel.PayloadLength).Select(i => (byte)i).ToArray()) :
            DlmsValue.Number(DlmsType.UInt16, 77);
        var draft = DlmsValueEditor.Format(written);
        var attribute = new AttributeViewModel(2, "value", null, "", true)
            { WriteType = written.Type, WriteText = draft, Value = "Old read" };
        attribute.RememberReadValue(DlmsValue.Number(DlmsType.UInt16, 11));
        obj.Attributes.Add(attribute);
        vm.Objects.Add(obj);
        vm.SelectedObject = obj;
        if (outcome == "octet-text")
        {
            var windows1251 = OctetStringFormat.Options.Single(format => format.Label == "Windows-1251");
            attribute.TryChangeWriteOctetFormat(windows1251);
            attribute.ReadOctetFormat = windows1251;
            attribute.WriteText = draft = "Я";
        }
        if (outcome == "cancel-before") CancelWrite();
        return (vm, channel, attribute, written, draft);
    }

    private sealed class MeterChannel(string outcome) : IApduChannel
    {
        private readonly Channel<ReadOnlyMemory<byte>> replies = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly ApduCodec codec = new();
        public int Sets { get; private set; }
        public List<sbyte> WriteIndexes { get; } = [];
        public List<DlmsValue?> ActionParameters { get; } = [];
        public int Gets { get; private set; }
        public int BlockRequests { get; private set; }
        public int WritesApplied { get; private set; }
        public bool UsesSmallPdu => outcome.StartsWith("blocks-") || outcome.StartsWith("boundary");
        public int PayloadLength => outcome == "boundary" ? 49 : outcome == "boundary-plus" ? 50 : 140;
        public Action? CancelWrite { get; set; }
        private readonly MemoryStream blockData = new();
        private uint expectedBlock = 1;
        private DlmsValue? stored;

        private DlmsApdu? AcceptBlock(byte invoke, bool last, uint number, ImmutableArray<byte> data)
        {
            BlockRequests++;
            if (number != expectedBlock++) throw new Exception("SET block number repeated or skipped");
            blockData.Write(data.AsSpan());
            if (outcome == "blocks-bad-number") return new SetBlockResponse(invoke, number + 1);
            if (outcome == "blocks-bad-invoke") return new SetBlockResponse((byte)(invoke ^ 0x80), number);
            if (outcome is "blocks-cancel" or "blocks-timeout" && number == 2)
            {
                if (outcome == "blocks-cancel") CancelWrite!();
                return null;
            }
            if (outcome == "blocks-reject-first" && number == 1 ||
                outcome == "blocks-reject-middle" && number == 2 ||
                outcome == "blocks-reject-last" && last)
                return new SetBlockResponse(invoke, number, CosemResultCode.ReadWriteDenied);
            if (!last) return new SetBlockResponse(invoke, number);
            var decoded = new AxdrCodec().Decode(new ReadOnlySequence<byte>(blockData.ToArray()));
            if (decoded.Status != DecodeStatus.Done || decoded.Consumed != blockData.Length ||
                !decoded.Value!.Bytes.SequenceEqual(Enumerable.Range(0, PayloadLength).Select(i => (byte)i)))
                throw new Exception("SET blocks changed the encoded value");
            stored = decoded.Value;
            WritesApplied++;
            if (outcome == "blocks-lost") { replies.Writer.TryComplete(); return null; }
            if (outcome == "blocks-bad-final") return new SetBlockResponse(invoke, number);
            return new SetBlockResponse(invoke, number, CosemResultCode.Success);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken = default)
        {
            DlmsApdu? response;
            int pduLimit = outcome == "blocks-too-small" ? 19 : UsesSmallPdu ? 64 : 1024;
            if (apdu.Span[0] == (byte)ApduTag.SetRequest && apdu.Length > pduLimit)
                throw new Exception("SET exceeded the server PDU");
            var decoded = codec.Decode(new ReadOnlySequence<byte>(apdu));
            switch (decoded.Value)
            {
                case AcseApdu { Kind: AcseKind.Aarq }:
                    var flags = LnAssociationOptions.SupportedConformance & ~DlmsConformance.PriorityManagement;
                    if (outcome == "no-get") flags &= ~DlmsConformance.Get;
                    if (outcome == "no-set") flags &= ~DlmsConformance.Set;
                    if (outcome == "blocks-no-support") flags &= ~DlmsConformance.BlockTransferWithSet;
                    response = AcseApdu.Aare(codec.EncodeBytes(new InitiateResponse(null, DlmsNegotiation.Version6, (uint)flags, (ushort)pduLimit)));
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
                        if (outcome.StartsWith("batch-", StringComparison.Ordinal))
                        {
                            WriteIndexes.Add(request.Operation.Index);
                            bool rejected = outcome == "batch-reject" && request.Operation.Index == 3;
                            stored = request.Operation.Value;
                            response = new LnResponse(CosemOperationKind.Write, request.InvokeIdAndPriority,
                                rejected ? new OperationResult(CosemResultCode.ReadWriteDenied) : OperationResult.Ok());
                            break;
                        }
                        if (outcome is "boundary" or "octet-text")
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
                            outcome == "reject" ? new OperationResult(CosemResultCode.ReadWriteDenied) : OperationResult.Ok());
                    }
                    else if (request.Operation.Kind == CosemOperationKind.Invoke)
                    {
                        ActionParameters.Add(request.Operation.Value);
                        response = new LnResponse(CosemOperationKind.Invoke, request.InvokeIdAndPriority, OperationResult.Ok());
                    }
                    else
                    {
                        Gets++;
                        response = new LnResponse(request.Operation.Kind, request.InvokeIdAndPriority,
                            outcome == "read-fails" ? new OperationResult(CosemResultCode.ReadWriteDenied) :
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
