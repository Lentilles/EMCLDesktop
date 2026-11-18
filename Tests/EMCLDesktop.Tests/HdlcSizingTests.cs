using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using EMCL.Client.Association;
using EMCL.Core.Codecs;
using EMCL.Framing;
using EMCL.Framing.Hdlc.Frames;
using EMCL.Protocol.Apdu;
using EMCL.Transport.Channels;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public class HdlcSizingTests
{
    [Fact]
    public async Task SaveConfigurationAsync_DefaultHdlcSettings_ProposesMaximumLinkedSizes()
    {
        // Arrange
        await using var vm = CreateViewModel();

        // Act
        var initial = await SaveAsync(vm);

        // Assert
        Assert.True(vm.UseHdlcLinkParameters, "New connections did not propose the maximum TX/RX sizes by default");
        Assert.Equal("2030", vm.HdlcMaxTxBytes);
        Assert.Equal("2030", vm.HdlcMaxRxBytes);
        Assert.Equal(HdlcNegotiation.MaximumInformationBytes, initial["channel"]!["framing"]!["hdlcLinkParameters"]!["maximumTransmitInformationBytes"]!.GetValue<int>());
        Assert.Equal(HdlcNegotiation.MaximumInformationBytes, initial["channel"]!["framing"]!["hdlcLinkParameters"]!["maximumReceiveInformationBytes"]!.GetValue<int>());
        Assert.True(vm.AreHdlcSizesLinked, "Equal default sizes were not linked");
    }

    [Fact]
    public async Task CreateFraming_DefaultHdlcSettings_ProducesMaximumSnrmProposal()
    {
        // Arrange
        await using var vm = CreateViewModel();

        // Act
        var framing = (ApduFraming.Hdlc)ParseOptions(vm).CreateFraming();

        // Assert
        Assert.Equal("818014050207EE060207EE070400000001080400000001",
            Convert.ToHexString(HdlcNegotiation.CreateSnrmInformation(framing.LinkParameters).AsSpan()));
    }

    [Fact]
    public async Task HdlcMaxTxBytes_LinkedSizes_UpdatesRx()
    {
        // Arrange
        await using var vm = CreateViewModel();

        // Act
        vm.HdlcMaxTxBytes = "571";

        // Assert
        Assert.Equal("571", vm.HdlcMaxRxBytes);
    }

    [Fact]
    public async Task HdlcMaxRxBytes_LinkedSizes_UpdatesTx()
    {
        // Arrange
        await using var vm = CreateViewModel();

        // Act
        vm.HdlcMaxRxBytes = "1024";

        // Assert
        Assert.Equal("1024", vm.HdlcMaxTxBytes);
    }

    [Fact]
    public async Task HdlcMaxTxBytes_UnlinkedSizes_LeavesRxUnchanged()
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.HdlcMaxRxBytes = "1024";
        vm.AreHdlcSizesLinked = false;

        // Act
        vm.HdlcMaxTxBytes = "256";

        // Assert
        Assert.Equal("1024", vm.HdlcMaxRxBytes);
    }

    [Fact]
    public async Task HdlcMaxRxBytes_UnlinkedSizes_LeavesTxUnchanged()
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = false;
        vm.HdlcMaxTxBytes = "256";

        // Act
        vm.HdlcMaxRxBytes = "512";

        // Assert
        Assert.Equal("256", vm.HdlcMaxTxBytes);
    }

    [Fact]
    public async Task AreHdlcSizesLinked_Relinking_CopiesTxToRxAndUpdatesAction()
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = false;
        vm.HdlcMaxTxBytes = "256";
        vm.HdlcMaxRxBytes = "512";

        // Act
        vm.AreHdlcSizesLinked = true;

        // Assert
        Assert.Equal("256", vm.HdlcMaxRxBytes);
        Assert.Contains("Развязать", vm.HdlcSizesLinkActionText);
    }

    [Fact]
    public async Task HdlcMaxRxBytes_LinkedSizes_AllowsClearingBothFields()
    {
        // Arrange
        await using var vm = CreateViewModel();

        // Act
        vm.HdlcMaxRxBytes = "";

        // Assert
        Assert.Equal("", vm.HdlcMaxTxBytes);
    }

    [Fact]
    public async Task LoadConfigurationAsync_EqualHdlcSizes_RestoresEditingLink()
    {
        // Arrange
        await using var vm = CreateViewModel();
        var initial = await SaveAsync(vm);
        vm.AreHdlcSizesLinked = false;
        vm.HdlcMaxRxBytes = "";

        // Act
        await LoadAsync(vm, initial);

        // Assert
        Assert.True(vm.AreHdlcSizesLinked, "Equal imported values did not restore their editing link");
        Assert.Equal("2030", vm.HdlcMaxTxBytes);
        Assert.Equal("2030", vm.HdlcMaxRxBytes);
    }

    [Fact]
    public async Task SaveConfigurationAsync_AsymmetricHdlcSizes_WritesBothSizesAndSingleFrameWindows()
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = false;
        vm.UseHdlcLinkParameters = true;
        vm.HdlcMaxTxBytes = "1024";
        vm.HdlcMaxRxBytes = "2030";

        // Act
        var saved = await SaveAsync(vm);

        // Assert
        var sizes = saved["channel"]!["framing"]!["hdlcLinkParameters"]!;
        Assert.Equal(1024, sizes["maximumTransmitInformationBytes"]!.GetValue<int>());
        Assert.Equal(2030, sizes["maximumReceiveInformationBytes"]!.GetValue<int>());
        Assert.Equal(1, sizes["transmitWindowFrames"]!.GetValue<int>());
        Assert.Equal(1, sizes["receiveWindowFrames"]!.GetValue<int>());
    }

    [Fact]
    public async Task LoadConfigurationAsync_AsymmetricHdlcSizes_RestoresSizesAndDisablesEditingLink()
    {
        // Arrange
        await using var vm = CreateViewModel();
        var saved = await CreateAsymmetricConfigurationAsync();
        vm.UseHdlcLinkParameters = false;
        vm.HdlcMaxTxBytes = "128";
        vm.HdlcMaxRxBytes = "128";

        // Act
        await LoadAsync(vm, saved);

        // Assert
        Assert.False(vm.HasStatusError, "Explicit sizes did not survive JSON round trip");
        Assert.True(vm.UseHdlcLinkParameters, "Explicit sizes did not survive JSON round trip");
        Assert.False(vm.AreHdlcSizesLinked, "Explicit sizes did not survive JSON round trip");
        Assert.Equal("1024", vm.HdlcMaxTxBytes);
        Assert.Equal("2030", vm.HdlcMaxRxBytes);
    }

    [Fact]
    public async Task CreateFraming_ConfiguredHdlcSizes_UsesLimitsForSnrm()
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = false;
        vm.UseHdlcLinkParameters = true;
        vm.HdlcMaxTxBytes = "1024";
        vm.HdlcMaxRxBytes = "2030";
        var options = ParseOptions(vm);
        var trace = new DiagnosticTrace();

        // Act
        var framing = (ApduFraming.Hdlc)options.CreateFraming(trace);

        // Assert
        Assert.Equal(new HdlcLinkParameters(1024, 2030), framing.LinkParameters);
        Assert.Equal("81801405020400060207EE070400000001080400000001", Convert.ToHexString(HdlcNegotiation.CreateSnrmInformation(framing.LinkParameters).AsSpan()));
    }

    [Fact]
    public async Task NegotiationObserver_AsymmetricPeerLimits_LogsClientPeerAndEffectiveSizes()
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = false;
        vm.UseHdlcLinkParameters = true;
        vm.HdlcMaxTxBytes = "1024";
        vm.HdlcMaxRxBytes = "2030";
        var options = ParseOptions(vm);
        var trace = new DiagnosticTrace();
        var framing = (ApduFraming.Hdlc)options.CreateFraming(trace);
        var result = HdlcNegotiation.Negotiate(framing.LinkParameters,
            [.. Convert.FromHexString("8180140502020006020100070400000001080400000001")]);

        // Act
        framing.NegotiationObserver!(result);

        // Assert
        Assert.Equal(256, result.Effective.MaximumTransmitInformationBytes);
        Assert.Equal(512, result.Effective.MaximumReceiveInformationBytes);
        Assert.Contains("SNRM клиента: MAX TX=1024, MAX RX=2030", trace.Snapshot());
        Assert.Contains("UA счётчика: MAX TX=512, MAX RX=256", trace.Snapshot());
        Assert.Contains("итог клиента: MAX TX=256, MAX RX=512", trace.Snapshot());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("0", true)]
    [InlineData("2", false)]
    [InlineData("2", true)]
    [InlineData("2031", false)]
    [InlineData("2031", true)]
    [InlineData("65535", false)]
    [InlineData("65535", true)]
    [InlineData("-1", false)]
    [InlineData("-1", true)]
    [InlineData("1.5", false)]
    [InlineData("1.5", true)]
    [InlineData("abc", false)]
    [InlineData("abc", true)]
    public async Task SaveConfigurationAsync_InvalidHdlcSize_ThrowsArgumentException(string invalid, bool tx)
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = false;
        vm.HdlcMaxTxBytes = tx ? invalid : "128";
        vm.HdlcMaxRxBytes = tx ? "128" : invalid;

        // Act
        var exception = await Record.ExceptionAsync(() => SaveAsync(vm));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(128)]
    [InlineData(HdlcNegotiation.MaximumInformationBytes)]
    public async Task SaveConfigurationAsync_SupportedHdlcSize_PersistsTransmitLimit(int boundary)
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.HdlcMaxTxBytes = boundary.ToString();
        vm.HdlcMaxRxBytes = boundary.ToString();

        // Act
        var saved = await SaveAsync(vm);

        // Assert
        Assert.Equal(boundary, saved["channel"]!["framing"]!["hdlcLinkParameters"]!["maximumTransmitInformationBytes"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("HDLC", false)]
    [InlineData("Wrapper", true)]
    public async Task SaveConfigurationAsync_InactiveHdlcParameters_OmitsInvalidSizes(string framing, bool useHdlcLinkParameters)
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.Framing = framing;
        vm.UseHdlcLinkParameters = useHdlcLinkParameters;
        vm.HdlcMaxTxBytes = "invalid";
        vm.HdlcMaxRxBytes = "invalid";

        // Act
        var saved = await SaveAsync(vm);

        // Assert
        Assert.Null(saved["channel"]!["framing"]!["hdlcLinkParameters"]);
    }

    [Fact]
    public async Task LoadConfigurationAsync_WithoutHdlcProposal_DisablesLinkNegotiation()
    {
        // Arrange
        await using var vm = CreateViewModel();
        var initial = await SaveAsync(vm);
        var oldConfiguration = initial.DeepClone();
        oldConfiguration["channel"]!["framing"]!.AsObject().Remove("hdlcLinkParameters");

        // Act
        await LoadAsync(vm, oldConfiguration);
        var saved = await SaveAsync(vm);

        // Assert
        Assert.False(vm.HasStatusError, "Old configurations inherited a previously enabled proposal");
        Assert.False(vm.UseHdlcLinkParameters, "Old configurations inherited a previously enabled proposal");
        Assert.Equal("2030", vm.HdlcMaxTxBytes);
        Assert.Equal("2030", vm.HdlcMaxRxBytes);
        Assert.Null(saved["channel"]!["framing"]!["hdlcLinkParameters"]);
    }

    [Fact]
    public async Task LoadConfigurationAsync_AsymmetricSizesWithActiveLink_PreservesDistinctSizes()
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = true;
        var saved = await CreateAsymmetricConfigurationAsync();

        // Act
        await LoadAsync(vm, saved);

        // Assert
        Assert.False(vm.AreHdlcSizesLinked, "An active editing link overwrote asymmetric imported values");
        Assert.Equal("1024", vm.HdlcMaxTxBytes);
        Assert.Equal("2030", vm.HdlcMaxRxBytes);
    }

    [Theory]
    [InlineData("maximumReceiveInformationBytes", 2031)]
    [InlineData("transmitWindowFrames", 2)]
    [InlineData("receiveWindowFrames", 8)]
    public async Task LoadConfigurationAsync_InvalidHdlcParameters_ReportsErrorAndPreservesSettings(string field, int value)
    {
        // Arrange
        await using var vm = CreateViewModel();
        vm.UseHdlcLinkParameters = false;
        var invalid = await CreateAsymmetricConfigurationAsync();
        invalid["channel"]!["framing"]!["hdlcLinkParameters"]![field] = value;

        // Act
        await LoadAsync(vm, invalid);

        // Assert
        Assert.True(vm.HasStatusError, "Invalid imported sizes/windows partially changed the configuration");
        Assert.False(vm.UseHdlcLinkParameters, "Invalid imported sizes/windows partially changed the configuration");
        Assert.True(vm.AreHdlcSizesLinked, "Invalid imported sizes/windows partially changed the configuration");
        Assert.Equal("2030", vm.HdlcMaxTxBytes);
    }

    [Fact]
    public void CreateFraming_LegacyHdlcSettings_UsesEmptySnrmProposal()
    {
        // Arrange
        var legacy = CreateLegacyOptions();

        // Act
        var framing = (ApduFraming.Hdlc)legacy.CreateFraming();

        // Assert
        Assert.Null(framing.LinkParameters);
        Assert.Empty(HdlcNegotiation.CreateSnrmInformation(framing.LinkParameters));
    }

    [Fact]
    public void CreateFraming_WrapperWithHdlcParameters_ReturnsWrapper()
    {
        // Arrange
        var options = CreateLegacyOptions() with { Framing = "Wrapper", HdlcParameters = new HdlcLinkParameters(0, 0) };

        // Act
        var framing = options.CreateFraming();

        // Assert
        Assert.IsType<ApduFraming.Wrapper>(framing);
    }

    [Theory]
    [InlineData(false, 128, 257)]
    [InlineData(true, 128, 257)]
    [InlineData(false, 571, 571)]
    public async Task ToggleConnectionCommand_OversizedHdlcResponse_ReportsLimitAndClosesSession(bool explicitSizes, int peerSize, int responseSize)
    {
        // Arrange
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connectionVm = new MainViewModel
        {
            Host = "127.0.0.1", TcpPort = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(),
            Framing = "HDLC", Authentication = "None", UseHdlcLinkParameters = explicitSizes,
            AreHdlcSizesLinked = false, HdlcMaxTxBytes = "1024", HdlcMaxRxBytes = "512"
        };
        var server = ServeInformationFrameAsync(listener, explicitSizes ? new(1024, 512) : null,
            peerSize, responseSize, deadline.Token);

        // Act
        await connectionVm.ToggleConnectionCommand.ExecuteAsync(null).WaitAsync(deadline.Token);
        await server;

        // Assert
        Assert.True(connectionVm.HasStatusError, "Oversized response lost its actionable error or original lengths");
        Assert.Contains("больше согласованного MAX RX", connectionVm.Status);
        Assert.Contains($"{responseSize} bytes", connectionVm.Status);
        Assert.Contains("128 bytes", connectionVm.Status);
        Assert.Contains("включено согласование MAX TX / MAX RX", connectionVm.Status);
        Assert.False(connectionVm.IsConnected, "Oversized response left a connected or busy session");
        Assert.False(connectionVm.IsConnecting, "Oversized response left a connected or busy session");
        Assert.False(connectionVm.IsBusy, "Oversized response left a connected or busy session");
        string history = connectionVm.GetTraceSnapshot();
        Assert.Contains("Счётчик превысил согласованный MAX RX", history);
        Assert.True((!explicitSizes || history.Contains("Задано клиентом: MAX TX=1024, MAX RX=512")), "Oversized response lost its original diagnostic or configured limits in the trace");
        Assert.False(listener.Pending(), "Connection retried without an API to override the incorrect UA limits");
    }

    [Theory]
    [InlineData(571)]
    [InlineData(HdlcNegotiation.MaximumInformationBytes)]
    public async Task ReceiveAsync_NegotiatedSizeAtLeast571_ReceivesCompleteInformationFrame(int proposedSize)
    {
        // Arrange
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var legacy = CreateLegacyOptions();
        listener.Start();
        var traceSizes = new HdlcLinkParameters(proposedSize, proposedSize);
        var configured = legacy with { Host = "127.0.0.1", TcpPort = ((IPEndPoint)listener.LocalEndpoint).Port,
            HdlcParameters = traceSizes };
        var server = ServeInformationFrameAsync(listener, traceSizes, 571, 571, deadline.Token);

        // Act
        ReadOnlyMemory<byte>? response;
        await using (var channel = await ApduChannels.ConnectAsync(
            new(configured.CreateTransportFactory(), configured.CreateFraming(), configured.Limits), deadline.Token))
        {
            await channel.SendAsync(new byte[] { 1 }, deadline.Token);
            response = await channel.ReceiveAsync(deadline.Token);
        }
        await server;

        // Assert
        Assert.True(response is { Length: 568 } && response.Value.Span.IndexOfAnyExcept((byte)0) < 0,
            $"Proposal {proposedSize}/{proposedSize} failed to receive a valid 571-byte information field");
    }

    private static MainViewModel CreateViewModel() =>
        new() { Host = "127.0.0.1", TcpPort = "4059", Framing = "HDLC" };

    private static MeterConnectionOptions ParseOptions(MainViewModel viewModel) =>
        (MeterConnectionOptions)typeof(MainViewModel).GetMethod("ParseOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null)!;

    private static async Task<JsonNode> SaveAsync(MainViewModel vm)
        {
            await using var stream = new MemoryStream();
            await vm.SaveConfigurationAsync(stream);
            return JsonNode.Parse(stream.ToArray())!;
        }

    private static async Task LoadAsync(MainViewModel vm, JsonNode json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        await vm.LoadConfigurationAsync(stream);
    }

    private static async Task<JsonNode> CreateAsymmetricConfigurationAsync()
    {
        await using var vm = CreateViewModel();
        vm.AreHdlcSizesLinked = false;
        vm.UseHdlcLinkParameters = true;
        vm.HdlcMaxTxBytes = "1024";
        vm.HdlcMaxRxBytes = "2030";
        return await SaveAsync(vm);
    }

    private static MeterConnectionOptions CreateLegacyOptions() =>
        new MeterConnectionOptions("localhost", 4059, "HDLC", 16, 1, null, false, 1,
            "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 10, 30, 20, 65535,
            DlmsConformance.Get, false, new ProtocolLimits());

    private static async Task ServeInformationFrameAsync(TcpListener listener, HdlcLinkParameters? proposal,
        int peerSize, int responseSize, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = peer.GetStream();
        var codec = new HdlcCodec();
        var snrm = await ReadFrameAsync();
        if (snrm.Control != HdlcControl.Snrm || !snrm.Information.AsSpan().SequenceEqual(
                HdlcNegotiation.CreateSnrmInformation(proposal).AsSpan()))
            throw new Exception("HDLC sizes: unexpected SNRM proposal");
        await WriteFrameAsync(new(snrm.Source, snrm.Destination, HdlcControl.Ua,
            HdlcNegotiation.CreateSnrmInformation(new(peerSize, peerSize))));
        if (!(await ReadFrameAsync()).IsInformation)
            throw new Exception("HDLC sizes: expected client information frame");
        await WriteFrameAsync(new(snrm.Source, snrm.Destination, 0x30,
            [0xE6, 0xE7, 0, .. new byte[responseSize - 3]]));
        if ((await ReadFrameAsync()).Control != HdlcControl.Disc)
            throw new Exception("HDLC sizes: failed connection did not disconnect HDLC");
        byte[] end = new byte[1];
        if (await stream.ReadAsync(end, cancellationToken) != 0)
            throw new Exception("HDLC sizes: failed connection did not close its transport");

        async Task WriteFrameAsync(HdlcFrame frame)
        {
            var bytes = new ArrayBufferWriter<byte>();
            codec.Encode(frame, bytes);
            await stream.WriteAsync(bytes.WrittenMemory, cancellationToken);
        }

        async Task<HdlcFrame> ReadFrameAsync()
        {
            var bytes = new ArrayBufferWriter<byte>();
            while (bytes.WrittenCount < 4096)
            {
                int count = await stream.ReadAsync(bytes.GetMemory(1)[..1], cancellationToken);
                if (count == 0) throw new EndOfStreamException("HDLC sizes: incomplete frame");
                bytes.Advance(count);
                var decoded = codec.Decode(new ReadOnlySequence<byte>(bytes.WrittenMemory));
                if (decoded.Status == DecodeStatus.Done) return decoded.Value!;
            }
            throw new Exception("HDLC sizes: frame exceeded test bound");
        }
    }
}
