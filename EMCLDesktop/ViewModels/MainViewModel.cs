using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EMCL.Client.Association;
using EMCL.Client.Exceptions;
using EMCL.Core.Codecs;
using EMCL.Core.Objects;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Objects.Definitions;
using EMCL.Objects.ProfileGeneric;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Transport.Streams;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private const int ObjectsTabIndex = 0;
    private const int InspectorTabIndex = 1;
    private const int TraceTabIndex = 2;

    private MeterConnection? connection;
    private Task closingConnectionTask = Task.CompletedTask;
    private MeterObjectViewModel? previousSelectedObject;
    private int selectionVersion;
    private ProtocolLimits limits = new();
    private readonly DiagnosticTrace trace = new();
    private int traceRefreshPending;
    private readonly CancellationTokenSource shutdown = new();
    private TaskCompletionSource? connecting;
    private int treeReadVersion;
    private int profileReadVersion;
    private int disposed;

    public IReadOnlyList<string> Transports { get; } = ["TCP", "Serial"];
    public ObservableCollection<string> SerialPortNames { get; } = [];
    public IReadOnlyList<int> SerialDataBitsOptions { get; } = [5, 6, 7, 8];
    public IReadOnlyList<Parity> SerialParities { get; } = Enum.GetValues<Parity>();
    public IReadOnlyList<StopBits> SerialStopBitsOptions { get; } =
        [StopBits.One, StopBits.OnePointFive, StopBits.Two];
    public IReadOnlyList<Handshake> SerialHandshakes { get; } = Enum.GetValues<Handshake>();
    public IReadOnlyList<string> Framings { get; } = ["Wrapper", "HDLC"];
    public IReadOnlyList<string> Authentications { get; } = ["None", "Low", "High"];
    public IReadOnlyList<string> PasswordFormats { get; } = ["UTF-8", "Hex", "Environment"];
    public IReadOnlyList<string> TraceFormats { get; } =
        ["Словесный", "Словесный + HEX", "Чистый HEX TX/RX"];
    public IReadOnlyList<string> AssociationVersions { get; } = ["1", "3"];
    public IReadOnlyList<ConformanceOptionViewModel> ConformanceOptions { get; } =
    [
        new("Get", DlmsConformance.Get),
        new("Set", DlmsConformance.Set),
        new("Action", DlmsConformance.Action),
        new("SelectiveAccess", DlmsConformance.SelectiveAccess),
        new("BlockTransferWithGet", DlmsConformance.BlockTransferWithGet),
        new("BlockTransferWithSet", DlmsConformance.BlockTransferWithSet),
        new("DataNotification", DlmsConformance.DataNotification),
        new("PriorityManagement", DlmsConformance.PriorityManagement)
    ];
    public ObservableCollection<MeterObjectViewModel> Objects { get; } = [];
    public ObservableCollection<ObjectGroupViewModel> VisibleGroups { get; } = [];

    [ObservableProperty] public partial string Transport { get; set; } = "TCP";
    [ObservableProperty] public partial string SerialPortName { get; set; } = "";
    [ObservableProperty] public partial string SerialPortsStatus { get; set; } = "";
    [ObservableProperty] public partial string SerialBaudRate { get; set; } = "9600";
    [ObservableProperty] public partial int SerialDataBits { get; set; } = 8;
    [ObservableProperty] public partial Parity SerialParity { get; set; } = Parity.None;
    [ObservableProperty] public partial StopBits SerialStopBits { get; set; } = StopBits.One;
    [ObservableProperty] public partial Handshake SerialHandshake { get; set; } = Handshake.None;
    [ObservableProperty] public partial bool SerialDtrEnable { get; set; }
    [ObservableProperty] public partial bool SerialRtsEnable { get; set; }
    [ObservableProperty] public partial string Host { get; set; } = "10.10.42.217";
    [ObservableProperty] public partial string TcpPort { get; set; } = "90";
    [ObservableProperty] public partial string Framing { get; set; } = "HDLC";
    [ObservableProperty] public partial string ClientAddress { get; set; } = "48";
    [ObservableProperty] public partial string ServerAddress { get; set; } = "1";
    [ObservableProperty] public partial string ServerPhysicalAddress { get; set; } = "17";
    [ObservableProperty] public partial bool ExtendedHdlcAddress { get; set; }
    [ObservableProperty] public partial string AssociationVersion { get; set; } = "1";
    [ObservableProperty] public partial string AssociationObis { get; set; } = "0.0.40.0.0.255";
    [ObservableProperty] public partial string Authentication { get; set; } = "High";
    [ObservableProperty] public partial string Password { get; set; } = "0000000100000001";
    [ObservableProperty] public partial string PasswordFormat { get; set; } = "UTF-8";
    [ObservableProperty] public partial string ConnectTimeoutSeconds { get; set; } = "10";
    [ObservableProperty] public partial string IdleTimeoutSeconds { get; set; } = "120";
    [ObservableProperty] public partial string KeepAliveIntervalSeconds { get; set; } = "20";
    [ObservableProperty] public partial string MaxReceivePduSize { get; set; } = "65535";
    [ObservableProperty] public partial bool IgnoreIdenticalObjectListEntries { get; set; } = true;
    private string status = "Укажите параметры и подключитесь к счётчику.";
    private bool hasStatusError;
    public string Status
    {
        get => status;
        set
        {
            HasStatusError = false;
            SetProperty(ref status, value);
        }
    }
    public bool HasStatusError
    {
        get => hasStatusError;
        private set => SetProperty(ref hasStatusError, value);
    }
    public void ReportError(string message)
    {
        Status = message;
        HasStatusError = !string.IsNullOrWhiteSpace(message);
    }
    [ObservableProperty] public partial string TraceText { get; set; } = "";
    [ObservableProperty] public partial int SelectedTraceFormatIndex { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; }
    [ObservableProperty] public partial bool IsDisconnecting { get; set; }
    [ObservableProperty] public partial bool IsReading { get; set; }
    [ObservableProperty] public partial bool IsTreeReadInProgress { get; set; }
    [ObservableProperty] public partial int TreeReadCurrent { get; set; }
    [ObservableProperty] public partial int TreeReadTotal { get; set; }
    [ObservableProperty] public partial string TreeReadProgressText { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileReadInProgress), nameof(IsProfileReadIndeterminate),
        nameof(ProfileReadProgressMaximum), nameof(ProfileReadProgressCurrent), nameof(ProfileReadProgressText))]
    private partial ProfileBufferProgress? ProfileReadProgress { get; set; }
    [ObservableProperty] public partial int SelectedTabIndex { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool UseRegularExpression { get; set; }
    [ObservableProperty] public partial string SearchError { get; set; } = "";
    [ObservableProperty] public partial int VisibleObjectCount { get; set; }
    [ObservableProperty] public partial MeterObjectViewModel? SelectedObject { get; set; }

    public bool IsTcp => Transport == "TCP";
    public bool IsSerial => Transport == "Serial";
    public bool CanSetSerialRts => SerialHandshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff);
    public bool IsHdlc => Framing == "HDLC";
    public bool HasPassword => Authentication != "None";
    public bool HasNoSelectedObject => SelectedObject is null;
    public string ConnectionState => IsConnecting ? "Подключение…" : IsDisconnecting ? "Отключение…"
        : IsConnected ? "Подключено" : "Не подключено";
    public string ConnectionActionText => IsConnected || IsDisconnecting ? "Отключиться" : "Подключиться";
    public bool IsConnectionChanging => IsConnecting || IsDisconnecting;
    public bool CanSelectObjects => !IsReading && !IsBusy;
    public bool CanEditConfiguration => !IsReading && !IsBusy;
    public int TreeReadProgressMaximum => Math.Max(1, TreeReadTotal);
    public bool IsTreeReadIndeterminate => TreeReadTotal == 0;
    private int? ProfileReadTotal => ProfileReadProgress?.TotalRows ?? ProfileReadProgress?.TotalBytes;
    public bool IsProfileReadInProgress => ProfileReadProgress.HasValue;
    public bool IsProfileReadIndeterminate => !ProfileReadTotal.HasValue;
    public int ProfileReadProgressMaximum => Math.Max(1, ProfileReadTotal.GetValueOrDefault());
    public int ProfileReadProgressCurrent => ProfileReadProgress switch
    {
        { TotalRows: not null } progress => progress.RowsRead,
        { TotalBytes: not null } progress => progress.BytesRead,
        _ => 0
    };
    public string ProfileReadProgressText => ProfileReadProgress switch
    {
        { TotalRows: { } total } progress => $"Чтение профиля · строк: {progress.RowsRead} из {total}",
        { TotalBytes: { } total } progress =>
            $"Чтение профиля · байт: {progress.BytesRead} из {total} · строк: {progress.RowsRead}",
        _ => "Ожидание заголовка буфера профиля…"
    };
    public bool IsObjectsTab => SelectedTabIndex == ObjectsTabIndex;

    public PacketInspectorViewModel Inspector { get; }

    public MainViewModel()
    {
        Inspector = new(trace);
        trace.Changed += OnTraceChanged;
    }

    public string GetTraceSnapshot() => trace.Snapshot();

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsObjectsTab));
        Inspector.IsActive = value == InspectorTabIndex;
        if (value == TraceTabIndex) TraceText = trace.Snapshot();
    }

    private void OnTraceChanged()
    {
        if (SelectedTabIndex != TraceTabIndex || Interlocked.Exchange(ref traceRefreshPending, 1) != 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref traceRefreshPending, 0);
            if (SelectedTabIndex == TraceTabIndex) TraceText = trace.Snapshot();
        });
    }

    partial void OnTransportChanged(string value)
    {
        OnPropertyChanged(nameof(IsTcp));
        OnPropertyChanged(nameof(IsSerial));
        if (IsSerial) RefreshSerialPorts();
    }

    partial void OnSerialPortNameChanged(string value)
    {
        // Keep a port loaded from JSON selectable even while its adapter is unplugged.
        if (!string.IsNullOrWhiteSpace(value) && !SerialPortNames.Contains(value))
            SerialPortNames.Add(value);
    }

    [RelayCommand]
    private void RefreshSerialPorts()
    {
        try
        {
            var ports = SerialPort.GetPortNames().Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            var selected = SerialPortName;
            // Keep the selected item in place so refreshing does not clear the binding.
            foreach (var stale in SerialPortNames.Where(name => name != selected && !ports.Contains(name)).ToArray())
                SerialPortNames.Remove(stale);
            foreach (var port in ports)
                if (!SerialPortNames.Contains(port)) SerialPortNames.Add(port);
            if (string.IsNullOrWhiteSpace(selected) && ports.Length > 0)
                SerialPortName = ports[0];
            SerialPortsStatus = ports.Length == 0
                ? "Serial-порты не найдены. Подключите адаптер и нажмите «Обновить»."
                : !string.IsNullOrWhiteSpace(selected) && !ports.Contains(selected)
                    ? "Выбранный порт сейчас не обнаружен."
                    : "";
        }
        catch (Exception error)
        {
            SerialPortsStatus = $"Не удалось получить список портов: {error.Message}";
        }
    }

    partial void OnSerialHandshakeChanged(Handshake value)
    {
        if (!CanSetSerialRts) SerialRtsEnable = false;
        OnPropertyChanged(nameof(CanSetSerialRts));
    }
    partial void OnFramingChanged(string value) => OnPropertyChanged(nameof(IsHdlc));
    partial void OnSelectedTraceFormatIndexChanged(int value)
    {
        trace.DisplayFormat = value switch
        {
            1 => TraceDisplayFormat.VerbalWithHex,
            2 => TraceDisplayFormat.HexOnly,
            _ => TraceDisplayFormat.Verbal
        };
        TraceText = trace.Snapshot();
    }
    partial void OnAuthenticationChanged(string value) => OnPropertyChanged(nameof(HasPassword));
    private void NotifyConnectionDisplayChanged()
    {
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(ConnectionActionText));
        OnPropertyChanged(nameof(IsConnectionChanging));
    }
    partial void OnIsConnectingChanged(bool value) => NotifyConnectionDisplayChanged();
    partial void OnIsDisconnectingChanged(bool value) => NotifyConnectionDisplayChanged();
    partial void OnIsConnectedChanged(bool value)
    {
        NotifyConnectionDisplayChanged();
        ReadSelectedObjectCommand.NotifyCanExecuteChanged();
        ReadTreeCommand.NotifyCanExecuteChanged();
        UpdateObjectOperationAvailability();
    }
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectObjects));
        OnPropertyChanged(nameof(CanEditConfiguration));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        ReadSelectedObjectCommand.NotifyCanExecuteChanged();
        ReadTreeCommand.NotifyCanExecuteChanged();
        UpdateObjectOperationAvailability();
    }
    partial void OnIsReadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectObjects));
        OnPropertyChanged(nameof(CanEditConfiguration));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        ReadSelectedObjectCommand.NotifyCanExecuteChanged();
        ReadTreeCommand.NotifyCanExecuteChanged();
        UpdateObjectOperationAvailability();
    }
    partial void OnTreeReadTotalChanged(int value)
    {
        OnPropertyChanged(nameof(TreeReadProgressMaximum));
        OnPropertyChanged(nameof(IsTreeReadIndeterminate));
    }

    private IProgress<ObjectListProgress> StartTreeReadProgress()
    {
        int version = ++treeReadVersion;
        TreeReadCurrent = 0;
        TreeReadTotal = 0;
        TreeReadProgressText = "Ожидание заголовка object_list…";
        IsTreeReadInProgress = true;
        return new Progress<ObjectListProgress>(value =>
        {
            if (!IsTreeReadInProgress || version != treeReadVersion) return;
            TreeReadCurrent = value.Current;
            TreeReadTotal = value.Total;
            TreeReadProgressText = $"Считано записей дерева: {value.Current} из {value.Total}";
        });
    }

    private void FinishTreeReadProgress()
    {
        ++treeReadVersion;
        IsTreeReadInProgress = false;
    }
    partial void OnSearchTextChanged(string value) => ApplyObjectFilter();
    partial void OnUseRegularExpressionChanged(bool value) => ApplyObjectFilter();

    public async Task LoadConfigurationAsync(Stream stream)
    {
        if (IsBusy || IsReading) return;
        IsBusy = true;
        try
        {
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            if (root.TryGetProperty("format", out var format) &&
                format.GetString() is not (SavedConfiguration.Format or SavedConfiguration.LegacyFormat))
                throw new FormatException("Неизвестный формат конфигурации.");
            if (root.TryGetProperty("schemaVersion", out var schemaVersion) &&
                schemaVersion.GetInt32() != SavedConfiguration.Version)
                throw new FormatException("Неподдерживаемая версия сохранённой конфигурации.");
            MeterObjectViewModel[]? savedObjects = null;
            if (root.TryGetProperty("catalog", out var catalogElement))
            {
                var catalog = JsonSerializer.Deserialize<SavedCatalog>(
                    catalogElement.GetRawText(), SavedConfiguration.JsonOptions)
                    ?? throw new FormatException("Не удалось прочитать сохранённый каталог.");
                savedObjects = SavedConfiguration.RestoreCatalog(catalog);
            }
            bool legacy = root.TryGetProperty("connection", out var connectionSettings);
            JsonElement framingSettings;
            if (legacy)
            {
                framingSettings = connectionSettings;
            }
            else
            {
                var channel = root.GetProperty("channel");
                connectionSettings = channel.GetProperty("connection");
                framingSettings = channel.GetProperty("framing");
            }
            var association = root.GetProperty("association");
            var importedTransport = legacy ? "TCP" : GetString(connectionSettings, "kind", "Tcp") switch
            {
                "Tcp" => "TCP",
                "Serial" => "Serial",
                _ => throw new FormatException("Тип подключения должен быть Tcp или Serial.")
            };
            var importedHost = importedTransport == "TCP" ? connectionSettings.GetProperty("host").GetString() ?? "" : Host;
            var importedPort = importedTransport == "TCP" ? connectionSettings.GetProperty("tcpPort").GetInt32() : 0;
            SerialPortSettings? importedSerial = null;
            if (importedTransport == "Serial")
            {
                importedSerial = new SerialPortSettings(
                    GetString(connectionSettings, "portName", ""),
                    GetInt(connectionSettings, "baudRate", 9600),
                    GetInt(connectionSettings, "dataBits", 8),
                    Enum.Parse<Parity>(GetString(connectionSettings, "parity", "None"), true),
                    Enum.Parse<StopBits>(GetString(connectionSettings, "stopBits", "One"), true),
                    Enum.Parse<Handshake>(GetString(connectionSettings, "handshake", "None"), true),
                    GetBool(connectionSettings, "dtrEnable", false),
                    GetBool(connectionSettings, "rtsEnable", false));
                importedSerial.Validate();
            }
            var importedFraming = legacy
                ? (GetString(framingSettings, "transport", "WrapperTcp") == "HdlcTcp" ? "HDLC" : "Wrapper")
                : (GetString(framingSettings, "kind", "Wrapper") == "Hdlc" ? "HDLC" : "Wrapper");
            string importedClient = importedFraming == "HDLC"
                ? GetInt(framingSettings, legacy ? "hdlcClientAddress" : "clientAddress", 16).ToString(CultureInfo.InvariantCulture)
                : GetInt(framingSettings, legacy ? "clientWrapperPort" : "clientPort", 16).ToString(CultureInfo.InvariantCulture);
            string importedServer = importedFraming == "HDLC"
                ? GetInt(framingSettings, legacy ? "hdlcServerLogicalAddress" : "serverLogicalAddress", 1).ToString(CultureInfo.InvariantCulture)
                : GetInt(framingSettings, legacy ? "serverWrapperPort" : "serverPort", 1).ToString(CultureInfo.InvariantCulture);
            string importedPhysical = GetOptionalInt(framingSettings,
                legacy ? "hdlcServerPhysicalAddress" : "serverPhysicalAddress")?.ToString(CultureInfo.InvariantCulture) ?? "";
            bool importedExtended = GetBool(framingSettings,
                legacy ? "hdlcExtendedAddress" : "extendedAddress", false);
            var importedLimits = root.TryGetProperty("limits", out var limitSettings)
                ? new ProtocolLimits
                {
                    MaxMessageBytes = GetInt(limitSettings, "maxMessageBytes", 1024 * 1024),
                    MaxDepth = GetInt(limitSettings, "maxDepth", 64),
                    MaxCollectionItems = GetInt(limitSettings, "maxCollectionItems", 100000),
                    MaxBlockCount = GetInt(limitSettings, "maxBlockCount", 4096)
                }
                : new ProtocolLimits();
            importedLimits.Validate();
            var importedConformance = Enum.Parse<DlmsConformance>(
                GetString(association, "conformance", "Get, BlockTransferWithGet"), true);
            if ((importedConformance & ~LnAssociationOptions.SupportedConformance) != 0)
                throw new ArgumentException("Конфигурация содержит неподдерживаемые флаги Conformance.");

            string importedPassword = "";
            string importedPasswordFormat = "UTF-8";
            if (GetNullableString(association, "password") is { } textPassword)
                importedPassword = textPassword;
            else if (GetNullableString(association, "passwordHex") is { } hexPassword)
            {
                importedPassword = hexPassword;
                importedPasswordFormat = "Hex";
            }
            else if (GetNullableString(association, "passwordEnvironmentVariable") is { } variable)
            {
                importedPassword = variable;
                importedPasswordFormat = "Environment";
            }

            IsConnected = false;
            await CloseConnectionAsync();
            ++selectionVersion;
            SelectedObject = null;
            Objects.Clear();
            VisibleGroups.Clear();
            VisibleObjectCount = 0;
            Transport = importedTransport;
            if (importedSerial is not null)
            {
                SerialPortName = importedSerial.PortName;
                SerialBaudRate = importedSerial.BaudRate.ToString(CultureInfo.InvariantCulture);
                SerialDataBits = importedSerial.DataBits;
                SerialParity = importedSerial.Parity;
                SerialStopBits = importedSerial.StopBits;
                SerialHandshake = importedSerial.Handshake;
                SerialDtrEnable = importedSerial.DtrEnable;
                SerialRtsEnable = importedSerial.RtsEnable;
            }
            else
            {
                Host = importedHost;
                TcpPort = importedPort.ToString(CultureInfo.InvariantCulture);
            }
            Framing = importedFraming;
            ClientAddress = importedClient;
            ServerAddress = importedServer;
            ServerPhysicalAddress = importedPhysical;
            ExtendedHdlcAddress = importedExtended;
            AssociationVersion = association.GetProperty("version").GetByte().ToString(CultureInfo.InvariantCulture);
            AssociationObis = association.GetProperty("logicalName").GetString() ?? "";
            Authentication = GetString(association, "authentication", "None");
            Password = importedPassword;
            PasswordFormat = importedPasswordFormat;
            ConnectTimeoutSeconds = GetDouble(connectionSettings, "connectTimeoutSeconds", 10)
                .ToString(CultureInfo.InvariantCulture);
            IdleTimeoutSeconds = GetDouble(association, "requestTimeoutSeconds", 30)
                .ToString(CultureInfo.InvariantCulture);
            KeepAliveIntervalSeconds = GetDouble(association, "keepAliveIntervalSeconds", 20)
                .ToString(CultureInfo.InvariantCulture);
            MaxReceivePduSize = GetInt(association, "maxReceivePduSize", 65535)
                .ToString(CultureInfo.InvariantCulture);
            foreach (var option in ConformanceOptions)
                option.IsSelected = (importedConformance & option.Flag) == option.Flag;
            IgnoreIdenticalObjectListEntries = GetBool(association, "ignoreIdenticalObjectListEntries", false);
            limits = importedLimits;
            if (savedObjects != null)
            {
                foreach (var obj in savedObjects) Objects.Add(obj);
                ApplyObjectFilter();
                Status = $"Настройки и каталог загружены из JSON: {Objects.Count} объектов. Для чтения значений подключитесь.";
            }
            else
            {
                Status = "Настройки загружены из JSON. Проверьте поля и подключитесь.";
            }
        }
        catch (Exception error)
        {
            ReportError($"Не удалось загрузить конфигурацию: {error.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveConfigurationAsync(Stream stream)
    {
        if (IsBusy || IsReading)
            throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        var options = ParseOptions();
        var snapshot = new
        {
            format = SavedConfiguration.Format,
            schemaVersion = SavedConfiguration.Version,
            channel = new
            {
                connection = options.Transport == "Serial"
                    ? (object)new
                    {
                        kind = "Serial",
                        portName = options.Serial!.PortName,
                        baudRate = options.Serial.BaudRate,
                        dataBits = options.Serial.DataBits,
                        parity = options.Serial.Parity.ToString(),
                        stopBits = options.Serial.StopBits.ToString(),
                        handshake = options.Serial.Handshake.ToString(),
                        dtrEnable = options.Serial.DtrEnable,
                        rtsEnable = options.Serial.RtsEnable,
                        connectTimeoutSeconds = options.ConnectTimeoutSeconds
                    }
                    : new
                    {
                        kind = "Tcp",
                        host = options.Host,
                        tcpPort = options.TcpPort,
                        connectTimeoutSeconds = options.ConnectTimeoutSeconds
                    },
                framing = new
                {
                    kind = options.Framing == "HDLC" ? "Hdlc" : "Wrapper",
                    clientAddress = options.ClientAddress,
                    serverLogicalAddress = options.ServerAddress,
                    serverPhysicalAddress = options.ServerPhysicalAddress,
                    extendedAddress = options.ExtendedHdlcAddress,
                    clientPort = options.ClientAddress,
                    serverPort = options.ServerAddress
                }
            },
            association = new
            {
                version = options.AssociationVersion,
                logicalName = options.AssociationObis,
                authentication = options.Authentication.ToString(),
                password = options.PasswordFormat == "UTF-8" ? options.Password : null,
                passwordHex = options.PasswordFormat == "Hex" ? options.Password : null,
                passwordEnvironmentVariable = options.PasswordFormat == "Environment" ? options.Password : null,
                conformance = options.Conformance.ToString(),
                requestTimeoutSeconds = options.RequestTimeoutSeconds,
                keepAliveIntervalSeconds = options.KeepAliveIntervalSeconds,
                maxReceivePduSize = options.MaxReceivePduSize,
                ignoreIdenticalObjectListEntries = options.IgnoreIdenticalObjectListEntries
            },
            limits = new
            {
                maxMessageBytes = options.Limits.MaxMessageBytes,
                maxDepth = options.Limits.MaxDepth,
                maxCollectionItems = options.Limits.MaxCollectionItems,
                maxBlockCount = options.Limits.MaxBlockCount
            },
            catalog = SavedConfiguration.CaptureCatalog(Objects)
        };
        await JsonSerializer.SerializeAsync(stream, snapshot, SavedConfiguration.JsonOptions);
        await stream.FlushAsync();
        Status = $"Конфигурация и каталог ({Objects.Count} объектов) сохранены в JSON.";
    }

    private static string GetString(JsonElement element, string name, string fallback) =>
        GetNullableString(element, name) ?? fallback;

    private static string? GetNullableString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() : null;

    private static int GetInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;

    private static int? GetOptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32() : null;

    private static bool GetBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;

    private static double GetDouble(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var value) ? value.GetDouble() : fallback;

    partial void OnSelectedObjectChanged(MeterObjectViewModel? value)
    {
        OnPropertyChanged(nameof(HasNoSelectedObject));
        if (previousSelectedObject != null)
            previousSelectedObject.IsSelected = false;
        if (value != null)
            value.IsSelected = true;
        previousSelectedObject = value;
        ++selectionVersion;
        ReadSelectedObjectCommand.NotifyCanExecuteChanged();
        UpdateObjectOperationAvailability();
        if (!IsReading && IsConnected && value != null)
            Status = $"Выбран {value.Obis}. Нажмите «Считать все атрибуты» или «Считать» у нужного атрибута.";
    }

    private void UpdateObjectOperationAvailability()
    {
        if (SelectedObject is not { } obj) return;
        bool isIdle = !IsBusy && !IsReading;
        foreach (var attribute in obj.Attributes)
        {
            attribute.IsReadEnabled = isIdle;
            string reason = connection?.WriteUnavailableReason(obj.ClassId, obj.LogicalName, attribute.Index)
                ?? "Подключитесь к счётчику для записи.";
            attribute.WriteUnavailableReason = reason.Length == 0 ? null : reason;
            attribute.IsWriteEnabled = isIdle && IsConnected && reason.Length == 0;
        }
        foreach (var method in obj.Methods)
        {
            string reason = MethodInvokeUnavailableReason(obj, method, IsConnected ? connection : null);
            method.InvokeUnavailableReason = reason.Length == 0 ? null : reason;
            method.IsInvokeEnabled = isIdle && reason.Length == 0;
        }
        obj.IsProfileReadEnabled = isIdle;
    }

    private static string MethodInvokeUnavailableReason(MeterObjectViewModel obj, MethodViewModel method,
        MeterConnection? meter) => meter?.InvokeUnavailableReason(obj.ClassId, obj.LogicalName, method.Index)
        ?? "Подключитесь к счётчику для вызова метода.";

    private void ApplyObjectFilter()
    {
        try
        {
            var query = SearchText ?? "";
            Regex? regex = UseRegularExpression && query.Length > 0
                ? new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100))
                : null;
            bool Matches(MeterObjectViewModel obj)
            {
                if (query.Length == 0) return true;
                string[] fields =
                [
                    obj.Obis,
                    obj.ClassName,
                    obj.ClassId.ToString(CultureInfo.InvariantCulture),
                    $"{obj.ClassId}:{obj.ClassVersion}"
                ];
                return fields.Any(field => regex?.IsMatch(field) ??
                    field.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = Objects.Where(Matches).ToArray();
            var groups = filtered
                .GroupBy(obj => (obj.ClassId, obj.ClassVersion, obj.ClassName))
                .OrderBy(group => group.Key.ClassName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.Key.ClassId)
                .ThenBy(group => group.Key.ClassVersion)
                .Select(group => new ObjectGroupViewModel(group.Key.ClassName, group.Key.ClassId,
                    group.Key.ClassVersion, group.OrderBy(obj => obj.LogicalName.Value).ToArray()))
                .ToArray();

            VisibleGroups.Clear();
            foreach (var group in groups)
                VisibleGroups.Add(group);
            VisibleObjectCount = filtered.Length;
            SearchError = "";
            if (SelectedObject != null && !filtered.Contains(SelectedObject))
                SelectedObject = null;
        }
        catch (ArgumentException error) when (UseRegularExpression)
        {
            VisibleGroups.Clear();
            VisibleObjectCount = 0;
            SearchError = $"Неверное регулярное выражение: {error.Message}";
        }
        catch (RegexMatchTimeoutException)
        {
            VisibleGroups.Clear();
            VisibleObjectCount = 0;
            SearchError = "Превышено время выполнения регулярного выражения.";
        }
    }

    private bool CanToggleConnection() =>
        Volatile.Read(ref disposed) == 0 && !IsBusy && !IsReading;

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnectionAsync()
    {
        if (!CanToggleConnection()) return;
        if (IsConnected)
            await DisconnectAsync();
        else
            await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (Volatile.Read(ref disposed) != 0 || IsBusy || IsReading) return;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connecting = finished;
        IsBusy = true;
        IsConnecting = true;
        Status = "Подключение к счётчику…";
        ++selectionVersion;
        IsConnected = false;
        MeterConnectionOptions? attemptedOptions = null;
        try
        {
            try
            {
                await CloseConnectionAsync();
            }
            catch (Exception)
            {
                // A faulted receive task can surface during disposal even after
                // the session has closed its transport. Reconnection can continue.
            }
            attemptedOptions = ParseOptions();
            trace.BeginConnection($"{attemptedOptions.Framing}/{attemptedOptions.Transport}");
            trace.Add($"Подключение к {attemptedOptions.Endpoint}, " +
                      $"{attemptedOptions.Framing}, клиент={attemptedOptions.ClientAddress}, " +
                      $"сервер={attemptedOptions.ServerAddress}, " +
                      $"физический адрес={attemptedOptions.ServerPhysicalAddress?.ToString() ?? "нет"}, " +
                      $"расширенный адрес={attemptedOptions.ExtendedHdlcAddress}, " +
                      $"аутентификация={attemptedOptions.Authentication}, " +
                      $"Conformance={attemptedOptions.Conformance}, " +
                      $"Max PDU={attemptedOptions.MaxReceivePduSize}; пароль скрыт");
            if (attemptedOptions.Serial is { } serial)
                trace.Add($"Serial: BaudRate={serial.BaudRate}, DataBits={serial.DataBits}, " +
                          $"Parity={serial.Parity}, StopBits={serial.StopBits}, Handshake={serial.Handshake}, " +
                          $"DTR={serial.DtrEnable}, RTS={serial.RtsEnable}");
            connection = await MeterConnection.OpenAsync(attemptedOptions, trace, shutdown.Token);
            connection.KeepAliveFailed += OnKeepAliveFailed;
            IsConnected = true;
            Status = "Подключено. Нажмите «Считать дерево», чтобы обновить каталог объектов.";
        }
        catch (IOException error) when (attemptedOptions?.Framing == "HDLC" &&
                                         error.Message.Contains("HDLC link establishment (DM)",
                                             StringComparison.Ordinal))
        {
            trace.Add("Ошибка установления HDLC: счётчик ответил DM");
            var physical = attemptedOptions.ServerPhysicalAddress is { } address
                ? $"/{address}" : " (без физического адреса)";
            var addressLength = attemptedOptions.ServerPhysicalAddress is null ? 1
                : attemptedOptions.ExtendedHdlcAddress ? 4 : 2;
            var addressLengthText = addressLength == 1 ? "1 байт" : $"{addressLength} байта";
            ReportError($"Счётчик отклонил установление HDLC (DM). " +
                     $"Использованы адреса: клиент {attemptedOptions.ClientAddress}, " +
                     $"сервер {attemptedOptions.ServerAddress}{physical}, " +
                     $"адрес сервера {addressLengthText}. " +
                     "Проверьте адресацию и наличие другого активного подключения к счётчику. " +
                     "Пароль и параметры ассоциации на этом этапе ещё не передавались.");
        }
        catch (DlmsAssociationException error) when (attemptedOptions is not null &&
            error.Response.Kind == AcseKind.Aare && error.Response.Result == 1 &&
            error.Response.Diagnostic?.Code == 1)
        {
            trace.Add("Отказ AARE: result=1, diagnostic=1 (NoReasonGiven)");
            ReportError($"Счётчик на {attemptedOptions.Endpoint} отклонил " +
                     "DLMS-ассоциацию (AARE: result=1, diagnostic=1; причина не указана). " +
                     $"Соединение {attemptedOptions.Framing} установлено, отказ получен при согласовании доступа. " +
                     $"Проверьте, что порт ведёт к нужному счётчику, и сверьте адрес клиента " +
                     $"{attemptedOptions.ClientAddress}, аутентификацию {attemptedOptions.Authentication}, " +
                     $"Conformance {attemptedOptions.Conformance} и Max PDU {attemptedOptions.MaxReceivePduSize} " +
                     "с настройками этого счётчика.");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            trace.Add("Подключение отменено при закрытии приложения");
            // The window is closing; the connection is being disposed before the process exits.
        }
        catch (Exception error)
        {
            trace.Add($"Ошибка подключения: {error.GetType().Name}");
            ReportError($"Ошибка подключения: {error.Message}");
        }
        finally
        {
            trace.Add($"Подключение {(IsConnected ? "успешно" : "не выполнено")}");
            IsConnecting = false;
            IsBusy = false;
            connecting = null;
            finished.TrySetResult();
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (IsBusy || IsReading) return;
        IsBusy = true;
        IsDisconnecting = true;
        ++selectionVersion;
        IsConnected = false;
        try
        {
            await CloseConnectionAsync();
            Status = $"Отключено. Каталог ({Objects.Count} объектов) доступен для просмотра и сохранения.";
        }
        catch (Exception error)
        {
            ReportError($"Ошибка отключения: {error.Message}");
        }
        finally
        {
            IsDisconnecting = false;
            IsBusy = false;
        }
    }

    private bool CanReadTree() => IsConnected && !IsBusy && !IsReading;

    [RelayCommand(CanExecute = nameof(CanReadTree))]
    private async Task ReadTreeAsync()
    {
        var meter = connection;
        if (meter?.IsActive != true)
        {
            IsConnected = false;
            ReportError("Нет активного соединения. Подключитесь к счётчику повторно.");
            return;
        }
        IsBusy = true;
        Status = "Чтение дерева объектов…";
        try
        {
            await meter.RefreshCatalogAsync(StartTreeReadProgress(), shutdown.Token);
            ++selectionVersion;
            PopulateObjects(meter);
            SelectedTabIndex = ObjectsTabIndex;
            Status = $"Дерево обновлено: {Objects.Count} объектов" +
                (meter.SkippedDuplicateEntries > 0
                    ? $"; пропущено одинаковых записей: {meter.SkippedDuplicateEntries}" : "") + ".";
        }
        catch (Exception error)
        {
            ReportError($"Не удалось считать дерево: {error.Message}");
            if (!meter.IsActive) IsConnected = false;
        }
        finally
        {
            FinishTreeReadProgress();
            IsBusy = false;
        }
    }

    private MeterConnectionOptions ParseOptions()
    {
        int port = 0;
        SerialPortSettings? serial = null;
        if (IsTcp && !int.TryParse(TcpPort, NumberStyles.None, CultureInfo.InvariantCulture, out port))
            throw new ArgumentException("Укажите числовой TCP-порт.");
        if (IsSerial)
        {
            if (!int.TryParse(SerialBaudRate, NumberStyles.None, CultureInfo.InvariantCulture, out int baudRate) || baudRate <= 0)
                throw new ArgumentException("BaudRate должен быть положительным целым числом.");
            serial = new SerialPortSettings(SerialPortName.Trim(), baudRate, SerialDataBits,
                SerialParity, SerialStopBits, SerialHandshake, SerialDtrEnable, SerialRtsEnable);
        }
        if (!ushort.TryParse(ClientAddress, NumberStyles.None, CultureInfo.InvariantCulture, out ushort client) ||
            !ushort.TryParse(ServerAddress, NumberStyles.None, CultureInfo.InvariantCulture, out ushort server))
            throw new ArgumentException("Адреса клиента и сервера должны быть числами.");
        ushort? physical = null;
        if (IsHdlc && !string.IsNullOrWhiteSpace(ServerPhysicalAddress))
        {
            if (!ushort.TryParse(ServerPhysicalAddress, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ushort parsedPhysical))
                throw new ArgumentException("Физический адрес сервера должен быть числом.");
            physical = parsedPhysical;
        }
        if (!byte.TryParse(AssociationVersion, NumberStyles.None, CultureInfo.InvariantCulture, out byte version))
            throw new ArgumentException("Выберите версию ассоциации.");
        if (!Enum.TryParse<ClientAuthentication>(Authentication, out var auth))
            throw new ArgumentException("Выберите способ аутентификации.");
        if (!double.TryParse(ConnectTimeoutSeconds, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double connectTimeout) ||
            !double.TryParse(IdleTimeoutSeconds, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double idleTimeout) ||
            !double.TryParse(KeepAliveIntervalSeconds, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double keepAliveInterval))
            throw new ArgumentException("Таймауты должны быть числами в секундах.");
        if (!ushort.TryParse(MaxReceivePduSize, NumberStyles.None,
                CultureInfo.InvariantCulture, out ushort maxPdu))
            throw new ArgumentException("Максимальный PDU должен быть числом от 0 до 65535.");
        var conformance = ConformanceOptions.Aggregate(DlmsConformance.None,
            (flags, option) => option.IsSelected ? flags | option.Flag : flags);
        var options = new MeterConnectionOptions(Host.Trim(), port, Framing, client, server, physical, ExtendedHdlcAddress,
            version,
            AssociationObis.Trim(), auth, Password, PasswordFormat,
            connectTimeout, idleTimeout, keepAliveInterval, maxPdu, conformance,
            IgnoreIdenticalObjectListEntries, limits, Transport, serial);
        _ = options.CreateTransportFactory();
        return options;
    }

    private async Task CloseConnectionAsync()
    {
        var oldConnection = connection;
        connection = null;
        if (oldConnection is not null)
        {
            oldConnection.KeepAliveFailed -= OnKeepAliveFailed;
            closingConnectionTask = Task.WhenAll(closingConnectionTask,
                oldConnection.DisposeAsync().AsTask());
        }

        var pending = closingConnectionTask;
        try
        {
            await pending;
        }
        finally
        {
            if (ReferenceEquals(closingConnectionTask, pending))
                closingConnectionTask = Task.CompletedTask;
        }
    }

    private void OnKeepAliveFailed(MeterConnection meter, Exception error) =>
        Dispatcher.UIThread.Post(() => _ = HandleKeepAliveFailureAsync(meter, error));

    private async Task HandleKeepAliveFailureAsync(MeterConnection meter, Exception error)
    {
        if (connection != meter) return;
        if (meter.IsActive)
        {
            ReportError($"Ошибка KeepAlive: {error.Message}. Проверьте соединение или подключитесь повторно.");
            return;
        }

        IsConnected = false;
        ReportError($"Ошибка KeepAlive: {error.Message}. Соединение потеряно; подключитесь повторно.");
        bool ownsBusy = !IsBusy;
        if (ownsBusy) IsBusy = true;
        try
        {
            await CloseConnectionAsync();
        }
        catch (Exception cleanupError)
        {
            ReportError($"{Status} Ошибка закрытия старой сессии: {cleanupError.Message}");
        }
        finally
        {
            if (ownsBusy) IsBusy = false;
        }
    }

    private void PopulateObjects(MeterConnection meter)
    {
        var imported = meter.Association
            ?? throw new InvalidOperationException("Дерево объектов ещё не считано.");
        var rows = new List<MeterObjectViewModel>();
        foreach (var obj in imported.Catalog.Objects.OrderBy(item => item.LogicalName.Value))
        {
            var descriptor = obj.Descriptor;
            var rights = imported.Access.Rights[obj.LogicalName];
            var row = new MeterObjectViewModel(obj.LogicalName, descriptor.Key.ClassId,
                descriptor.Key.ClassVersion, descriptor.IsKnown ? descriptor.Name : null);
            var definedAttributes = descriptor.IsKnown
                ? descriptor.Attributes.ToDictionary(item => item.Index)
                : [];
            var reportedAttributes = rights.Attributes.ToDictionary(item => item.Index);
            foreach (var index in definedAttributes.Keys.Union(reportedAttributes.Keys)
                         .Append(CosemObject.LogicalNameAttributeIndex).Distinct().Order())
            {
                definedAttributes.TryGetValue(index, out var definition);
                reportedAttributes.TryGetValue(index, out var access);
                var attribute = new AttributeViewModel(index, definition?.Name, definition?.Type?.ToString(),
                    DlmsDisplay.AttributeAccess(access, imported.Access.AssociationVersion), meter.CanRead(access));
                if (index == CosemObject.LogicalNameAttributeIndex && !attribute.HasDeclaredType)
                    attribute.Type = "OctetString (9)";
                row.Attributes.Add(attribute);
            }
            var definedMethods = descriptor.IsKnown
                ? descriptor.Methods.ToDictionary(item => item.Index)
                : [];
            var reportedMethods = rights.Methods.ToDictionary(item => item.Index);
            foreach (var index in definedMethods.Keys.Union(reportedMethods.Keys).Order())
            {
                definedMethods.TryGetValue(index, out var definition);
                reportedMethods.TryGetValue(index, out var access);
                row.Methods.Add(new(index, definition?.Name, definition?.ParameterType?.ToString(),
                    DlmsDisplay.MethodAccess(access, imported.Access.AssociationVersion)));
            }
            rows.Add(row);
        }
        SelectedObject = null;
        Objects.Clear();
        foreach (var row in rows) Objects.Add(row);
        ApplyObjectFilter();
    }

    private bool CanReadSelectedObject() =>
        !IsBusy && !IsReading && SelectedObject?.Attributes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanReadSelectedObject))]
    private Task ReadSelectedObjectAsync()
    {
        var obj = SelectedObject!;
        var attributes = obj.Attributes
            .OrderBy(attribute => obj.IsProfileGeneric &&
                attribute.Index == ProfileGenericMembers.Attributes.CaptureObjects ? 0 : 1)
            .ThenBy(attribute => attribute.Index)
            .ToArray();
        return ReadAttributesAsync(obj, attributes);
    }

    public Task ReadAttributeAsync(AttributeViewModel attribute)
    {
        var obj = SelectedObject;
        if (obj == null || !obj.Attributes.Contains(attribute) ||
            !CanReadSelectedObject())
            return Task.CompletedTask;
        return ReadAttributesAsync(obj, [attribute]);
    }

    public async Task WriteAttributeAsync(AttributeViewModel attribute)
    {
        var obj = SelectedObject;
        var meter = connection;
        if (obj is null || !obj.Attributes.Contains(attribute) || IsBusy || IsReading) return;
        string reason = meter?.WriteUnavailableReason(obj.ClassId, obj.LogicalName, attribute.Index)
            ?? "Подключитесь к счётчику для записи.";
        if (reason.Length != 0)
        {
            attribute.WriteStatus = reason;
            ReportError(reason);
            return;
        }

        DlmsValue value;
        try
        {
            if (attribute.WriteType is not { } type)
                throw new FormatException("Выберите тип записываемого значения.");
            value = DlmsValueEditor.Parse(type, attribute.WriteText, limits, attribute.WriteOctetFormat);
        }
        catch (FormatException error)
        {
            attribute.WriteStatus = error.Message;
            ReportError($"{obj.Obis}, атрибут {attribute.Index}: {error.Message}");
            return;
        }

        int version = selectionVersion;
        IsBusy = true;
        attribute.WriteStatus = "Запись…";
        Status = $"Запись {obj.Obis}: атрибут {attribute.Index}…";
        try
        {
            await meter!.WriteAsync(obj.ClassId, obj.LogicalName, attribute.Index, value, shutdown.Token);
            if (version != selectionVersion || meter != connection) return;
            attribute.ForgetReadValue();
            attribute.Value = "Запись подтверждена. Фактическое значение не считано.";
            InvalidateProfileAfterWrite(obj, attribute);
            attribute.WriteStatus = "Запись подтверждена счётчиком.";
            if (meter.CanRead(obj.LogicalName, attribute.Index))
            {
                try
                {
                    var actual = await meter.ReadAsync(obj.ClassId, obj.LogicalName,
                        attribute.Index, cancellationToken: shutdown.Token);
                    if (version != selectionVersion || meter != connection) return;
                    ApplyReadResult(obj, attribute, actual);
                    attribute.WriteStatus = "Запись подтверждена. Значение перечитано со счётчика.";
                }
                catch (Exception error)
                {
                    attribute.WriteStatus = $"Запись подтверждена, но перечитать значение не удалось: {error.Message}";
                    ReportError($"{obj.Obis}, атрибут {attribute.Index}: {attribute.WriteStatus}");
                    return;
                }
            }
            Status = $"{obj.Obis}, атрибут {attribute.Index}: {attribute.WriteStatus}";
        }
        catch (Exception error)
        {
            if (version != selectionVersion || meter != connection) return;
            if (error is not (DlmsOperationException or ArgumentException or InvalidOperationException or NotSupportedException or OperationCanceledException))
            {
                attribute.ForgetReadValue();
                attribute.Value = "Исход записи неизвестен. Перечитайте значение со счётчика.";
                InvalidateProfileAfterWrite(obj, attribute);
            }
            attribute.WriteStatus = error switch
            {
                DlmsOperationException remote => $"Счётчик отклонил запись: {remote.Error} (код {(int)remote.Error}). {remote.Message}",
                DlmsWriteUnconfirmedException unconfirmed =>
                    $"Запись не подтверждена: {unconfirmed.InnerException?.Message ?? unconfirmed.Message}. Проверьте значение чтением перед повторной записью.",
                OperationCanceledException => "Запись отменена до отправки запроса.",
                ArgumentException or InvalidOperationException or NotSupportedException => $"Запись не выполнена: {error.Message}",
                _ => $"Запись не подтверждена: {error.Message}. Проверьте значение чтением перед повторной записью."
            };
            ReportError($"{obj.Obis}, атрибут {attribute.Index}: {attribute.WriteStatus}");
        }
        finally
        {
            if (meter == connection && meter?.IsActive != true) IsConnected = false;
            IsBusy = false;
        }
    }

    private static void InvalidateProfileAfterWrite(MeterObjectViewModel obj, AttributeViewModel attribute)
    {
        if (!obj.IsProfileGeneric) return;
        obj.ProfileBuffer = null;
        obj.ProfileTable = null;
        obj.ProfileTableStatus = "После записи перечитайте buffer и capture_objects.";
        if (attribute.Index != ProfileGenericMembers.Attributes.CaptureObjects) return;
        obj.ProfileCaptureObjects = null;
        obj.ProfileCaptureColumns.Clear();
        obj.SelectedProfileCaptureColumn = null;
    }

    public async Task InvokeMethodAsync(MethodViewModel method)
    {
        var obj = SelectedObject;
        if (obj is null || !obj.Methods.Contains(method) || IsBusy || IsReading) return;
        var meter = IsConnected ? connection : null;
        string target = $"{obj.Obis}, метод {method.Index}";
        string reason = MethodInvokeUnavailableReason(obj, method, meter);
        if (meter is null || reason.Length != 0)
        {
            method.InvokeStatus = reason;
            ReportError($"{target}. {reason}");
            return;
        }

        DlmsValue? parameter = null;
        if (method.IncludeParameter)
        {
            try
            {
                if (method.ArgumentType is not { } type)
                    throw new FormatException("Выберите тип параметра.");
                parameter = DlmsValueEditor.Parse(type, method.ParameterText, limits);
            }
            catch (FormatException error)
            {
                method.InvokeStatus = error.Message;
                ReportError($"{target}. {error.Message}");
                return;
            }
        }

        IsBusy = true;
        method.InvokeStatus = "Вызов…";
        Status = $"Вызов {target}…";
        try
        {
            var result = await meter.InvokeAsync(obj.ClassId, obj.LogicalName, method.Index, parameter, shutdown.Token);
            InvalidateAfterInvoke(obj);
            method.InvokeStatus = result is null ? "Вызов подтверждён. Счётчик не вернул значение."
                : $"Вызов подтверждён. Возвращённое значение ({result.Type}):\n{DlmsDisplay.Value(result)}";
            Status = $"{target}. Вызов подтверждён счётчиком.";
        }
        catch (DlmsOperationException error)
        {
            string code = $"{error.Error} (код {(int)error.Error})";
            if (error.Response.Result.Error == CosemResultCode.Success)
            {
                InvalidateAfterInvoke(obj);
                method.InvokeStatus = $"Вызов подтверждён. Счётчик вернул ошибку доступа к результату: {code}.";
            }
            else
                method.InvokeStatus = $"Счётчик отклонил вызов: {code}.";
            ReportError($"{target}. {method.InvokeStatus}");
        }
        catch (Exception error)
        {
            // EMCL 0.1.1 faults the association if an attempted ACTION exchange fails.
            if (meter.IsActive && error is (ArgumentException or InvalidOperationException or
                NotSupportedException or OperationCanceledException))
                method.InvokeStatus = $"Вызов не выполнен: {error.Message}";
            else
            {
                InvalidateAfterInvoke(obj);
                method.InvokeStatus = $"Исход вызова не подтверждён: {error.Message}. Проверьте состояние счётчика перед повторным вызовом.";
            }
            ReportError($"{target}. {method.InvokeStatus}");
        }
        finally
        {
            if (meter == connection && !meter.IsActive) IsConnected = false;
            IsBusy = false;
        }
    }

    private static void InvalidateAfterInvoke(MeterObjectViewModel obj)
    {
        foreach (var attribute in obj.Attributes)
        {
            if (attribute.Index == CosemObject.LogicalNameAttributeIndex) continue;
            attribute.ForgetReadValue();
            attribute.Value = "Перечитайте значение после вызова метода.";
        }
        if (!obj.IsProfileGeneric) return;
        obj.ProfileBuffer = null;
        obj.ProfileCaptureObjects = null;
        obj.ProfileCaptureColumns.Clear();
        obj.SelectedProfileCaptureColumn = null;
        obj.ProfileTable = null;
        obj.ProfileTableStatus = "После вызова метода перечитайте buffer и capture_objects.";
    }

    public async Task ReadProfileAsync(MeterObjectViewModel obj)
    {
        if (!obj.IsProfileGeneric || obj != SelectedObject || IsBusy || IsReading) return;
        var meter = connection;
        if (meter?.IsActive != true)
        {
            IsConnected = false;
            ReportError("Нет активного соединения. Подключитесь к счётчику повторно.");
            return;
        }

        int version = selectionVersion;
        var bufferAttribute = obj.Attributes.FirstOrDefault(attribute =>
            attribute.Index == ProfileGenericMembers.Attributes.Buffer);
        obj.ProfileTable = null;
        obj.ProfileTableStatus = "Чтение профиля…";
        IsReading = true;
        try
        {
            if (obj.ProfileCaptureObjects is null)
            {
                var captures = obj.Attributes.FirstOrDefault(attribute =>
                    attribute.Index == ProfileGenericMembers.Attributes.CaptureObjects)
                    ?? throw new InvalidOperationException("В каталоге нет атрибута capture_objects.");
                Status = $"{obj.Obis}: чтение capture_objects…";
                if (!await ReadAttributeCoreAsync(obj, captures, meter, version))
                    throw new InvalidOperationException($"Не удалось прочитать capture_objects: {captures.Value}");
            }
            if (version != selectionVersion || meter != connection) return;

            SelectiveAccess? selection = null;
            switch (obj.SelectedProfileReadMode.Mode)
            {
                case ProfileReadMode.First:
                    selection = ProfileSelectiveAccessBuilder.Entries(1, ParsePositiveEntry(obj.ProfileCountText, "Количество записей"));
                    break;
                case ProfileReadMode.Last:
                {
                    uint requested = ParsePositiveEntry(obj.ProfileCountText, "Количество записей");
                    Status = $"{obj.Obis}: чтение entries_in_use…";
                    var entries = await meter.ReadAsync(obj.ClassId, obj.LogicalName,
                        ProfileGenericMembers.Attributes.EntriesInUse, cancellationToken: shutdown.Token);
                    if (version != selectionVersion || meter != connection) return;
                    var entriesAttribute = obj.Attributes.FirstOrDefault(attribute =>
                        attribute.Index == ProfileGenericMembers.Attributes.EntriesInUse);
                    if (entriesAttribute != null) ApplyReadResult(obj, entriesAttribute, entries);
                    uint total = checked((uint)entries.AsUInt64());
                    if (total == 0)
                    {
                        if (bufferAttribute != null)
                            ApplyReadResult(obj, bufferAttribute, DlmsValue.Array());
                        Status = $"{obj.Obis}: профиль пуст.";
                        return;
                    }
                    selection = ProfileSelectiveAccessBuilder.Entries(
                        total > requested ? total - requested + 1 : 1, total);
                    break;
                }
                case ProfileReadMode.Entries:
                    selection = ProfileSelectiveAccessBuilder.Entries(
                        ParsePositiveEntry(obj.ProfileFirstEntryText, "Первая запись"),
                        ParsePositiveEntry(obj.ProfileLastEntryText, "Последняя запись"));
                    break;
                case ProfileReadMode.Dates:
                {
                    var column = obj.SelectedProfileCaptureColumn
                        ?? throw new ArgumentException("Выберите столбец времени из capture_objects.");
                    selection = ProfileSelectiveAccessBuilder.Dates(column.Capture, column.WireType,
                        obj.ProfileFromDate, obj.ProfileToDate, obj.ProfileTimeZone);
                    break;
                }
            }

            Status = $"{obj.Obis}: чтение buffer…";
            if (!await ReadProfileBufferAsync(obj, bufferAttribute, meter, version, selection)) return;
            string summary = $"{obj.Obis}: {obj.ProfileTableStatus}";
            if (obj.ProfileBuffer is not null && obj.ProfileCaptureObjects is not null && obj.ProfileTable is null)
                ReportError(summary);
            else Status = summary;
        }
        catch (Exception error)
        {
            if (version == selectionVersion)
            {
                if (bufferAttribute != null)
                {
                    bufferAttribute.ForgetReadValue();
                    bufferAttribute.Value = $"Ошибка: {error.Message}";
                }
                obj.ProfileTableStatus = $"Ошибка чтения: {error.Message}";
                ReportError($"{obj.Obis}: {error.Message}");
            }
        }
        finally
        {
            IsReading = false;
            if (meter == connection && !meter.IsActive)
            {
                IsConnected = false;
                ReportError("Соединение потеряно. Подключитесь повторно.");
            }
        }
    }

    private async Task<bool> ReadProfileBufferAsync(MeterObjectViewModel obj, AttributeViewModel? attribute,
        MeterConnection meter, int version, SelectiveAccess? selection = null)
    {
        int progressVersion = ++profileReadVersion;
        ProfileReadProgress = default(ProfileBufferProgress);
        var progress = new Progress<ProfileBufferProgress>(value =>
        {
            if (progressVersion == profileReadVersion && version == selectionVersion && meter == connection)
                ProfileReadProgress = value;
        });
        try
        {
            var result = await meter.ReadProfileBufferAsync(obj.LogicalName, selection, progress, shutdown.Token);
            if (version != selectionVersion || meter != connection) return false;

            var buffer = new DlmsValue(DlmsType.Array, result.Rows);
            if (attribute != null)
            {
                if (result.Type == DlmsType.Array)
                    attribute.RememberReadValue(buffer);
                else
                    attribute.ShowStreamedCompactArray(result.Rows);
                attribute.Value = DlmsDisplay.Collection(result.Type, result.Rows);
                if (!attribute.HasDeclaredType)
                    attribute.Type = $"{result.Type} ({(byte)result.Type})";
            }
            obj.ProfileBuffer = buffer;
            UpdateProfileTable(obj);
            return true;
        }
        finally
        {
            ++profileReadVersion;
            ProfileReadProgress = null;
        }
    }

    private static uint ParsePositiveEntry(string text, string field)
    {
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value == 0)
            throw new ArgumentException($"{field}: укажите целое число от 1 до {uint.MaxValue}.");
        return value;
    }

    private async Task ReadAttributesAsync(MeterObjectViewModel obj, AttributeViewModel[] attributes)
    {
        var meter = connection;
        if (IsReading) return;
        if (meter?.IsActive != true)
        {
            IsConnected = false;
            ReportError("Нет активного соединения. Подключитесь к счётчику повторно.");
            return;
        }
        int version = selectionVersion;
        int readCount = 0;
        IsReading = true;
        try
        {
            foreach (var attribute in attributes)
            {
                if (version != selectionVersion || meter != connection || !meter.IsActive) break;
                if (obj.IsProfileGeneric && attribute.Index == ProfileGenericMembers.Attributes.Buffer &&
                    obj.ProfileCaptureObjects == null)
                {
                    var captures = obj.Attributes.FirstOrDefault(item =>
                        item.Index == ProfileGenericMembers.Attributes.CaptureObjects);
                    if (captures != null && captures != attribute)
                    {
                        Status = $"Чтение {obj.Obis}: capture_objects…";
                        await ReadAttributeCoreAsync(obj, captures, meter, version);
                    }
                }
                if (version != selectionVersion || !meter.IsActive) break;
                Status = $"Чтение {obj.Obis}: атрибут {attribute.Index}…";
                if (await ReadAttributeCoreAsync(obj, attribute, meter, version))
                    readCount++;
            }
        }
        finally
        {
            IsReading = false;
            if (meter == connection && !meter.IsActive)
            {
                IsConnected = false;
                ReportError("Соединение потеряно. Подключитесь повторно.");
            }
            else if (version == selectionVersion)
            {
                string summary = $"{obj.Obis}: считано атрибутов {readCount} из {attributes.Length}.";
                if (readCount == attributes.Length) Status = summary;
                else ReportError(summary);
            }
        }
    }

    private async Task<bool> ReadAttributeCoreAsync(MeterObjectViewModel obj,
        AttributeViewModel attribute, MeterConnection meter, int version)
    {
        if (attribute.Index == CosemObject.LogicalNameAttributeIndex)
        {
            attribute.Value = obj.Obis;
            if (!attribute.HasDeclaredType)
                attribute.Type = "OctetString (9)";
            return true;
        }
        if (obj.ClassId == CosemClassIds.AssociationLn &&
            attribute.Index == AssociationLnMembers.Attributes.ObjectList)
        {
            attribute.Value = $"Загружено {Objects.Count} объектов";
            return true;
        }
        try
        {
            if (obj.IsProfileGeneric && attribute.Index == ProfileGenericMembers.Attributes.Buffer)
                return await ReadProfileBufferAsync(obj, attribute, meter, version);

            var result = await meter.ReadAsync(obj.ClassId, obj.LogicalName, attribute.Index,
                cancellationToken: shutdown.Token);
            if (version != selectionVersion || meter != connection) return false;
            ApplyReadResult(obj, attribute, result);
            return true;
        }
        catch (Exception error)
        {
            if (version == selectionVersion && meter == connection)
            {
                attribute.ForgetReadValue();
                attribute.Value = $"Ошибка: {error.Message}";
            }
            return false;
        }
    }

    private void ApplyReadResult(MeterObjectViewModel obj, AttributeViewModel attribute, DlmsValue result)
    {
        attribute.RememberReadValue(result);
        attribute.Value = DlmsDisplay.Value(result);
        if (!attribute.HasDeclaredType)
            attribute.Type = $"{result.Type} ({(byte)result.Type})";
        if (!obj.IsProfileGeneric) return;
        if (attribute.Index == ProfileGenericMembers.Attributes.CaptureObjects)
        {
            obj.ProfileCaptureObjects = result;
            obj.ProfileBuffer = null;
            UpdateProfileCaptureColumns(obj, result);
            UpdateProfileTable(obj);
        }
        else if (attribute.Index == ProfileGenericMembers.Attributes.Buffer)
        {
            obj.ProfileBuffer = result;
            UpdateProfileTable(obj);
        }
    }

    private void UpdateProfileCaptureColumns(MeterObjectViewModel obj, DlmsValue captures)
    {
        obj.ProfileCaptureColumns.Clear();
        obj.SelectedProfileCaptureColumn = null;
        if (captures.Type != DlmsType.Array) return;
        foreach (var item in captures.Items)
        {
            CaptureObject capture;
            try { capture = CaptureObject.Parse(item); }
            catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException or ArgumentException)
            {
                continue;
            }
            var source = Objects.FirstOrDefault(candidate => candidate.ClassId == capture.ClassId &&
                candidate.LogicalName == capture.LogicalName);
            var attribute = source?.Attributes.FirstOrDefault(candidate => candidate.Index == capture.AttributeIndex);
            var type = attribute?.Type ?? "";
            bool clockTime = capture.ClassId == CosemClassIds.Clock && capture.AttributeIndex == 2;
            DlmsType wireType = type.StartsWith("DateTime", StringComparison.OrdinalIgnoreCase)
                ? DlmsType.DateTime : DlmsType.OctetString;
            if (!clockTime && !type.StartsWith("DateTime", StringComparison.OrdinalIgnoreCase) &&
                !type.StartsWith("OctetString", StringComparison.OrdinalIgnoreCase))
                continue;
            string name = attribute?.Name ?? $"Атрибут {capture.AttributeIndex}";
            var column = new ProfileCaptureColumnViewModel(capture, name, wireType);
            obj.ProfileCaptureColumns.Add(column);
            if (obj.SelectedProfileCaptureColumn is null || clockTime)
                obj.SelectedProfileCaptureColumn = column;
        }
    }

    private void UpdateProfileTable(MeterObjectViewModel obj)
    {
        if (obj.ProfileCaptureObjects == null || obj.ProfileBuffer == null)
        {
            obj.ProfileTable = null;
            obj.ProfileTableStatus = "Для таблицы нужны buffer и capture_objects.";
            return;
        }
        try
        {
            obj.ProfileTable = ProfileGenericTableBuilder.Build(
                obj.ProfileCaptureObjects, obj.ProfileBuffer, Objects);
            obj.ProfileTableStatus = $"Строк: {obj.ProfileTable.RowCount}";
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException)
        {
            obj.ProfileTable = null;
            obj.ProfileTableStatus = $"Не удалось построить таблицу: {error.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Inspector.Dispose();
        trace.Changed -= OnTraceChanged;
        shutdown.Cancel();
        try
        {
            if (connecting is { } pending) await pending.Task;
            ++selectionVersion;
            await CloseConnectionAsync();
        }
        finally
        {
            shutdown.Dispose();
        }
    }
}
