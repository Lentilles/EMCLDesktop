using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
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
        var entries = new[]
        {
            (Id: (ushort)9, Version: (byte)0, Name: "Script Table", Attribute: "scripts", Type: "Array"),
            (Id: (ushort)11, Version: (byte)0, Name: "Special Days Table", Attribute: "entries", Type: "Array"),
            (Id: (ushort)18, Version: (byte)0, Name: "Image Transfer", Attribute: "image_block_size", Type: "UInt32"),
            (Id: (ushort)19, Version: (byte)1, Name: "IEC Local Port Setup", Attribute: "default_mode", Type: "Enum"),
            (Id: (ushort)70, Version: (byte)0, Name: "Disconnect Control", Attribute: "output_state", Type: "Boolean"),
            (Id: (ushort)40, Version: (byte)3, Name: "Push Setup", Attribute: "push_object_list", Type: "Array")
        };
        string Obis(int index) => $"0.0.10.0.{index}.255";
        var list = entries.Select((entry, index) => Entry(entry.Id, entry.Version, Obis(index), 2)).ToList();
        list.Add(Entry(20, 1, Obis(entries.Length), 2));
        list.Add(Entry(4, 0, "1.0.1.6.0.255", 5));
        var meterChannel = new ObjectListChannel(DlmsValue.Array(list.ToArray()));
        var liveSession = new DlmsClientSession(meterChannel, new ClientSessionOptions());
        await liveSession.AssociateAsync();
        await using var liveMeter = (MeterConnection)Activator.CreateInstance(typeof(MeterConnection),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [liveSession, ClientAuthentication.None, LogicalName.Parse(options.AssociationObis), TimeSpan.FromHours(1), options], null)!;
        await liveMeter.RefreshCatalogAsync();
        populate.Invoke(vm, [liveMeter]);
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var attribute = Attribute(Obis(index), 2);
            Check(Object(Obis(index)).ClassName == entry.Name && attribute.Name == entry.Attribute &&
                attribute.Type == entry.Type && attribute.CanRead,
                $"The live catalog did not resolve {entry.Id} v{entry.Version} with its reported access");
        }
        Check(Object(Obis(0)).Methods.Single().Name == "execute",
            "The Script Table method definition was not available");
        Check(Object(Obis(entries.Length)).ClassName == "Неизвестный класс",
            "The catalog used an Activity Calendar definition from another version");
        Check(Attribute("1.0.1.6.0.255", 5).Type == "OctetString",
            "A SPODES definition replaced a standard COSEM definition");
        Check(meterChannel.Gets == 1, "Resolving class metadata sent additional GET requests");

        json["catalog"] = JsonNode.Parse("""
            {"objects":[
              {"obis":"0.0.10.0.0.255","classId":9,"classVersion":0,"className":"Неизвестный класс",
               "attributes":[{"index":2,"name":"Неизвестный атрибут","type":"Тип не указан","hasDeclaredType":false,
                              "access":"ReadOnly (код 1)","canRead":true,"value":"Previously read"}],
               "methods":[{"index":1,"name":"Неизвестный метод","parameterType":"Тип параметра не указан","access":"Access"}]},
              {"obis":"0.0.10.0.1.255","classId":9,"classVersion":0,
               "attributes":[{"index":2,"type":"CompactArray (19)","hasDeclaredType":false}],"methods":[]}
            ]}
            """);
        await vm.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString())));
        var savedScript = Object(Obis(0));
        var savedScripts = Attribute(Obis(0), 2);
        Check(savedScript.ClassName == "Script Table" && savedScripts.Name == "scripts" &&
            savedScripts.HasDeclaredType && savedScripts.WriteType == DlmsType.Array &&
            savedScripts.CanRead && savedScripts.Access == "ReadOnly (код 1)" && savedScripts.Value == "Previously read" &&
            savedScript.Methods.Single().Name == "execute" && savedScript.Methods.Single().ArgumentType == DlmsType.UInt16,
            "Loading an old catalog failed to recover missing metadata or changed saved values and permissions");
        var observed = Attribute(Obis(1), 2);
        Check(Object(Obis(1)).ClassName == "Script Table" && observed.Name == "scripts" &&
            !observed.HasDeclaredType && observed.WriteType == DlmsType.CompactArray,
            "Loading an old catalog replaced an observed DLMS type");
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

    private sealed class ObjectListChannel(DlmsValue list) : IApduChannel
    {
        private readonly ApduCodec codec = new();
        private readonly Channel<ReadOnlyMemory<byte>> responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public int Gets { get; private set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken = default)
        {
            var request = codec.Decode(new ReadOnlySequence<byte>(apdu)).Value;
            DlmsApdu reply;
            switch (request)
            {
                case AcseApdu { Kind: AcseKind.Aarq }:
                    reply = AcseApdu.Aare(codec.EncodeBytes(new InitiateResponse(null, DlmsNegotiation.Version6,
                        (uint)(LnAssociationOptions.SupportedConformance & ~DlmsConformance.PriorityManagement), 1024)));
                    break;
                case LnRequest ln when ln.Operation.Kind == CosemOperationKind.Read:
                    Gets++;
                    reply = new LnResponse(ln.Operation.Kind, ln.InvokeIdAndPriority, OperationResult.Ok(list));
                    break;
                case AcseApdu { Kind: AcseKind.Rlrq }:
                    reply = AcseApdu.Release(response: true);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected catalog-test request: {request}");
            }
            return responses.Writer.WriteAsync(codec.EncodeBytes(reply), cancellationToken);
        }

        public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            await responses.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            responses.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
