using System.IO.Ports;
using System.Text;
using System.Text.Json.Nodes;
using EMCLDesktop.ViewModels;
using EMCLDesktop.Services;
using EMCL.Framing;
using EMCL.Transport.Streams;
using Xunit;

namespace EMCLDesktop.Tests;

public class ConnectionSettingsTests
{
    [Fact]
    public async Task Constructor_NewConnection_DefaultsToTcpWithoutEndpoint()
    {
        // Arrange
        const string defaultTransport = "TCP";

        // Act
        await using var vm = new MainViewModel();

        // Assert
        Assert.Equal("", vm.Host);
        Assert.Equal("", vm.TcpPort);
        Assert.False(vm.HasStatusError, "Initial status must not show error-copy action");
        Assert.True(vm.IsTcp, "Default transport changed");
        Assert.False(vm.IsSerial, "Default transport changed");
        Assert.Equal(defaultTransport, vm.Transport);
    }

    [Fact]
    public async Task ReportError_DeviceRefusal_SetsErrorStatus()
    {
        // Arrange
        await using var vm = new MainViewModel();

        // Act
        vm.ReportError("Счётчик отклонил запрос");

        // Assert
        Assert.True(vm.HasStatusError, "Device refusal without an error prefix must expose error-copy action");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_InformationalMessage_ClearsErrorSeverity(bool sameMessage)
    {
        // Arrange
        await using var vm = new MainViewModel();
        vm.ReportError("Повторяемое сообщение");

        // Act
        vm.Status = sameMessage ? vm.Status : "Чтение дерева объектов…";

        // Assert
        Assert.False(vm.HasStatusError);
    }

    [Fact]
    public async Task SelectedTabIndex_TraceTab_RefreshesHistoryAndDeactivatesOtherViews()
    {
        // Arrange
        await using var vm = new MainViewModel();
        vm.ReportError("Ошибка подключения для проверки видимости");
        vm.TraceText = "Устаревший снимок";

        // Act
        vm.SelectedTabIndex = 2;

        // Assert
        Assert.False(vm.IsObjectsTab, "Trace tab incorrectly activates meter status or packet inspector");
        Assert.False(vm.Inspector.IsActive, "Trace tab incorrectly activates meter status or packet inspector");
        Assert.True(vm.TraceText == vm.GetTraceSnapshot(), "Trace tab did not refresh history when opened");
    }

    [Fact]
    public async Task SelectedTabIndex_ReturnToObjectsTab_PreservesConnectionError()
    {
        // Arrange
        await using var vm = new MainViewModel();
        vm.ReportError("Ошибка подключения для проверки видимости");
        vm.TraceText = "Устаревший снимок";
        vm.SelectedTabIndex = 2;

        // Act
        vm.SelectedTabIndex = 0;

        // Assert
        Assert.True(vm.IsObjectsTab, "Meter tab lost its connection error");
        Assert.True(vm.HasStatusError, "Meter tab lost its connection error");
        Assert.Equal("Ошибка подключения для проверки видимости", vm.Status);
    }

    [Fact]
    public async Task ConnectionState_Disconnected_ShowsConnectAction()
    {
        // Arrange
        const string expectedAction = "Подключиться";

        // Act
        await using var vm = new MainViewModel();

        // Assert
        Assert.Equal("Не подключено", vm.ConnectionState);
        Assert.Equal(expectedAction, vm.ConnectionActionText);
    }

    [Fact]
    public async Task IsConnecting_ConnectionStarts_UpdatesStateAndNotifies()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        vm.IsConnecting = true;

        // Assert
        Assert.Equal("Подключение…", vm.ConnectionState);
        Assert.True(vm.IsConnectionChanging, "Connecting state or notification missing");
        Assert.Contains(nameof(vm.IsConnectionChanging), changed);
    }

    [Fact]
    public async Task IsConnected_ConnectionEstablished_ShowsDisconnectAction()
    {
        // Arrange
        await using var vm = new MainViewModel();

        // Act
        vm.IsConnected = true;

        // Assert
        Assert.Equal("Подключено", vm.ConnectionState);
        Assert.Equal("Отключиться", vm.ConnectionActionText);
    }

    [Fact]
    public async Task IsBusy_MeterOperation_KeepsConnectedStateAndDisablesConnectionAction()
    {
        // Arrange
        await using var vm = new MainViewModel();
        vm.IsConnected = true;

        // Act
        vm.IsBusy = true;

        // Assert
        Assert.Equal("Подключено", vm.ConnectionState);
        Assert.False(vm.IsConnectionChanging, "A busy meter operation must not look like connection establishment");
        Assert.False(vm.ToggleConnectionCommand.CanExecute(null), "A busy meter operation must not look like connection establishment");
    }

    [Fact]
    public async Task IsDisconnecting_ConnectionClosing_ContinuesToShowDisconnectAction()
    {
        // Arrange
        await using var vm = new MainViewModel();
        vm.IsConnected = true;
        vm.IsDisconnecting = true;

        // Act
        vm.IsConnected = false;

        // Assert
        Assert.Equal("Отключение…", vm.ConnectionState);
        Assert.Equal("Отключиться", vm.ConnectionActionText);
        Assert.True(vm.IsConnectionChanging, "Disconnecting state prematurely offers reconnection");
    }

    [Fact]
    public async Task ToggleConnectionCommand_InvalidPort_ReportsErrorAndResetsTransition()
    {
        // Arrange
        await using var vm = new MainViewModel();
        vm.TcpPort = "invalid"; // Fails validation before any transport can be opened.
        var connectionStates = new List<string>();
        bool connectingButtonDisabled = false;
        void ObserveConnectionState(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(vm.ConnectionState)) return;
            connectionStates.Add(vm.ConnectionState);
            if (vm.IsConnecting) connectingButtonDisabled |= !vm.ToggleConnectionCommand.CanExecute(null);
        }
        vm.PropertyChanged += ObserveConnectionState;

        // Act
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        vm.PropertyChanged -= ObserveConnectionState;

        // Assert
        Assert.Contains("Подключение…", connectionStates);
        Assert.True(connectingButtonDisabled, "Failed connection left a transition flag or an enabled connection action");
        Assert.False(vm.IsConnecting, "Failed connection left a transition flag or an enabled connection action");
        Assert.False(vm.IsBusy, "Failed connection left a transition flag or an enabled connection action");
        Assert.Equal("Не подключено", vm.ConnectionState);
        Assert.True(vm.HasStatusError, "Failed connection did not expose error-copy action");
    }

    [Fact]
    public async Task Transport_SerialSelected_UpdatesVisibilityAndNotifies()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        vm.Transport = "Serial";

        // Assert
        Assert.True(vm.IsSerial, "Transport visibility notifications missing");
        Assert.False(vm.IsTcp, "Transport visibility notifications missing");
        Assert.Contains(nameof(vm.IsSerial), changed);
        Assert.Contains(nameof(vm.IsTcp), changed);
    }

    [Fact]
    public async Task Authentication_HighSelected_FixedInvokeIdAndCompatibilityModesRemainOptIn()
    {
        // Arrange
        await using var vm = new MainViewModel();

        // Act
        vm.Authentication = "High";

        // Assert
        Assert.False(vm.UseFixedInvokeC1, "The fixed 0xC1 mode should be opt-in for High authentication");
        Assert.True(vm.CanUseFixedInvokeC1, "The fixed 0xC1 mode should be opt-in for High authentication");
        Assert.False(vm.UseHighPriority, "High priority and Invoke ID compatibility should be opt-in");
        Assert.False(vm.IgnoreInvokeIdValidation, "High priority and Invoke ID compatibility should be opt-in");
    }

    [Fact]
    public async Task UseHighPriority_EnabledWithHighAuthentication_DisablesFixedInvokeId()
    {
        // Arrange
        await using var vm = new MainViewModel();
        vm.Authentication = "High";

        // Act
        vm.UseHighPriority = true;

        // Assert
        Assert.False(vm.CanUseFixedInvokeC1, "High priority should use automatic Invoke IDs");
    }

    [Fact]
    public async Task SaveConfigurationAsync_ValidSerialSettings_OmitsTcpFieldsAndClearsError()
    {
        // Arrange
        await using var vm = CreateSerialViewModel();
        vm.ReportError("Счётчик отклонил запрос");
        await using var serialJson = new MemoryStream();

        // Act
        await vm.SaveConfigurationAsync(serialJson);

        // Assert
        Assert.False(vm.HasStatusError, "Successful save retained stale error-copy action");
        var savedSerial = JsonNode.Parse(serialJson.ToArray())!;
        var serialSettings = savedSerial["channel"]!["connection"]!;
        Assert.Equal("Serial", serialSettings["kind"]!.GetValue<string>());
        Assert.Null(serialSettings["host"]);
        Assert.Null(serialSettings["tcpPort"]);
        Assert.Equal(19200, serialSettings["baudRate"]!.GetValue<int>());
        var savedHdlc = savedSerial["channel"]!["framing"]!;
        Assert.Equal(1, savedHdlc["informationFrameRetryCount"]!.GetValue<int>());
        Assert.Equal(0.75, savedHdlc["acknowledgementTimeoutSeconds"]!.GetValue<double>());
    }

    [Fact]
    public async Task LoadConfigurationAsync_SavedSerialSettings_RestoresLineAndAssociationOptions()
    {
        // Arrange
        await using var vm = CreateSerialViewModel();
        await using var serialJson = new MemoryStream();
        await vm.SaveConfigurationAsync(serialJson);
        await using var restored = new MainViewModel();
        serialJson.Position = 0;

        // Act
        await restored.LoadConfigurationAsync(serialJson);

        // Assert
        Assert.True(restored.IsSerial && restored.SerialPortName == vm.SerialPortName && restored.SerialBaudRate == "19200" &&
              restored.SerialDataBits == 7 && restored.SerialParity == Parity.Even && restored.SerialStopBits == StopBits.Two &&
              restored.SerialHandshake == Handshake.XOnXOff && restored.SerialDtrEnable && restored.SerialRtsEnable,
            "Serial JSON round trip lost settings: " + restored.Status);
        Assert.Equal(1, restored.HdlcInformationFrameRetryCount);
        Assert.Equal("0.75", restored.HdlcAcknowledgementTimeoutSeconds);
        Assert.False(restored.UseFixedInvokeC1, "Disabled fixed 0xC1 setting did not round trip");
        Assert.Equal("High", restored.Authentication);
        Assert.True(restored.UseHighPriority, "High request priority or Invoke ID compatibility setting did not round trip");
        Assert.True(restored.IgnoreInvokeIdValidation, "High request priority or Invoke ID compatibility setting did not round trip");
    }

    [Fact]
    public async Task LoadConfigurationAsync_FixedInvokeIdAndHighPriority_PrioritizesHighPriority()
    {
        // Arrange
        await using var vm = CreateSerialViewModel();
        var savedSerial = await SaveAsync(vm);
        await using var restored = new MainViewModel();
        var savedAssociation = savedSerial["association"]!.AsObject();
        savedAssociation["useFixedInvokeC1"] = true;

        // Act
        await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(savedSerial.ToJsonString())));

        // Assert
        Assert.True(restored.UseFixedInvokeC1, "High priority should take precedence over a saved fixed 0xC1 setting");
        Assert.True(restored.UseHighPriority, "High priority should take precedence over a saved fixed 0xC1 setting");
        Assert.False(restored.CanUseFixedInvokeC1, "High priority should take precedence over a saved fixed 0xC1 setting");
    }

    [Theory]
    [InlineData("High", false, true)]
    [InlineData("High", true, false)]
    [InlineData("Low", false, false)]
    public async Task CanUseFixedInvokeC1_AuthenticationAndPriority_RequiresHighAuthenticationWithNormalPriority(string authentication, bool useHighPriority, bool expected)
    {
        // Arrange
        await using var vm = new MainViewModel();

        // Act
        vm.Authentication = authentication;
        vm.UseHighPriority = useHighPriority;

        // Assert
        Assert.Equal(expected, vm.CanUseFixedInvokeC1);
    }

    [Fact]
    public async Task LoadConfigurationAsync_OlderAssociationSettings_RestoresDefaultInvokeOptions()
    {
        // Arrange
        await using var vm = CreateSerialViewModel();
        var savedSerial = await SaveAsync(vm);
        var savedAssociation = savedSerial["association"]!.AsObject();
        savedAssociation.Remove("useFixedInvokeC1");
        savedAssociation.Remove("useHighPriority");
        savedAssociation.Remove("ignoreInvokeIdValidation");
        await using var restored = new MainViewModel { Authentication = "High", UseFixedInvokeC1 = true, UseHighPriority = true, IgnoreInvokeIdValidation = true };

        // Act
        await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(savedSerial.ToJsonString())));

        // Assert
        Assert.False(restored.UseFixedInvokeC1, "An older profile did not restore the default DLMS settings");
        Assert.False(restored.UseHighPriority, "An older profile did not restore the default DLMS settings");
        Assert.False(restored.IgnoreInvokeIdValidation, "An older profile did not restore the default DLMS settings");
    }

    [Fact]
    public async Task SaveConfigurationAsync_IdleTimeoutBelowMinimum_ThrowsArgumentException()
    {
        // Arrange
        await using var vm = CreateSerialViewModel();
        vm.IdleTimeoutSeconds = "2.25";
        using var stream = new MemoryStream();

        // Act
        var exception = await Record.ExceptionAsync(() => vm.SaveConfigurationAsync(stream));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task SerialHandshake_HardwareFlowControl_TakesOverRts()
    {
        // Arrange
        await using var restored = new MainViewModel { SerialRtsEnable = true };

        // Act
        restored.SerialHandshake = Handshake.RequestToSend;

        // Assert
        Assert.False(restored.CanSetSerialRts, "Hardware handshake did not take over RTS");
        Assert.False(restored.SerialRtsEnable, "Hardware handshake did not take over RTS");
    }

    [Fact]
    public async Task SerialHandshake_DisablingHardwareFlowControl_RestoresManualRts()
    {
        // Arrange
        await using var restored = new MainViewModel { SerialHandshake = Handshake.RequestToSend };

        // Act
        restored.SerialHandshake = Handshake.None;

        // Assert
        Assert.True(restored.CanSetSerialRts, "Manual RTS not restored");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("bad")]
    public async Task SaveConfigurationAsync_InvalidBaudRate_ThrowsArgumentException(string invalid)
    {
        // Arrange
        await using var vm = CreateSerialViewModel();
        vm.SerialBaudRate = invalid;
        using var stream = new MemoryStream();

        // Act
        var exception = await Record.ExceptionAsync(() => vm.SaveConfigurationAsync(stream));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task SaveConfigurationAsync_WhitespaceSerialPort_ThrowsArgumentException()
    {
        // Arrange
        await using var vm = CreateSerialViewModel();
        vm.SerialPortName = " ";
        using var stream = new MemoryStream();

        // Act
        var exception = await Record.ExceptionAsync(() => vm.SaveConfigurationAsync(stream));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task SaveConfigurationAsync_TcpAndWrapper_OmitsSerialAndHdlcSettings()
    {
        // Arrange
        await using var vm = new MainViewModel
        {
            Host = "127.0.0.1",
            TcpPort = "4059",
            Framing = "Wrapper",
            ServerPhysicalAddress = "unused",
            HdlcAcknowledgementTimeoutSeconds = "unused"
        };
        await using var tcpJson = new MemoryStream();

        // Act
        await vm.SaveConfigurationAsync(tcpJson);

        // Assert
        var savedTcp = JsonNode.Parse(tcpJson.ToArray())!;
        var tcpSettings = savedTcp["channel"]!["connection"]!.AsObject();
        Assert.Equal("Tcp", tcpSettings["kind"]!.GetValue<string>());
        Assert.Null(tcpSettings["portName"]);
        var savedWrapper = savedTcp["channel"]!["framing"]!;
        Assert.Null(savedWrapper["informationFrameRetryCount"]);
        Assert.Null(savedWrapper["acknowledgementTimeoutSeconds"]);
    }

    [Fact]
    public async Task LoadConfigurationAsync_TcpSettingsWithoutKind_InfersTcp()
    {
        // Arrange
        await using var vm = new MainViewModel
        {
            Host = "127.0.0.1",
            TcpPort = "4059",
            Framing = "Wrapper",
            ServerPhysicalAddress = "unused",
            HdlcAcknowledgementTimeoutSeconds = "unused"
        };
        await using var restored = new MainViewModel();
        var savedTcp = await SaveAsync(vm);
        var tcpSettings = savedTcp["channel"]!["connection"]!.AsObject();
        tcpSettings.Remove("kind");

        // Act
        await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(savedTcp.ToJsonString())));

        // Assert
        Assert.True(restored.IsTcp && restored.Host == "127.0.0.1" && restored.TcpPort == "4059" && restored.Framing == "Wrapper",
            "TCP configuration without kind did not load: " + restored.Status);
    }

    [Fact]
    public async Task LoadConfigurationAsync_LegacyHdlcTcpSettings_UsesSafeRecoveryDefaults()
    {
        // Arrange
        await using var vm = new MainViewModel
        {
            Host = "127.0.0.1",
            TcpPort = "4059",
            Framing = "Wrapper",
            ServerPhysicalAddress = "unused",
            HdlcAcknowledgementTimeoutSeconds = "unused"
        };
        await using var restored = new MainViewModel();
        var savedTcp = await SaveAsync(vm);
        var legacy = new JsonObject
        {
            ["connection"] = new JsonObject { ["host"] = "localhost", ["tcpPort"] = 93, ["transport"] = "HdlcTcp" },
            ["association"] = savedTcp["association"]!.DeepClone()
        };

        // Act
        await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(legacy.ToJsonString())));

        // Assert
        Assert.True(restored.IsTcp && restored.Host == "localhost" && restored.TcpPort == "93" && restored.IsHdlc,
            "Legacy TCP configuration did not load: " + restored.Status);
        Assert.Equal(0, restored.HdlcInformationFrameRetryCount);
        Assert.Equal("1", restored.HdlcAcknowledgementTimeoutSeconds);
    }

    [Fact]
    public void CreateTransportFactory_Serial_ForwardsSerialSettingsAndTimeout()
    {
        // Arrange
        var options = CreateSerialOptions();

        // Act
        var factory = options.CreateTransportFactory();

        // Assert
        var serialFactory = Assert.IsType<SerialByteTransportFactory>(factory);
        Assert.Equal(options.Serial, serialFactory.Settings);
        Assert.Equal(TimeSpan.FromSeconds(10), serialFactory.Timeout);
    }

    [Fact]
    public void CreateTransportFactory_Tcp_ReturnsTcpFactory()
    {
        // Arrange
        var options = CreateSerialOptions() with { Transport = "TCP", Host = "localhost", TcpPort = 4059 };

        // Act
        var factory = options.CreateTransportFactory();

        // Assert
        Assert.IsType<TcpByteTransportFactory>(factory);
    }

    private static MainViewModel CreateSerialViewModel() => new()
    {
        Transport = "Serial",
        SerialPortName = "/dev/test-serial",
        SerialBaudRate = "19200",
        SerialDataBits = 7,
        SerialParity = Parity.Even,
        SerialStopBits = StopBits.Two,
        SerialHandshake = Handshake.XOnXOff,
        SerialDtrEnable = true,
        SerialRtsEnable = true,
        HdlcInformationFrameRetryCount = 1,
        HdlcAcknowledgementTimeoutSeconds = "0.75",
        Authentication = "High",
        UseHighPriority = true,
        IgnoreInvokeIdValidation = true
    };

    private static async Task<JsonNode> SaveAsync(MainViewModel viewModel)
    {
        using var stream = new MemoryStream();
        await viewModel.SaveConfigurationAsync(stream);
        return JsonNode.Parse(stream.ToArray())!;
    }

    private static MeterConnectionOptions CreateSerialOptions() =>
        new MeterConnectionOptions("", 0, "HDLC", 16, 1, null, false, 1, "0.0.40.0.0.255",
            EMCL.Client.Association.ClientAuthentication.None, "", "UTF-8", 10, 30, 20, 65535,
            EMCL.Protocol.Apdu.DlmsConformance.Get, false, new EMCL.Core.Codecs.ProtocolLimits(),
            "Serial", new SerialPortSettings("/dev/test-serial", 19200, 7, Parity.Even, StopBits.Two));
}
