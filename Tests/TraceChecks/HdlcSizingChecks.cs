using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using EMCL.Client.Association;
using EMCL.Core.Codecs;
using EMCL.Framing;
using EMCL.Protocol.Apdu;
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
        Check(!vm.UseHdlcLinkParameters && vm.HdlcMaxTxBytes == "128" && vm.HdlcMaxRxBytes == "128" &&
            initial["channel"]!["framing"]!["hdlcLinkParameters"] is null,
            "Legacy defaults unexpectedly enabled explicit SNRM parameters");
        vm.UseHdlcLinkParameters = true; vm.HdlcMaxTxBytes = "1024"; vm.HdlcMaxRxBytes = "2030";
        var saved = await Save();
        var sizes = saved["channel"]!["framing"]!["hdlcLinkParameters"]!;
        Check(sizes["maximumTransmitInformationBytes"]!.GetValue<int>() == 1024 &&
            sizes["maximumReceiveInformationBytes"]!.GetValue<int>() == 2030 &&
            sizes["transmitWindowFrames"]!.GetValue<int>() == 1 && sizes["receiveWindowFrames"]!.GetValue<int>() == 1,
            "Asymmetric sizes or safe one-frame windows were not saved");
        vm.UseHdlcLinkParameters = false; vm.HdlcMaxTxBytes = "128"; vm.HdlcMaxRxBytes = "128";
        await Load(saved);
        Check(!vm.HasStatusError && vm.UseHdlcLinkParameters && vm.HdlcMaxTxBytes == "1024" && vm.HdlcMaxRxBytes == "2030",
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
        await Load(initial);
        Check(!vm.HasStatusError && !vm.UseHdlcLinkParameters && vm.HdlcMaxTxBytes == "128" && vm.HdlcMaxRxBytes == "128",
            "Old configurations inherited a previously enabled proposal");
        foreach (var (field, value) in new[] { ("maximumReceiveInformationBytes", 2031), ("transmitWindowFrames", 2), ("receiveWindowFrames", 2) })
        {
            var invalid = saved.DeepClone(); invalid["channel"]!["framing"]!["hdlcLinkParameters"]![field] = value;
            await Load(invalid);
            Check(vm.HasStatusError && !vm.UseHdlcLinkParameters && vm.HdlcMaxTxBytes == "128",
                "Invalid imported sizes/windows partially changed the configuration");
        }
        var legacy = new MeterConnectionOptions("localhost", 4059, "HDLC", 16, 1, null, false, 1,
            "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 10, 30, 20, 65535,
            DlmsConformance.Get, false, new ProtocolLimits());
        Check(((ApduFraming.Hdlc)legacy.CreateFraming()).LinkParameters is null &&
            HdlcNegotiation.CreateSnrmInformation(null).IsEmpty, "Legacy connections no longer use an empty SNRM");
        Check((legacy with { Framing = "Wrapper", HdlcParameters = new HdlcLinkParameters(0, 0) }).CreateFraming() is ApduFraming.Wrapper,
            "Wrapper used HDLC-only parameters");
        return checks;
    }
}
