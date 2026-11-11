using System.Text.RegularExpressions;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EMCLDesktop.ViewModels;
using EMCLDesktop.Services;
using EMCL.Framing;
using EMCL.Transport.Streams;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
string[] Lines(DiagnosticTrace trace) => trace.Snapshot()
    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
string[] Tokens(DiagnosticTrace trace) => Lines(trace)
    .Select(line => Regex.Match(line, @"(?: HEX \d+B \+[0-9A-F]+: |^(?:TX|RX): )(.*)"))
    .Where(match => match.Success)
    .SelectMany(match => match.Groups[1].Value.Split(' ')).ToArray();
string[] Hex(byte[] bytes) => bytes.Select(b => b.ToString("X2")).ToArray();

// Include authentication-like, malformed and incomplete bytes: neither HEX mode filters them.
byte[] tx = [0x60, 9, 0xAC, 7, 0x80, 5, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5];
byte[] rx = [0xFF, 0xDB, 0xAA, 0x00, 0x7E];
var trace = new DiagnosticTrace();
trace.BeginConnection("HDLC");
trace.Add("Описание обмена");
trace.LogHdlcLinkEvent(new HdlcLinkEvent.ChecksumError("Invalid HDLC FCS."));
trace.LogHdlcLinkEvent(new HdlcLinkEvent.RecoveryPoll(3, 1));
trace.LogHdlcLinkEvent(new HdlcLinkEvent.Retransmission(3, 1));
trace.LogTcpBytes("TX", tx);
trace.Add("Получен ответ");
trace.LogTcpBytes("RX", rx);
Check(trace.DisplayFormat == TraceDisplayFormat.Verbal, "Default is not verbal");
Check(trace.Snapshot().Contains("Описание обмена"), "Verbal description missing");
Check(Lines(trace).Any(s => s.Contains("ошибки контрольной суммы") && s.Contains("Invalid HDLC FCS")) &&
      Lines(trace).Any(s => s.Contains("запрос RR после таймаута") && s.Contains("N(S)=3, попытка 1")) &&
      Lines(trace).Any(s => s.Contains("повтор I-кадра N(S)=3 по ответу RR, попытка 1")),
    "HDLC recovery events lost their reasons, frame sequence or attempt");
Check(!trace.Snapshot().Contains(" HEX "), "Verbal mode contains a byte dump");
Check(Tokens(trace).Length == 0, "Verbal mode contains raw bytes");

trace.DisplayFormat = TraceDisplayFormat.VerbalWithHex;
Check(trace.Snapshot().Contains("Описание обмена"), "Mixed mode lost descriptions");
Check(trace.Snapshot().Contains("TCP TX HEX 11B +000000:"), "Mixed metadata missing");
Check(Tokens(trace).SequenceEqual(Hex([.. tx, .. rx])), "Mixed mode changed bytes captured before switching");
Check(!trace.Snapshot().Contains("**"), "Mixed mode still masks bytes");
var mixed = Lines(trace);
Check(Array.FindIndex(mixed, s => s.Contains("Описание обмена")) < Array.FindIndex(mixed, s => s.Contains("TCP TX HEX")), "Mixed mode reordered messages");
Check(Array.FindIndex(mixed, s => s.Contains("TCP TX HEX")) < Array.FindIndex(mixed, s => s.Contains("Получен ответ")), "Mixed mode reordered bytes");

trace.DisplayFormat = TraceDisplayFormat.HexOnly;
Check(Lines(trace).SequenceEqual(new[] { "TX: " + string.Join(' ', Hex(tx)), "RX: " + string.Join(' ', Hex(rx)) }), "Pure HEX contains descriptions, metadata or altered bytes");
Check(Lines(trace).All(s => Regex.IsMatch(s, @"^(TX|RX): [0-9A-F]{2}( [0-9A-F]{2})*$")), "Pure HEX has extra text");
Check(Tokens(trace).SequenceEqual(Hex([.. tx, .. rx])), "HEX modes disagree on data");

trace.DisplayFormat = TraceDisplayFormat.Verbal;
Check(trace.Snapshot().Contains("Описание обмена") && Tokens(trace).Length == 0, "Switching back to verbal failed");
trace.BeginConnection("Wrapper");
trace.LogTcpBytes("TX", [1, 2]);
trace.DisplayFormat = TraceDisplayFormat.HexOnly;
Check(Tokens(trace).SequenceEqual(Hex([.. tx, .. rx, 1, 2])), "Reconnect discarded or changed history");

var lastCapture = trace.CapturesAfter(0)[^1];
bool clearNotified = false;
trace.Changed += () => clearNotified = trace.Snapshot() == "";
trace.Clear();
Check(clearNotified, "Clearing the trace did not notify the display");
Check(trace.DisplayFormat == TraceDisplayFormat.HexOnly && trace.Snapshot() == "",
    "Clearing the trace retained raw data or changed the format");
trace.DisplayFormat = TraceDisplayFormat.Verbal;
Check(trace.Snapshot() == "" && trace.CapturesAfter(0).Count == 0, "Clearing the trace retained old entries");
trace.LogTcpBytes("RX", rx);
var nextCapture = trace.CapturesAfter(lastCapture.Id).Single();
Check(nextCapture.SessionId == lastCapture.SessionId && nextCapture.Framing == lastCapture.Framing,
    "Clearing the trace interrupted capture or changed its connection metadata");

// Chunk boundaries and byte order remain exact in both formats, including long chunks.
byte[] large = Enumerable.Range(0, 150).Select(n => (byte)n).ToArray();
var chunks = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
chunks.LogTcpBytes("TX", large);
Check(Lines(chunks).Length == 3, "Long chunk did not wrap at 64 bytes");
Check(Lines(chunks).All(s => s.StartsWith("TX: ")), "Wrapped line lost direction");
Check(Tokens(chunks).SequenceEqual(Hex(large)), "Wrapping changed byte order");
chunks.DisplayFormat = TraceDisplayFormat.VerbalWithHex;
Check(chunks.Snapshot().Contains("+000040:") && chunks.Snapshot().Contains("+000080:"), "Mixed offsets incorrect");
Check(Tokens(chunks).SequenceEqual(Hex(large)), "Mixed wrapping changed bytes");
var fragmented = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
foreach (byte value in tx) fragmented.LogTcpBytes("TX", [value]);
Check(Tokens(fragmented).SequenceEqual(Hex(tx)), "Byte-sized fragments were lost or masked");
fragmented.LogTcpBytes("RX", []);
Check(Lines(fragmented).Length == tx.Length, "Empty read created a HEX line");

// Buffers must be owned by the capture and old history must survive past the former cap.
var ownership = new DiagnosticTrace();
var buffer = tx.ToArray();
ownership.LogTcpBytes("TX", buffer);
Array.Fill(buffer, (byte)0);
ownership.DisplayFormat = TraceDisplayFormat.HexOnly;
Check(Tokens(ownership).SequenceEqual(Hex(tx)), "Buffer reuse changed captured data");
ownership.Add("BEGIN-RETENTION");
ownership.Add(new string('x', 2_100_000));
ownership.DisplayFormat = TraceDisplayFormat.Verbal;
Check(ownership.Snapshot().Contains("BEGIN-RETENTION"), "Old history was truncated");
var empty = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
empty.Add("No bytes");
Check(empty.Snapshot() == "", "Pure HEX contains a message without data");

// The transport keeps failed TX attempts and only the received portion of RX buffers.
var ioTrace = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.HexOnly };
var fake = new FakeTransport(rx);
await using (var transport = new IdleTimeoutByteTransport(fake, TimeSpan.FromSeconds(1), ioTrace))
{
    var received = new byte[rx.Length + 10];
    int count = await transport.ReadAsync(received);
    Check(count == rx.Length, "Read count changed");
    Array.Fill(received, (byte)0);
    Check(Tokens(ioTrace).SequenceEqual(Hex(rx)), "Read included unused or stale buffer bytes");
    fake.FailWrite = true;
    try { await transport.WriteAsync(tx); throw new Exception("Expected write failure"); }
    catch (IOException) { }
    Check(Tokens(ioTrace).SequenceEqual(Hex([.. rx, .. tx])), "Failed write attempt was lost");
    Check(Lines(ioTrace).All(s => s.StartsWith("TX: ") || s.StartsWith("RX: ")), "Pure HEX includes transport descriptions");
    ioTrace.DisplayFormat = TraceDisplayFormat.VerbalWithHex;
    Check(ioTrace.Snapshot().Contains("попытка передачи"), "Failed write not marked as an attempt");
    Check(!ioTrace.Snapshot().Contains("запись"), "Failed write marked complete");
}
// Connection selection and JSON round trips must work without opening real hardware.
var vm = new MainViewModel();
Check(vm.Host == "" && vm.TcpPort == "", "New TCP settings contain a prefilled endpoint");
Check(!vm.HasStatusError, "Initial status must not show error-copy action");
vm.ReportError("Счётчик отклонил запрос");
Check(vm.HasStatusError, "Device refusal without an error prefix must expose error-copy action");
vm.Status = "Чтение дерева объектов…";
Check(!vm.HasStatusError, "Progress status retained error-copy action");
vm.ReportError("Повторяемое сообщение");
vm.Status = vm.Status;
Check(!vm.HasStatusError, "Same-text informational status retained error severity");
vm.ReportError("Ошибка подключения для проверки видимости");
vm.TraceText = "Устаревший снимок";
vm.SelectedTabIndex = 2;
Check(!vm.IsObjectsTab && !vm.Inspector.IsActive, "Trace tab incorrectly activates meter status or packet inspector");
Check(vm.TraceText == vm.GetTraceSnapshot(), "Trace tab did not refresh history when opened");
vm.SelectedTabIndex = 0;
Check(vm.IsObjectsTab && vm.HasStatusError && vm.Status == "Ошибка подключения для проверки видимости", "Meter tab lost its connection error");
var changed = new List<string?>();
vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
Check(vm.ConnectionState == "Не подключено" && vm.ConnectionActionText == "Подключиться", "Disconnected status/action labels");
vm.IsConnecting = true;
Check(vm.ConnectionState == "Подключение…" && vm.IsConnectionChanging && changed.Contains(nameof(vm.IsConnectionChanging)),
    "Connecting state or notification missing");
vm.IsConnecting = false;
vm.IsConnected = true;
Check(vm.ConnectionState == "Подключено" && vm.ConnectionActionText == "Отключиться", "Connected status/action labels");
vm.IsBusy = true;
Check(vm.ConnectionState == "Подключено" && !vm.IsConnectionChanging && !vm.ToggleConnectionCommand.CanExecute(null),
    "A busy meter operation must not look like connection establishment");
vm.IsBusy = false;
vm.IsDisconnecting = true;
vm.IsConnected = false;
Check(vm.ConnectionState == "Отключение…" && vm.ConnectionActionText == "Отключиться" && vm.IsConnectionChanging,
    "Disconnecting state prematurely offers reconnection");
vm.IsDisconnecting = false;
string validPort = vm.TcpPort;
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
await vm.ConnectCommand.ExecuteAsync(null);
vm.PropertyChanged -= ObserveConnectionState;
vm.TcpPort = validPort;
Check(connectionStates.Contains("Подключение…") && connectingButtonDisabled && !vm.IsConnecting && !vm.IsBusy &&
      vm.ConnectionState == "Не подключено", "Failed connection left a transition flag or an enabled connection action");
Check(vm.HasStatusError, "Failed connection did not expose error-copy action");
Check(vm.IsTcp && !vm.IsSerial, "Default transport changed");
vm.Transport = "Serial";
Check(vm.IsSerial && !vm.IsTcp && changed.Contains(nameof(vm.IsSerial)) && changed.Contains(nameof(vm.IsTcp)),
    "Transport visibility notifications missing");
vm.SerialPortName = "/dev/test-serial";
vm.SerialBaudRate = "19200";
vm.SerialDataBits = 7;
vm.SerialParity = Parity.Even;
vm.SerialStopBits = StopBits.Two;
vm.SerialHandshake = Handshake.XOnXOff;
vm.SerialDtrEnable = true;
vm.SerialRtsEnable = true;
vm.Host = "";
vm.TcpPort = "unused";
vm.HdlcInformationFrameRetryCount = 1;
vm.HdlcAcknowledgementTimeoutSeconds = "0.75";
vm.Authentication = "High";
Check(!vm.UseFixedInvokeC1 && vm.CanUseFixedInvokeC1, "The fixed 0xC1 mode should be opt-in for High authentication");
Check(!vm.UseHighPriority && !vm.IgnoreInvokeIdValidation, "High priority and Invoke ID compatibility should be opt-in");
vm.UseHighPriority = true;
Check(!vm.CanUseFixedInvokeC1, "High priority should use automatic Invoke IDs");
vm.IgnoreInvokeIdValidation = true;
await using var serialJson = new MemoryStream();
await vm.SaveConfigurationAsync(serialJson);
Check(!vm.HasStatusError, "Successful save retained stale error-copy action");
var savedSerial = JsonNode.Parse(serialJson.ToArray())!;
var serialSettings = savedSerial["channel"]!["connection"]!;
Check(serialSettings["kind"]!.GetValue<string>() == "Serial" && serialSettings["host"] is null &&
      serialSettings["tcpPort"] is null, "Serial JSON contains TCP fields");
Check(serialSettings["baudRate"]!.GetValue<int>() == 19200, "Serial baud rate lost");
var savedHdlc = savedSerial["channel"]!["framing"]!;
Check(savedHdlc["informationFrameRetryCount"]!.GetValue<int>() == 1 &&
      savedHdlc["acknowledgementTimeoutSeconds"]!.GetValue<double>() == 0.75,
    "HDLC recovery settings missing from JSON");
var restored = new MainViewModel();
serialJson.Position = 0;
await restored.LoadConfigurationAsync(serialJson);
Check(restored.IsSerial && restored.SerialPortName == vm.SerialPortName && restored.SerialBaudRate == "19200" &&
      restored.SerialDataBits == 7 && restored.SerialParity == Parity.Even && restored.SerialStopBits == StopBits.Two &&
      restored.SerialHandshake == Handshake.XOnXOff && restored.SerialDtrEnable && restored.SerialRtsEnable,
    "Serial JSON round trip lost settings: " + restored.Status);
Check(restored.HdlcInformationFrameRetryCount == 1 && restored.HdlcAcknowledgementTimeoutSeconds == "0.75",
    "HDLC recovery settings did not round trip");
Check(!restored.UseFixedInvokeC1 && restored.Authentication == "High", "Disabled fixed 0xC1 setting did not round trip");
Check(restored.UseHighPriority && restored.IgnoreInvokeIdValidation,
    "High request priority or Invoke ID compatibility setting did not round trip");
var savedAssociation = savedSerial["association"]!.AsObject();
savedAssociation["useFixedInvokeC1"] = true;
await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(savedSerial.ToJsonString())));
Check(restored.UseFixedInvokeC1 && restored.UseHighPriority && !restored.CanUseFixedInvokeC1,
    "High priority should take precedence over a saved fixed 0xC1 setting");
restored.UseHighPriority = false;
Check(restored.CanUseFixedInvokeC1, "Fixed 0xC1 should be available for High authentication with normal priority");
restored.Authentication = "Low";
Check(!restored.CanUseFixedInvokeC1, "Fixed 0xC1 should only be available for High authentication");
savedAssociation.Remove("useFixedInvokeC1");
savedAssociation.Remove("useHighPriority");
savedAssociation.Remove("ignoreInvokeIdValidation");
await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(savedSerial.ToJsonString())));
Check(!restored.UseFixedInvokeC1 && !restored.UseHighPriority && !restored.IgnoreInvokeIdValidation,
    "An older profile did not restore the default DLMS settings");
vm.IdleTimeoutSeconds = "2.25";
try { await vm.SaveConfigurationAsync(new MemoryStream()); throw new Exception("Too short an idle timeout was accepted"); }
catch (ArgumentException) { checks++; }
vm.IdleTimeoutSeconds = "120";
restored.SerialHandshake = Handshake.RequestToSend;
Check(!restored.CanSetSerialRts && !restored.SerialRtsEnable, "Hardware handshake did not take over RTS");
restored.SerialHandshake = Handshake.None;
Check(restored.CanSetSerialRts, "Manual RTS not restored");

foreach (var invalid in new[] { "0", "-1", "bad" })
{
    vm.SerialBaudRate = invalid;
    try { await vm.SaveConfigurationAsync(new MemoryStream()); throw new Exception("Invalid baud rate accepted"); }
    catch (ArgumentException) { checks++; }
}
vm.SerialBaudRate = "9600";
vm.SerialPortName = " ";
try { await vm.SaveConfigurationAsync(new MemoryStream()); throw new Exception("Empty serial port accepted"); }
catch (ArgumentException) { checks++; }

vm.Transport = "TCP";
vm.Host = "127.0.0.1";
vm.TcpPort = "4059";
vm.Framing = "Wrapper";
vm.ServerPhysicalAddress = "unused";
vm.HdlcAcknowledgementTimeoutSeconds = "unused";
await using var tcpJson = new MemoryStream();
await vm.SaveConfigurationAsync(tcpJson);
var savedTcp = JsonNode.Parse(tcpJson.ToArray())!;
var tcpSettings = savedTcp["channel"]!["connection"]!.AsObject();
Check(tcpSettings["kind"]!.GetValue<string>() == "Tcp" && tcpSettings["portName"] is null,
    "TCP JSON contains Serial settings");
var savedWrapper = savedTcp["channel"]!["framing"]!;
Check(savedWrapper["informationFrameRetryCount"] is null && savedWrapper["acknowledgementTimeoutSeconds"] is null,
    "Wrapper JSON contains HDLC recovery settings");
tcpSettings.Remove("kind");
await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(savedTcp.ToJsonString())));
Check(restored.IsTcp && restored.Host == "127.0.0.1" && restored.TcpPort == "4059" && restored.Framing == "Wrapper",
    "TCP configuration without kind did not load: " + restored.Status);
var legacy = new JsonObject
{
    ["connection"] = new JsonObject { ["host"] = "localhost", ["tcpPort"] = 93, ["transport"] = "HdlcTcp" },
    ["association"] = savedTcp["association"]!.DeepClone()
};
await restored.LoadConfigurationAsync(new MemoryStream(Encoding.UTF8.GetBytes(legacy.ToJsonString())));
Check(restored.IsTcp && restored.Host == "localhost" && restored.TcpPort == "93" && restored.IsHdlc,
    "Legacy TCP configuration did not load: " + restored.Status);
Check(restored.HdlcInformationFrameRetryCount == 0 && restored.HdlcAcknowledgementTimeoutSeconds == "1",
    "Legacy configuration did not receive safe HDLC recovery defaults");

// Factory selection validates only the active endpoint and forwards line settings unchanged.
var options = new MeterConnectionOptions("", 0, "HDLC", 16, 1, null, false, 1, "0.0.40.0.0.255",
    EMCL.Client.Association.ClientAuthentication.None, "", "UTF-8", 10, 30, 20, 65535,
    EMCL.Protocol.Apdu.DlmsConformance.Get, false, new EMCL.Core.Codecs.ProtocolLimits(),
    "Serial", new SerialPortSettings("/dev/test-serial", 19200, 7, Parity.Even, StopBits.Two));
Check(options.CreateTransportFactory() is SerialByteTransportFactory serialFactory &&
      serialFactory.Settings == options.Serial && serialFactory.Timeout == TimeSpan.FromSeconds(10),
    "Serial factory did not receive settings");
Check((options with { Transport = "TCP", Host = "localhost", TcpPort = 4059 }).CreateTransportFactory()
    is TcpByteTransportFactory, "TCP factory selection failed");

var serialTrace = new DiagnosticTrace { DisplayFormat = TraceDisplayFormat.VerbalWithHex };
await using (var transport = new IdleTimeoutByteTransport(new FakeTransport(rx), TimeSpan.FromSeconds(1), serialTrace, "Serial"))
{
    await transport.ReadAsync(new byte[rx.Length]);
    await transport.WriteAsync(tx);
}
serialTrace.LogTcpBytes("TX", [1]);
Check(serialTrace.Snapshot().Contains("Serial RX HEX") && serialTrace.Snapshot().Contains("Serial TX HEX") &&
      serialTrace.Snapshot().Contains("TCP TX HEX"), "Mixed transport history is mislabeled");
Check(Tokens(serialTrace).SequenceEqual(Hex([.. rx, .. tx, 1])), "Serial tracing changed bytes");
Console.WriteLine($"Passed {checks} trace and connection checks.");
Console.WriteLine($"Passed {await PacketInspectionChecks.RunAsync()} packet inspector checks.");
Console.WriteLine($"Passed {await AttributeWriteChecks.RunAsync()} attribute write checks.");
Console.WriteLine($"Passed {ProfileTableChecks.Run()} profile table checks.");
Console.WriteLine($"Passed {await CatalogTypeChecks.RunAsync()} catalog type checks.");
Console.WriteLine($"Passed {await CancellationChecks.RunAsync()} cancellation checks.");
Console.WriteLine($"Passed {await HdlcSizingChecks.RunAsync()} HDLC size checks.");

sealed class FakeTransport(byte[] received) : IByteTransport
{
    public bool FailWrite { get; set; }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        received.CopyTo(buffer);
        return ValueTask.FromResult(received.Length);
    }
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (FailWrite) throw new IOException("Simulated partial write failure");
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
