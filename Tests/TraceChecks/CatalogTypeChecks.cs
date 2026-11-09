using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using EMCL.Client.Association;
using EMCL.Client.Sessions;
using EMCL.Composition;
using EMCL.Core.Codecs;
using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCL.Transport.Channels;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;

internal static class CatalogTypeChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Catalog types: " + message);
            checks++;
        }
        DlmsValue Entry(ushort classId, byte version, string obis, params sbyte[] indices) => DlmsValue.Structure(
            DlmsValue.Number(DlmsType.UInt16, classId), DlmsValue.Number(DlmsType.UInt8, version),
            DlmsValue.Octets(LogicalName.Parse(obis).ToBytes()),
            DlmsValue.Structure(DlmsValue.Array(indices.Select(index => DlmsValue.Structure(
                DlmsValue.Number(DlmsType.Int8, index), DlmsValue.Number(DlmsType.Enum, 1), DlmsValue.Null)).ToArray()),
                DlmsValue.Array()));
        var channel = new NoRequestsChannel();
        var session = new DlmsClientSession(channel, new ClientSessionOptions());
        var options = new MeterConnectionOptions("test", 1, "Wrapper", 16, 1, null, false, 1,
            "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 1, 1, 3600, 1024,
            LnAssociationOptions.SupportedConformance, false, new ProtocolLimits());
        await using var meter = (MeterConnection)Activator.CreateInstance(typeof(MeterConnection),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [session, ClientAuthentication.None, LogicalName.Parse(options.AssociationObis), TimeSpan.FromHours(1), options], null)!;
        await using var vm = new MainViewModel { Host = "127.0.0.1", TcpPort = "4059", Framing = "Wrapper" };
        var populate = typeof(MainViewModel).GetMethod("PopulateObjects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Import(params DlmsValue[] entries)
        {
            var imported = new EmclBuilder().CreateImporter().Import(DlmsValue.Array(entries), "test", "test", 1);
            typeof(MeterConnection).GetProperty(nameof(MeterConnection.Association))!.SetValue(meter, imported);
            populate.Invoke(vm, [meter]);
        }
        MeterObjectViewModel Object(string obis) => vm.Objects.Single(obj => obj.Obis == obis);
        AttributeViewModel Attribute(string obis, sbyte index) => Object(obis).Attributes.Single(attr => attr.Index == index);

        await using var settings = new MemoryStream();
        await vm.SaveConfigurationAsync(settings);
        var json = JsonNode.Parse(settings.ToArray())!;
        json["catalog"] = JsonNode.Parse("""
            {"objects":[{"obis":"0.0.0.1.2.255","classId":1,"classVersion":0,"className":"Data",
              "attributes":[
                {"index":1,"name":"logical_name","type":"OctetString","hasDeclaredType":true,
                 "access":"ReadOnly (код 1)","canRead":true,"value":"0.0.0.1.2.255"},
                {"index":2,"name":"value","type":"Date (26)","hasDeclaredType":false,
                 "access":"ReadOnly (код 1)","canRead":true,"value":"07EA0A1E05"}],"methods":[]}]}
            """);
        await vm.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString())));
        var savedDate = Attribute("0.0.0.1.2.255", 2);
        Check(savedDate.Type == "Date (26)" && savedDate.WriteType == DlmsType.Date && !savedDate.HasDeclaredType,
            "The user's saved Date type was not restored as a concrete type");
        Import(Entry(1, 0, "0.0.0.1.2.255", 2), Entry(8, 0, "0.0.1.0.0.255", 3),
            Entry(7, 1, "1.0.99.1.0.255", 2, 4));
        var date = Attribute("0.0.0.1.2.255", 2);
        Check(date.Type == "Date (26)" && date.WriteType == DlmsType.Date && !date.HasDeclaredType,
            "Reading the tree discarded the known Date type from the saved catalog");
        Check(date.Value == "Не считано" && date.LastReadValue is null && date.Access.Contains("ReadOnly") && date.CanRead,
            "Keeping a known type restored stale values or replaced the current access rights");
        Check(Attribute("0.0.1.0.0.255", 3).Type == "Int16" &&
            Attribute("0.0.1.0.0.255", 3).WriteType == DlmsType.Int16 &&
            Attribute("1.0.99.1.0.255", 4).Type == "UInt32",
            "Fixed types were not available immediately after importing the tree");
        var applyRead = typeof(MainViewModel).GetMethod("ApplyReadResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        applyRead.Invoke(vm, [Object("0.0.0.1.2.255"), date, DlmsValue.Number(DlmsType.UInt16, 7)]);
        Import(Entry(1, 0, "0.0.0.1.2.255", 2));
        Check(Attribute("0.0.0.1.2.255", 2).Type == "UInt16 (18)",
            "A fresh response type did not replace the cached type on the next tree read");

        foreach (DlmsType type in Enum.GetValues<DlmsType>())
        {
            var known = Attribute("0.0.0.1.2.255", 2);
            known.Type = $"{type} ({(byte)type})";
            Import(Entry(1, 0, "0.0.0.1.2.255", 2));
            var refreshed = Attribute("0.0.0.1.2.255", 2);
            Check(refreshed.Type == $"{type} ({(byte)type})" && refreshed.WriteType == type && !refreshed.HasDeclaredType,
                $"A tree refresh discarded a learned {type} type");
        }
        foreach (string invalid in new[] { "Тип не указан", "Array / CompactArray", "Date (9)", "26", "Undefined", "UInt8 (999)" })
        {
            Attribute("0.0.0.1.2.255", 2).Type = invalid;
            Import(Entry(1, 0, "0.0.0.1.2.255", 2));
            Check(Attribute("0.0.0.1.2.255", 2).Type == "Тип не указан" &&
                Attribute("0.0.0.1.2.255", 2).WriteType is null,
                "An unrecognized description or inconsistent tag became a cached concrete type");
        }
        Attribute("0.0.0.1.2.255", 2).Type = "Date (26)";
        Import(Entry(3, 0, "0.0.0.1.2.255", 2));
        Check(Attribute("0.0.0.1.2.255", 2).WriteType is null, "A type was reused after changing class ID");
        Attribute("0.0.0.1.2.255", 2).Type = "Date (26)";
        Import(Entry(3, 1, "0.0.0.1.2.255", 2));
        Check(Attribute("0.0.0.1.2.255", 2).WriteType is null, "A type was reused after changing class version");
        Attribute("0.0.0.1.2.255", 2).Type = "Date (26)";
        Import(Entry(3, 1, "0.0.0.1.2.255", 3));
        Check(Attribute("0.0.0.1.2.255", 3).WriteType is null, "A type was reused for another attribute index");
        Attribute("0.0.0.1.2.255", 3).Type = "Date (26)";
        Import(Entry(3, 1, "0.0.0.1.3.255", 3));
        Check(Attribute("0.0.0.1.3.255", 3).WriteType is null, "A type was reused for another OBIS");
        Check(channel.Requests == 0, "Keeping known types caused implicit attribute reads");
        return checks;
    }

    private sealed class NoRequestsChannel : IApduChannel
    {
        public int Requests { get; private set; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken = default)
        {
            Requests++;
            throw new Exception("Populating tree types must not send requests");
        }
        public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            new((ReadOnlyMemory<byte>?)null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
