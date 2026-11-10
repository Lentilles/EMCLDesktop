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

internal static class HdlcSizingChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("HDLC sizes: " + message);
            checks++;
        }
        await using var vm = new MainViewModel { Host = "127.0.0.1", TcpPort = "4059", Framing = "HDLC" };
        async Task<JsonNode> Save()
        {
            await using var stream = new MemoryStream();
            await vm.SaveConfigurationAsync(stream);
            return JsonNode.Parse(stream.ToArray())!;
        }
        Task Load(JsonNode json) => vm.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString())));
        var initial = await Save();
        Check(vm.UseHdlcLinkParameters && vm.HdlcMaxTxBytes == "2030" && vm.HdlcMaxRxBytes == "2030" &&
            initial["channel"]!["framing"]!["hdlcLinkParameters"]!["maximumTransmitInformationBytes"]!.GetValue<int>() == HdlcNegotiation.MaximumInformationBytes &&
            initial["channel"]!["framing"]!["hdlcLinkParameters"]!["maximumReceiveInformationBytes"]!.GetValue<int>() == HdlcNegotiation.MaximumInformationBytes,
            "New connections did not propose the maximum TX/RX sizes by default");
        var defaultOptions = (MeterConnectionOptions)typeof(MainViewModel).GetMethod("ParseOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        Check(Convert.ToHexString(HdlcNegotiation.CreateSnrmInformation(
                ((ApduFraming.Hdlc)defaultOptions.CreateFraming()).LinkParameters).AsSpan()) ==
            "818014050207EE060207EE070400000001080400000001",
            "Default connection framing did not send the maximum TX/RX proposal in SNRM");
        Check(vm.AreHdlcSizesLinked, "Equal default sizes were not linked");
        vm.HdlcMaxTxBytes = "571";
        Check(vm.HdlcMaxRxBytes == "571", "Linked RX did not follow TX");
        vm.HdlcMaxRxBytes = "1024";
        Check(vm.HdlcMaxTxBytes == "1024", "Linked TX did not follow RX");
        vm.AreHdlcSizesLinked = false;
        vm.HdlcMaxTxBytes = "256";
        Check(vm.HdlcMaxRxBytes == "1024", "Unlinked RX changed with TX");
        vm.HdlcMaxRxBytes = "512";
        Check(vm.HdlcMaxTxBytes == "256", "Unlinked TX changed with RX");
        vm.AreHdlcSizesLinked = true;
        Check(vm.HdlcMaxRxBytes == "256" && vm.HdlcSizesLinkActionText.Contains("Развязать"),
            "Relinking did not use TX or update its action text");
        vm.HdlcMaxRxBytes = "";
        Check(vm.HdlcMaxTxBytes == "", "Linked edits could not clear the fields before replacing a value");
        await Load(initial);
        Check(vm.AreHdlcSizesLinked && vm.HdlcMaxTxBytes == "2030" && vm.HdlcMaxRxBytes == "2030",
            "Equal imported values did not restore their editing link");
        vm.AreHdlcSizesLinked = false;
        vm.UseHdlcLinkParameters = true; vm.HdlcMaxTxBytes = "1024"; vm.HdlcMaxRxBytes = "2030";
        var saved = await Save();
        var sizes = saved["channel"]!["framing"]!["hdlcLinkParameters"]!;
        Check(sizes["maximumTransmitInformationBytes"]!.GetValue<int>() == 1024 &&
            sizes["maximumReceiveInformationBytes"]!.GetValue<int>() == 2030 &&
            sizes["transmitWindowFrames"]!.GetValue<int>() == 1 && sizes["receiveWindowFrames"]!.GetValue<int>() == 1,
            "Asymmetric sizes or safe one-frame windows were not saved");
        vm.UseHdlcLinkParameters = false; vm.HdlcMaxTxBytes = "128"; vm.HdlcMaxRxBytes = "128";
        await Load(saved);
        Check(!vm.HasStatusError && vm.UseHdlcLinkParameters && !vm.AreHdlcSizesLinked && vm.HdlcMaxTxBytes == "1024" && vm.HdlcMaxRxBytes == "2030",
            "Explicit sizes did not survive JSON round trip");
        var options = (MeterConnectionOptions)typeof(MainViewModel).GetMethod("ParseOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        var trace = new DiagnosticTrace();
        var framing = (ApduFraming.Hdlc)options.CreateFraming(trace);
        Check(framing.LinkParameters == new HdlcLinkParameters(1024, 2030), "Connection framing did not receive configured limits");
        Check(Convert.ToHexString(HdlcNegotiation.CreateSnrmInformation(framing.LinkParameters).AsSpan()) ==
            "81801405020400060207EE070400000001080400000001", "SNRM proposal bytes did not match the configured sizes");
        var result = HdlcNegotiation.Negotiate(framing.LinkParameters,
            [.. Convert.FromHexString("8180140502020006020100070400000001080400000001")]);
        framing.NegotiationObserver!(result);
        Check(result.Effective.MaximumTransmitInformationBytes == 256 && result.Effective.MaximumReceiveInformationBytes == 512,
            "Client and peer TX/RX directions were mixed up or capped at 128");
        Check(trace.Snapshot().Contains("SNRM клиента: MAX TX=1024, MAX RX=2030") &&
            trace.Snapshot().Contains("UA счётчика: MAX TX=512, MAX RX=256") &&
            trace.Snapshot().Contains("итог клиента: MAX TX=256, MAX RX=512"),
            "Negotiation trace did not distinguish proposal, peer and effective limits");
        foreach (string invalid in new[] { "", "0", "2", "2031", "65535", "-1", "1.5", "abc" })
        {
            foreach (bool tx in new[] { true, false })
            {
                vm.HdlcMaxTxBytes = tx ? invalid : "128"; vm.HdlcMaxRxBytes = tx ? "128" : invalid;
                try { await Save(); throw new Exception("Invalid HDLC size accepted: " + invalid); }
                catch (ArgumentException) { checks++; }
            }
        }
        foreach (int boundary in new[] { 3, 128, HdlcNegotiation.MaximumInformationBytes })
        {
            vm.HdlcMaxTxBytes = boundary.ToString(); vm.HdlcMaxRxBytes = boundary.ToString();
            Check((await Save())["channel"]!["framing"]!["hdlcLinkParameters"]!["maximumTransmitInformationBytes"]!.GetValue<int>() == boundary,
                "A supported boundary size was rejected");
        }
        vm.UseHdlcLinkParameters = false; vm.HdlcMaxTxBytes = "invalid"; vm.HdlcMaxRxBytes = "invalid";
        Check((await Save())["channel"]!["framing"]!["hdlcLinkParameters"] is null, "Disabled values affected saving");
        vm.Framing = "Wrapper"; vm.UseHdlcLinkParameters = true;
        Check((await Save())["channel"]!["framing"]!["hdlcLinkParameters"] is null, "Wrapper saved or validated inactive HDLC values");
        var oldConfiguration = initial.DeepClone();
        oldConfiguration["channel"]!["framing"]!.AsObject().Remove("hdlcLinkParameters");
        await Load(oldConfiguration);
        Check(!vm.HasStatusError && !vm.UseHdlcLinkParameters && vm.HdlcMaxTxBytes == "2030" && vm.HdlcMaxRxBytes == "2030",
            "Old configurations inherited a previously enabled proposal");
        Check((await Save())["channel"]!["framing"]!["hdlcLinkParameters"] is null,
            "Loading a configuration without proposals unexpectedly enabled negotiation");
        // Loading asymmetric values must also work while the editing link is active.
        await Load(saved);
        Check(!vm.AreHdlcSizesLinked && vm.HdlcMaxTxBytes == "1024" && vm.HdlcMaxRxBytes == "2030",
            "An active editing link overwrote asymmetric imported values");
        await Load(oldConfiguration);
        foreach (var (field, value) in new[] { ("maximumReceiveInformationBytes", 2031), ("transmitWindowFrames", 2), ("receiveWindowFrames", 8) })
        {
            var invalid = saved.DeepClone(); invalid["channel"]!["framing"]!["hdlcLinkParameters"]![field] = value;
            await Load(invalid);
            Check(vm.HasStatusError && !vm.UseHdlcLinkParameters && vm.AreHdlcSizesLinked && vm.HdlcMaxTxBytes == "2030",
                "Invalid imported sizes/windows partially changed the configuration");
        }
        var legacy = new MeterConnectionOptions("localhost", 4059, "HDLC", 16, 1, null, false, 1,
            "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 10, 30, 20, 65535,
            DlmsConformance.Get, false, new ProtocolLimits());
        Check(((ApduFraming.Hdlc)legacy.CreateFraming()).LinkParameters is null &&
            HdlcNegotiation.CreateSnrmInformation(null).IsEmpty, "Legacy connections no longer use an empty SNRM");
        Check((legacy with { Framing = "Wrapper", HdlcParameters = new HdlcLinkParameters(0, 0) }).CreateFraming() is ApduFraming.Wrapper,
            "Wrapper used HDLC-only parameters");

        // Exercise the published EMCL package and the actual connection error handler.
        foreach (var (explicitSizes, peerSize, responseSize) in new[] { (false, 128, 257), (true, 128, 257), (false, 571, 571) })
        {
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
            await connectionVm.ConnectCommand.ExecuteAsync(null).WaitAsync(deadline.Token);
            await server;
            Check(connectionVm.HasStatusError && connectionVm.Status.Contains("больше согласованного MAX RX") &&
                connectionVm.Status.Contains($"{responseSize} bytes") && connectionVm.Status.Contains("128 bytes") &&
                connectionVm.Status.Contains("включено согласование MAX TX / MAX RX"),
                "Oversized response lost its actionable error or original lengths");
            Check(!connectionVm.IsConnected && !connectionVm.IsConnecting && !connectionVm.IsBusy,
                "Oversized response left a connected or busy session");
            string history = connectionVm.GetTraceSnapshot();
            Check(history.Contains("Счётчик превысил согласованный MAX RX") &&
                (!explicitSizes || history.Contains("Задано клиентом: MAX TX=1024, MAX RX=512")),
                "Oversized response lost its original diagnostic or configured limits in the trace");
            Check(!listener.Pending(), "Connection retried without an API to override the incorrect UA limits");
        }

        // The supplied trace advertises 571 in UA: an explicit proposal is sufficient;
        // no override of the peer's negotiated sizes is needed for this case.
        foreach (int proposedSize in new[] { 571, HdlcNegotiation.MaximumInformationBytes })
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            var traceSizes = new HdlcLinkParameters(proposedSize, proposedSize);
            var configured = legacy with { Host = "127.0.0.1", TcpPort = ((IPEndPoint)listener.LocalEndpoint).Port,
                HdlcParameters = traceSizes };
            var server = ServeInformationFrameAsync(listener, traceSizes, 571, 571, deadline.Token);
            await using (var channel = await ApduChannels.ConnectAsync(
                new(configured.CreateTransportFactory(), configured.CreateFraming(), configured.Limits), deadline.Token))
            {
                await channel.SendAsync(new byte[] { 1 }, deadline.Token);
                var response = await channel.ReceiveAsync(deadline.Token);
                Check(response is { Length: 568 } && response.Value.Span.IndexOfAnyExcept((byte)0) < 0,
                    $"Proposal {proposedSize}/{proposedSize} failed to receive a valid 571-byte information field");
            }
            await server;
        }
        return checks;
    }

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
