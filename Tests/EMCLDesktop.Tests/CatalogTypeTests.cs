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
using Xunit;

namespace EMCLDesktop.Tests;

public class CatalogTypeTests
{
    private const string DateObis = "0.0.0.1.2.255";

        private const string SavedDateCatalog = """
            {"objects":[{"obis":"0.0.0.1.2.255","classId":1,"classVersion":0,"className":"Data",
              "attributes":[
                {"index":1,"name":"logical_name","type":"OctetString","hasDeclaredType":true,
                 "access":"ReadOnly (код 1)","canRead":true,"value":"0.0.0.1.2.255"},
                {"index":2,"name":"value","type":"Date (26)","hasDeclaredType":false,
                 "access":"ReadOnly (код 1)","canRead":true,"value":"07EA0A1E05"}],"methods":[]}]}
            """;
        private const string LegacyCatalog = """
            {"objects":[
              {"obis":"0.0.10.0.0.255","classId":9,"classVersion":0,"className":"Неизвестный класс",
               "attributes":[{"index":2,"name":"Неизвестный атрибут","type":"Тип не указан","hasDeclaredType":false,
                              "access":"ReadOnly (код 1)","canRead":true,"value":"Previously read"}],
               "methods":[{"index":1,"name":"Неизвестный метод","parameterType":"Тип параметра не указан","access":"Access"}]},
              {"obis":"0.0.10.0.1.255","classId":9,"classVersion":0,
               "attributes":[{"index":2,"type":"CompactArray (19)","hasDeclaredType":false}],"methods":[]}
            ]}
            """;

    [Fact]
    public async Task LoadConfigurationAsync_CatalogWithObservedDate_RestoresConcreteType()
    {
        // Arrange
        await using var vm = CreateViewModel();

        // Act
        await LoadCatalogAsync(vm, SavedDateCatalog);

        // Assert
        var savedDate = Attribute(vm, "0.0.0.1.2.255", 2);
        Assert.Equal("Date (26)", savedDate.Type);
        Assert.Equal(DlmsType.Date, savedDate.WriteType);
        Assert.False(savedDate.HasDeclaredType, "The user's saved Date type was not restored as a concrete type");
    }

    [Fact]
    public async Task PopulateObjects_CatalogWithKnownDate_PreservesTypeAndUsesCurrentAccess()
    {
        // Arrange
        await using var vm = CreateViewModel();
        var channel = new NoRequestsChannel();
        await using var meter = CreateMeter(new DlmsClientSession(channel, new ClientSessionOptions()));
        await LoadCatalogAsync(vm, SavedDateCatalog);

        // Act
        Import(vm, meter, Entry(1, 0, "0.0.0.1.2.255", 2), Entry(8, 0, "0.0.1.0.0.255", 3),
            Entry(7, 1, "1.0.99.1.0.255", 2, 4));

        // Assert
        var date = Attribute(vm, "0.0.0.1.2.255", 2);
        Assert.Equal("Date (26)", date.Type);
        Assert.Equal(DlmsType.Date, date.WriteType);
        Assert.False(date.HasDeclaredType, "Reading the tree discarded the known Date type from the saved catalog");
        Assert.Equal("Не считано", date.Value);
        Assert.Null(date.LastReadValue);
        Assert.Contains("ReadOnly", date.Access);
        Assert.True(date.CanRead, "Keeping a known type restored stale values or replaced the current access rights");
        Assert.Equal("Int16", Attribute(vm, "0.0.1.0.0.255", 3).Type);
        Assert.Equal(DlmsType.Int16, Attribute(vm, "0.0.1.0.0.255", 3).WriteType);
        Assert.Equal("UInt32", Attribute(vm, "1.0.99.1.0.255", 4).Type);
        Assert.Equal(0, channel.Requests);
    }

    [Fact]
    public async Task PopulateObjects_NewlyObservedType_ReplacesCachedTypeOnRefresh()
    {
        // Arrange
        await using var vm = CreateViewModel();
        var channel = new NoRequestsChannel();
        await using var meter = CreateMeter(new DlmsClientSession(channel, new ClientSessionOptions()));
        await LoadCatalogAsync(vm, SavedDateCatalog);
        Import(vm, meter, Entry(1, 0, DateObis, 2));
        var date = Attribute(vm, DateObis, 2);
        var applyRead = typeof(MainViewModel).GetMethod("ApplyReadResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        applyRead.Invoke(vm, [Object(vm, "0.0.0.1.2.255"), date, DlmsValue.Number(DlmsType.UInt16, 7)]);

        // Act
        Import(vm, meter, Entry(1, 0, "0.0.0.1.2.255", 2));

        // Assert
        Assert.Equal("UInt16 (18)", Attribute(vm, "0.0.0.1.2.255", 2).Type);
    }

    [Theory]
    [MemberData(nameof(DlmsTypes))]
    public async Task PopulateObjects_KnownDlmsType_PreservesTypeOnRefresh(DlmsType type)
    {
        // Arrange
        await using var vm = CreateViewModel();
        var channel = new NoRequestsChannel();
        await using var meter = CreateMeter(new DlmsClientSession(channel, new ClientSessionOptions()));
        Import(vm, meter, Entry(1, 0, DateObis, 2));
        Attribute(vm, DateObis, 2).Type = $"{type} ({(byte)type})";

        // Act
        Import(vm, meter, Entry(1, 0, "0.0.0.1.2.255", 2));

        // Assert
        var refreshed = Attribute(vm, "0.0.0.1.2.255", 2);
        Assert.True(refreshed.Type == $"{type} ({(byte)type})" && refreshed.WriteType == type && !refreshed.HasDeclaredType,
            $"A tree refresh discarded a learned {type} type");
    }

    [Theory]
    [InlineData("Тип не указан")]
    [InlineData("Array / CompactArray")]
    [InlineData("Date (9)")]
    [InlineData("26")]
    [InlineData("Undefined")]
    [InlineData("UInt8 (999)")]
    public async Task PopulateObjects_UnknownTypeDescription_DoesNotInferConcreteType(string invalid)
    {
        // Arrange
        await using var vm = CreateViewModel();
        var channel = new NoRequestsChannel();
        await using var meter = CreateMeter(new DlmsClientSession(channel, new ClientSessionOptions()));
        Import(vm, meter, Entry(1, 0, DateObis, 2));
        Attribute(vm, DateObis, 2).Type = invalid;

        // Act
        Import(vm, meter, Entry(1, 0, "0.0.0.1.2.255", 2));

        // Assert
        Assert.Equal("Тип не указан", Attribute(vm, "0.0.0.1.2.255", 2).Type);
        Assert.Null(Attribute(vm, "0.0.0.1.2.255", 2).WriteType);
    }

    [Theory]
    [InlineData(1, 0, 2, 3, 0, DateObis, 2)]
    [InlineData(3, 0, 2, 3, 1, DateObis, 2)]
    [InlineData(3, 1, 2, 3, 1, DateObis, 3)]
    [InlineData(3, 1, 3, 3, 1, "0.0.0.1.3.255", 3)]
    public async Task PopulateObjects_ChangedAttributeIdentity_DoesNotReuseCachedType(ushort oldClassId, byte oldVersion, sbyte oldIndex, ushort classId, byte version, string obis, sbyte index)
    {
        // Arrange
        await using var vm = CreateViewModel();
        var channel = new NoRequestsChannel();
        await using var meter = CreateMeter(new DlmsClientSession(channel, new ClientSessionOptions()));
        Import(vm, meter, Entry(oldClassId, oldVersion, DateObis, oldIndex));
        Attribute(vm, DateObis, oldIndex).Type = "Date (26)";

        // Act
        Import(vm, meter, Entry(classId, version, obis, index));

        // Assert
        Assert.Null(Attribute(vm, obis, index).WriteType);
        Assert.Equal(0, channel.Requests);
    }

    [Fact]
    public async Task PopulateObjects_LiveCatalog_ResolvesDefinitionsWithoutAdditionalGets()
    {
        // Arrange
        await using var vm = CreateViewModel();
        var entries = new[]
        {
            (Id: (ushort)9, Version: (byte)0, Name: "Script Table", Attribute: "scripts", Type: "Array"),
            (Id: (ushort)11, Version: (byte)0, Name: "Special Days Table", Attribute: "entries", Type: "Array"),
            (Id: (ushort)18, Version: (byte)0, Name: "Image Transfer", Attribute: "image_block_size", Type: "UInt32"),
            (Id: (ushort)19, Version: (byte)1, Name: "IEC Local Port Setup", Attribute: "default_mode", Type: "Enum"),
            (Id: (ushort)70, Version: (byte)0, Name: "Disconnect Control", Attribute: "output_state", Type: "Boolean"),
            (Id: (ushort)40, Version: (byte)3, Name: "Push Setup", Attribute: "push_object_list", Type: "Array")
        };
        var list = entries.Select((entry, index) => Entry(entry.Id, entry.Version, Obis(index), 2)).ToList();
        list.Add(Entry(20, 1, Obis(entries.Length), 2));
        list.Add(Entry(4, 0, "1.0.1.6.0.255", 5));
        var meterChannel = new ObjectListChannel(DlmsValue.Array(list.ToArray()));
        var liveSession = new DlmsClientSession(meterChannel, new ClientSessionOptions());
        await liveSession.AssociateAsync();
        await using var liveMeter = CreateMeter(liveSession);
        await liveMeter.RefreshCatalogAsync();

        // Act
        PopulateObjects(vm, liveMeter);

        // Assert
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var attribute = Attribute(vm, Obis(index), 2);
            Assert.True(Object(vm, Obis(index)).ClassName == entry.Name && attribute.Name == entry.Attribute &&
                attribute.Type == entry.Type && attribute.CanRead,
                $"The live catalog did not resolve {entry.Id} v{entry.Version} with its reported access");
        }
        Assert.Equal("execute", Object(vm, Obis(0)).Methods.Single().Name);
        Assert.Equal("Неизвестный класс", Object(vm, Obis(entries.Length)).ClassName);
        Assert.Equal("OctetString", Attribute(vm, "1.0.1.6.0.255", 5).Type);
        Assert.Equal(1, meterChannel.Gets);
    }

    [Fact]
    public async Task LoadConfigurationAsync_LegacyCatalog_RecoversMetadataAndPreservesObservedTypes()
    {
        // Arrange
        await using var vm = CreateViewModel();

        // Act
        await LoadCatalogAsync(vm, LegacyCatalog);

        // Assert
        var savedScript = Object(vm, Obis(0));
        var savedScripts = Attribute(vm, Obis(0), 2);
        Assert.Equal("Script Table", savedScript.ClassName);
        Assert.Equal("scripts", savedScripts.Name);
        Assert.True(savedScripts.HasDeclaredType, "Loading an old catalog failed to recover missing metadata or changed saved values and permissions");
        Assert.Equal(DlmsType.Array, savedScripts.WriteType);
        Assert.True(savedScripts.CanRead, "Loading an old catalog failed to recover missing metadata or changed saved values and permissions");
        Assert.Equal("ReadOnly (код 1)", savedScripts.Access);
        Assert.Equal("Previously read", savedScripts.Value);
        Assert.Equal("execute", savedScript.Methods.Single().Name);
        Assert.Equal(DlmsType.UInt16, savedScript.Methods.Single().ArgumentType);
        var observed = Attribute(vm, Obis(1), 2);
        Assert.Equal("Script Table", Object(vm, Obis(1)).ClassName);
        Assert.Equal("scripts", observed.Name);
        Assert.False(observed.HasDeclaredType, "Loading an old catalog replaced an observed DLMS type");
        Assert.Equal(DlmsType.CompactArray, observed.WriteType);
    }

    public static IEnumerable<object[]> DlmsTypes =>
        Enum.GetValues<DlmsType>().Select(type => new object[] { type });

    private static MainViewModel CreateViewModel() =>
        new() { Host = "127.0.0.1", TcpPort = "4059", Framing = "Wrapper" };

    private static MeterConnection CreateMeter(DlmsClientSession session)
    {
        var options = new MeterConnectionOptions("test", 1, "Wrapper", 16, 1, null, false, 1,
            "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 1, 1, 3600, 1024,
            LnAssociationOptions.SupportedConformance, false, new ProtocolLimits());
        return (MeterConnection)Activator.CreateInstance(typeof(MeterConnection),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [session, ClientAuthentication.None, LogicalName.Parse(options.AssociationObis), TimeSpan.FromHours(1), options], null)!;
    }

    private static async Task LoadCatalogAsync(MainViewModel vm, string catalog)
    {
        await using var settings = new MemoryStream();
        await vm.SaveConfigurationAsync(settings);
        var json = JsonNode.Parse(settings.ToArray())!;
        json["catalog"] = JsonNode.Parse(catalog);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        await vm.LoadConfigurationAsync(stream);
    }

    private static DlmsValue Entry(ushort classId, byte version, string obis, params sbyte[] indices) => DlmsValue.Structure(
        DlmsValue.Number(DlmsType.UInt16, classId), DlmsValue.Number(DlmsType.UInt8, version),
        DlmsValue.Octets(LogicalName.Parse(obis).ToBytes()),
        DlmsValue.Structure(DlmsValue.Array(indices.Select(index => DlmsValue.Structure(
            DlmsValue.Number(DlmsType.Int8, index), DlmsValue.Number(DlmsType.Enum, 1), DlmsValue.Null)).ToArray()),
            DlmsValue.Array()));

    private static void Import(MainViewModel vm, MeterConnection meter, params DlmsValue[] entries)
    {
        var imported = new EmclBuilder().CreateImporter().Import(DlmsValue.Array(entries), "test", "test", 1);
        typeof(MeterConnection).GetProperty(nameof(MeterConnection.Association))!.SetValue(meter, imported);
        PopulateObjects(vm, meter);
    }

    private static void PopulateObjects(MainViewModel vm, MeterConnection meter) =>
        typeof(MainViewModel).GetMethod("PopulateObjects", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [meter]);

    private static MeterObjectViewModel Object(MainViewModel vm, string obis) =>
        vm.Objects.Single(obj => obj.Obis == obis);

    private static AttributeViewModel Attribute(MainViewModel vm, string obis, sbyte index) =>
        Object(vm, obis).Attributes.Single(attr => attr.Index == index);

    private static string Obis(int index) => $"0.0.10.0.{index}.255";

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
