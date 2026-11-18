using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EMCL.Client.Association;
using EMCL.Core.Codecs;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Framing.Wrapper;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public sealed class ConformanceSettingsTests
{
    private const DlmsConformance AddedFlags = DlmsConformance.GeneralProtection |
        DlmsConformance.GeneralBlockTransfer | DlmsConformance.BlockTransferWithAction;
    private const string TestPassword = "test-password-01";

    [Fact]
    public async Task SaveAndLoadConfiguration_RestoresSelectionsAndDefaultsToAllWhenMissing()
    {
        await using var settings = new MainViewModel { Host = "localhost", TcpPort = "4059" };
        Assert.Equal(LnAssociationOptions.SupportedConformance | AddedFlags,
            settings.ConformanceOptions.Aggregate(DlmsConformance.None, (flags, option) => flags | option.Flag));
        Assert.All(settings.ConformanceOptions, option => Assert.True(option.IsSelected, option.Name));

        foreach (var option in settings.ConformanceOptions.Where(option => (option.Flag & AddedFlags) != 0))
            option.IsSelected = false;
        await using var json = new MemoryStream();
        await settings.SaveConfigurationAsync(json);
        json.Position = 0;
        var profile = (await JsonNode.ParseAsync(json))!;

        await using var restored = new MainViewModel();
        json.Position = 0;
        await restored.LoadConfigurationAsync(json);
        Assert.False(restored.HasStatusError, restored.Status);
        Assert.All(restored.ConformanceOptions, option =>
            Assert.Equal((option.Flag & AddedFlags) == 0, option.IsSelected));

        profile["association"]!.AsObject().Remove("conformance");
        await using var olderProfile = new MemoryStream(Encoding.UTF8.GetBytes(profile.ToJsonString()));
        await restored.LoadConfigurationAsync(olderProfile);
        Assert.False(restored.HasStatusError, restored.Status);
        Assert.All(restored.ConformanceOptions, option => Assert.True(option.IsSelected, option.Name));
    }

    [Theory]
    [InlineData(AddedFlags, 0x00589Du, 0x40189Du, false)]
    [InlineData(DlmsConformance.BlockTransferWithAction | DlmsConformance.GeneralBlockTransfer, 0x40589Du, 0x40189Du, true)]
    [InlineData(DlmsConformance.None, 0x605C9Du, 0x40189Du, true)]
    [InlineData(DlmsConformance.None, 0x605C9Du, 0x4018DDu, false)]
    public async Task Connect_HighWithUspdConformance_AcceptsOnlyProposedFlags(DlmsConformance disabledFlags,
        uint expectedProposal, uint peerConformance, bool expectHls)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var settings = new MainViewModel
        {
            Host = "127.0.0.1",
            TcpPort = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(),
            Framing = "Wrapper",
            Authentication = "High",
            Password = TestPassword
        };
        foreach (var option in settings.ConformanceOptions.Where(option => (option.Flag & disabledFlags) != 0))
            option.IsSelected = false;

        var server = ServeUspdAssociationAsync(listener, expectHls, expectedProposal, peerConformance, deadline.Token);
        await settings.ToggleConnectionCommand.ExecuteAsync(null).WaitAsync(deadline.Token);

        Assert.True(expectHls == settings.IsConnected, settings.Status);
        if (expectHls)
            await settings.ToggleConnectionCommand.ExecuteAsync(null).WaitAsync(deadline.Token);
        else
            Assert.Contains("conformance, or PDU size is incompatible", settings.Status);
        await server;
    }

    private static async Task ServeUspdAssociationAsync(TcpListener listener, bool expectHls,
        uint expectedProposal, uint peerConformance, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = peer.GetStream();
        var codec = new ApduCodec();
        var request = Assert.IsType<AcseApdu>(await ReadApduAsync(stream, codec, cancellationToken));
        Assert.Equal(AcseKind.Aarq, request.Kind);
        var initiate = Assert.IsType<InitiateRequest>(codec.Decode(
            new ReadOnlySequence<byte>(request.UserInformation!.Value.AsMemory())).Value);
        Assert.Equal(expectedProposal, initiate.Conformance);

        byte[] challenge = [.. Enumerable.Range(1, 16).Select(value => (byte)value)];
        var response = AcseApdu.Aare(codec.EncodeBytes(new InitiateResponse(null, DlmsNegotiation.Version6,
            peerConformance, 1024)), contextId: 1, result: 0,
            diagnostic: (byte)AcseServiceUserDiagnostic.AuthenticationRequired,
            mechanism: (byte)ClientAuthentication.High, challenge, systemTitle: []);
        await WriteApduAsync(stream, codec, response, cancellationToken);

        if (!expectHls)
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellationToken));
            return;
        }

        var action = Assert.IsType<LnRequest>(await ReadApduAsync(stream, codec, cancellationToken));
        Assert.Equal(CosemOperationKind.Invoke, action.Operation.Kind);
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(TestPassword);
        var reply = aes.EncryptEcb(request.CallingAuthenticationValue!.Value.AsSpan(), PaddingMode.None);
        await WriteApduAsync(stream, codec, new LnResponse(CosemOperationKind.Invoke, action.InvokeIdAndPriority,
            OperationResult.Ok(DlmsValue.Octets(reply))), cancellationToken);

        var release = Assert.IsType<AcseApdu>(await ReadApduAsync(stream, codec, cancellationToken));
        Assert.Equal(AcseKind.Rlrq, release.Kind);
        await WriteApduAsync(stream, codec, AcseApdu.Release(response: true), cancellationToken);
    }

    private static async Task<DlmsApdu?> ReadApduAsync(NetworkStream stream, ApduCodec codec,
        CancellationToken cancellationToken)
    {
        var header = new byte[WrapperHeader.Length];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(WrapperHeader.PayloadLengthOffset))];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return codec.Decode(new ReadOnlySequence<byte>(payload)).Value;
    }

    private static async Task WriteApduAsync(NetworkStream stream, ApduCodec codec, DlmsApdu apdu,
        CancellationToken cancellationToken)
    {
        var frame = new WrapperFrame(1, 48, codec.EncodeBytes(apdu).ToImmutableArray());
        await stream.WriteAsync(new WrapperCodec().EncodeBytes(frame), cancellationToken);
    }
}
