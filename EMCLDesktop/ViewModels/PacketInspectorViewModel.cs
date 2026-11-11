using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EMCLDesktop.Services;
using EMCLDesktop.Services.PacketInspection;

namespace EMCLDesktop.ViewModels;

public sealed partial class PacketItemViewModel(InspectedPacket packet) : ObservableObject
{
    public InspectedPacket Packet { get; private set; } = packet;
    public string Summary => Packet.Summary;
    public string Details => Packet.Details;
    public bool HasError => Packet.HasError;
    public bool IsIncomplete => Packet.IsIncomplete;
    public void Update(InspectedPacket value)
    {
        Packet = value;
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsIncomplete));
    }
}

public sealed partial class InspectorFieldViewModel : ObservableObject
{
    public InspectionField Field { get; }
    public InspectorFieldViewModel? Parent { get; }
    public string Label => Field.Label;
    public IReadOnlyList<InspectorFieldViewModel> Children { get; }
    [ObservableProperty] public partial bool IsHighlighted { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    public InspectorFieldViewModel(InspectionField field, InspectorFieldViewModel? parent = null)
    {
        Field = field; Parent = parent;
        IsExpanded = parent is null;
        Children = field.Children.Select(child => new InspectorFieldViewModel(child, this)).ToArray();
    }
    public IEnumerable<InspectorFieldViewModel> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var field in child.Flatten()) yield return field;
    }
}

public sealed partial class InspectorByteViewModel(int offset, byte value, InspectorFieldViewModel? owner) : ObservableObject
{
    public int Offset { get; } = offset;
    public string Hex { get; } = value.ToString("X2");
    public InspectorFieldViewModel? Owner { get; } = owner;
    public string Description => $"+0x{Offset:X6} ({Offset}) · {Hex}\n{Owner?.Label ?? "Поле не определено"}";
    [ObservableProperty] public partial bool IsHighlighted { get; set; }
}

public sealed record InspectorByteRow(string Offset, IReadOnlyList<InspectorByteViewModel> Bytes);
public sealed record RelatedPacketViewModel(long Id, string Title);

public sealed partial class PacketInspectorViewModel : ObservableObject, IDisposable
{
    private const int BytesPerPage = 256;
    private readonly DiagnosticTrace trace;
    private PacketAnalyzer liveAnalyzer = new();
    private readonly ObservableCollection<PacketItemViewModel> livePackets = [];
    private readonly ObservableCollection<PacketItemViewModel> textPackets = [];
    private readonly DispatcherTimer timer;
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken stopToken;
    private readonly Dictionary<int, (long Id, InspectionField? Field, int Page)> selections = [];
    private IReadOnlyList<InspectorFieldViewModel> flatFields = [];
    private InspectorFieldViewModel?[] byteOwners = [];
    private InspectorFieldViewModel? hoveredField;
    private int dirty = 1, refreshing, liveSession = -1, bytePage, textVersion;
    private long lastCapture;
    private long liveClearedThroughCaptureId;
    private bool disposed, liveFaulted;
    private string liveStatus = "Данные появятся при обмене с прибором.";
    private string textStatus = "Вставьте лог с HEX-пакетами и нажмите «Разобрать».";

    [ObservableProperty] public partial int SourceIndex { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsAnalyzing { get; set; }
    [ObservableProperty] public partial bool IsInputExpanded { get; set; } = true;
    [ObservableProperty] public partial string InputText { get; set; } = "";
    [ObservableProperty] public partial string AnalysisStatus { get; set; } = "Данные появятся при обмене с прибором.";
    [ObservableProperty] public partial ObservableCollection<PacketItemViewModel> Packets { get; set; }
    [ObservableProperty] public partial PacketItemViewModel? SelectedPacket { get; set; }
    [ObservableProperty] public partial InspectorFieldViewModel? SelectedField { get; set; }
    public ObservableCollection<InspectorFieldViewModel> Fields { get; } = [];
    public ObservableCollection<InspectorByteRow> ByteRows { get; } = [];
    public ObservableCollection<RelatedPacketViewModel> RelatedPackets { get; } = [];
    public bool IsLive => SourceIndex == 0;
    public bool IsText => SourceIndex == 1;
    public bool HasPacket => SelectedPacket is not null;
    public bool HasPackets => Packets.Count > 0;
    public string PacketTitle => SelectedPacket?.Summary ?? "Выберите пакет";
    public string PacketOrigin => SelectedPacket?.Packet.Origin ?? "";
    public string PacketSource => SelectedPacket?.Packet.SourceText is { } text && text.Length > 65536
        ? text[..65536] + "\n… отображение исходного текста сокращено" : SelectedPacket?.Packet.SourceText ?? "";
    public InspectorFieldViewModel? ActiveField => hoveredField ?? SelectedField;
    public string FieldName => ActiveField?.Field.Name ?? "Наведите на поле или байт";
    public string FieldValue => ActiveField?.Field.Value ?? "";
    public string FieldRanges => ActiveField is { } active
        ? active.Field.Ranges.Count == 0 ? "У поля нет байтов в этом пакете"
            : string.Join(", ", active.Field.Ranges.Take(24).Select(r => $"+0x{r.Offset:X6}…+0x{r.Offset + r.Length - 1:X6}")) +
              (active.Field.Ranges.Count > 24 ? ", …" : "") + $" · {active.Field.ByteCount} байт"
        : "";
    public string BytePageText => SelectedPacket is { } selected
        ? $"Байты {bytePage * BytesPerPage}–{Math.Min(selected.Packet.Bytes.Length, (bytePage + 1) * BytesPerPage) - 1} из {selected.Packet.Bytes.Length} · смещения от 0"
        : "";
    public bool CanPreviousPage => bytePage > 0;
    public bool CanNextPage => SelectedPacket is { } selected && (bytePage + 1) * BytesPerPage < selected.Packet.Bytes.Length;

    public PacketInspectorViewModel(DiagnosticTrace trace)
    {
        this.trace = trace;
        stopToken = shutdown.Token;
        Packets = livePackets;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += OnTimerTick;
        trace.Changed += OnTraceChanged;
    }

    private void OnTraceChanged() => Interlocked.Exchange(ref dirty, 1);
    private async void OnTimerTick(object? sender, EventArgs args) => await RefreshLiveAsync();

    partial void OnIsActiveChanged(bool value)
    {
        if (value) { timer.Start(); _ = RefreshLiveAsync(); }
        else timer.Stop();
    }

    partial void OnSourceIndexChanging(int value)
    {
        if (SelectedPacket is { } selected)
            selections[SourceIndex] = (selected.Packet.Id, SelectedField?.Field, bytePage);
    }
    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLive)); OnPropertyChanged(nameof(IsText));
        Packets = IsLive ? livePackets : textPackets;
        AnalysisStatus = IsLive ? liveStatus : textStatus;
        SelectedPacket = selections.TryGetValue(value, out var selection)
            ? Packets.FirstOrDefault(p => p.Packet.Id == selection.Id) : Packets.FirstOrDefault();
        if (selection.Field is { } field)
        {
            SelectedField = FindField(field) ?? SelectedField;
            bytePage = Math.Clamp(selection.Page, 0, Math.Max(0, ((SelectedPacket?.Packet.Bytes.Length ?? 0) - 1) / BytesPerPage));
            BuildBytePage();
        }
        OnPropertyChanged(nameof(HasPackets));
        if (IsLive && IsActive) _ = RefreshLiveAsync();
    }
    partial void OnInputTextChanged(string value)
    {
        textVersion++;
        textStatus = "Текст изменён. Нажмите «Разобрать», чтобы обновить результат.";
        if (IsText) AnalysisStatus = textStatus;
    }

    [RelayCommand] private void LiveSource() => SourceIndex = 0;
    [RelayCommand] private void TextSource() => SourceIndex = 1;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AnalyzeTextAsync(CancellationToken cancellationToken)
    {
        using var analysis = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);
        string input = InputText;
        int version = textVersion;
        IsAnalyzing = true;
        textStatus = "Разбор лога…";
        if (IsText) AnalysisStatus = textStatus;
        try
        {
            var result = await Task.Run(() =>
            {
                var packets = PacketAnalyzer.AnalyzeText(input, out int ignored, analysis.Token);
                return (Packets: packets, Ignored: ignored);
            }, analysis.Token);
            analysis.Token.ThrowIfCancellationRequested();
            if (disposed || version != textVersion) return;
            textPackets.Clear();
            selections.Remove(1);
            Reconcile(textPackets, result.Packets);
            if (result.Packets.Count > 0) IsInputExpanded = false;
            textStatus = result.Packets.Count == 0 ? "HEX-пакеты не найдены. Поддерживаются HDLC, Wrapper и DLMS APDU."
                : $"Пакетов: {result.Packets.Count}; ошибок: {result.Packets.Count(p => p.HasError)}; пропущено текстовых строк: {result.Ignored}.";
            if (IsText)
            {
                SelectedPacket = textPackets.FirstOrDefault();
                AnalysisStatus = textStatus;
                OnPropertyChanged(nameof(HasPackets));
            }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (OperationCanceledException) when (analysis.IsCancellationRequested)
        {
            textStatus = "Разбор лога отменён.";
            if (IsText) AnalysisStatus = textStatus;
        }
        catch (Exception error)
        {
            textStatus = $"Не удалось разобрать лог: {error.Message}";
            if (IsText && !disposed) AnalysisStatus = textStatus;
        }
        finally { if (!disposed) IsAnalyzing = false; }
    }

    public async Task RefreshLiveAsync()
    {
        if (disposed || !IsLive || !IsActive || Interlocked.Exchange(ref refreshing, 1) != 0) return;
        try
        {
            if (Interlocked.Exchange(ref dirty, 0) == 0) return;
            long clearedThroughCaptureId = trace.ClearedThroughCaptureId;
            int currentSession = trace.CurrentSessionId;
            var captures = trace.CapturesAfter(lastCapture);
            int session = Math.Max(currentSession, captures.LastOrDefault()?.SessionId ?? 0);
            bool reset = liveSession != session || liveClearedThroughCaptureId != clearedThroughCaptureId;
            if (reset)
            {
                liveAnalyzer = new();
                liveSession = session;
                liveClearedThroughCaptureId = clearedThroughCaptureId;
                liveFaulted = false;
            }
            if (liveFaulted) return;
            if (captures.Count == 0)
            {
                if (reset)
                {
                    livePackets.Clear(); selections.Remove(0); SelectedPacket = null;
                    liveStatus = "Данные текущего подключения появятся при обмене с прибором.";
                    AnalysisStatus = liveStatus; OnPropertyChanged(nameof(HasPackets));
                }
                return;
            }
            var snapshot = await Task.Run(() =>
            {
                foreach (var capture in captures.Where(c => c.SessionId == session))
                {
                    stopToken.ThrowIfCancellationRequested();
                    liveAnalyzer.Feed(new(capture.Bytes.ToArray(), capture.Direction, capture.Framing,
                        capture.Timestamp.ToString("HH:mm:ss.fff"),
                        $"Подключение {capture.SessionId} · {capture.Transport} · фрагмент {capture.Id}",
                        $"{capture.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {capture.Transport} {capture.Direction}: {Convert.ToHexString(capture.Bytes.Span)}",
                        capture.SessionId));
                }
                return liveAnalyzer.Snapshot();
            }, stopToken);
            if (disposed || trace.ClearedThroughCaptureId != clearedThroughCaptureId) return;
            lastCapture = captures[^1].Id;
            if (reset) { livePackets.Clear(); selections.Remove(0); }
            Reconcile(livePackets, snapshot);
            liveStatus = $"Текущее подключение · пакетов: {snapshot.Count} · обновляется при обмене. Данные остаются после отключения.";
            if (IsLive)
            {
                if (reset || SelectedPacket is null) SelectedPacket = livePackets.FirstOrDefault();
                AnalysisStatus = liveStatus;
                OnPropertyChanged(nameof(HasPackets));
            }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error)
        {
            liveFaulted = true;
            liveStatus = $"Анализ приостановлен: {error.Message}";
            if (IsLive && !disposed) AnalysisStatus = liveStatus;
        }
        finally { Interlocked.Exchange(ref refreshing, 0); }
    }

    private void Reconcile(ObservableCollection<PacketItemViewModel> collection, IReadOnlyList<InspectedPacket> snapshot)
    {
        for (int i = 0; i < snapshot.Count; i++)
        {
            if (i >= collection.Count) collection.Add(new(snapshot[i]));
            else if (!ReferenceEquals(collection[i].Packet, snapshot[i]))
            {
                collection[i].Update(snapshot[i]);
                if (ReferenceEquals(SelectedPacket, collection[i])) PopulateSelected();
            }
        }
    }

    partial void OnSelectedPacketChanged(PacketItemViewModel? value)
    {
        bytePage = 0;
        SelectedField = null;
        PopulateSelected();
    }

    private void PopulateSelected()
    {
        var previous = SelectedField?.Field;
        hoveredField = null;
        Fields.Clear(); RelatedPackets.Clear();
        if (SelectedPacket is { } selected)
        {
            foreach (var field in selected.Packet.Fields) Fields.Add(new(field));
            // Correlated OBIS and scaled values may not be present in the response's own wire fields.
            // Keep that information in the structure without inventing byte ranges for request metadata.
            var wireFields = Fields.SelectMany(f => f.Flatten()).ToArray();
            var context = new List<InspectionField>();
            if (selected.Packet.Target is { } target && !wireFields.Any(f => f.Field.Name == "OBIS / logical name"))
                context.Add(new("OBIS из запроса", target.Description, [], []));
            if (selected.Packet.DataValue is { } value && selected.Packet.ValueText != DlmsDisplay.Value(value))
            {
                var encodedValue = wireFields.FirstOrDefault(f => f.Field.Name is "Полученное значение" or "Собранное значение");
                context.Add(new("Значение с scaler/unit", selected.Packet.ValueText, encodedValue?.Field.Ranges ?? [], []));
            }
            if (context.Count > 0) Fields.Add(new(new("Контекст обмена", "", [], context)) { IsExpanded = true });
            if (!string.IsNullOrEmpty(selected.Packet.Diagnostic))
                Fields.Add(new(new("Диагностика", selected.Packet.Diagnostic, [], [])));
            foreach (long id in selected.Packet.RelatedPacketIds)
                RelatedPackets.Add(new(id, $"Перейти к № {id}"));
        }
        flatFields = Fields.SelectMany(f => f.Flatten()).ToArray();
        byteOwners = new InspectorFieldViewModel?[SelectedPacket?.Packet.Bytes.Length ?? 0];
        // Later, smaller fields own their bytes; no search through a large tree is needed on hover.
        foreach (var field in flatFields)
        {
            foreach (var range in field.Field.Ranges)
                for (int offset = Math.Max(0, range.Offset); offset < Math.Min(byteOwners.Length, range.Offset + range.Length); offset++)
                    if (byteOwners[offset] is not { } owner || field.Field.ByteCount <= owner.Field.ByteCount)
                        byteOwners[offset] = field;
        }
        SelectedField = previous is null ? null : FindField(previous);
        SelectedField ??= flatFields.FirstOrDefault(f => f.Field.Name == "OBIS / logical name") ??
                          flatFields.FirstOrDefault(f => f.Field.Name is "Полученное значение" or "Собранное значение") ?? flatFields.FirstOrDefault();
        BuildBytePage();
        foreach (string name in new[] { nameof(HasPacket), nameof(PacketTitle), nameof(PacketOrigin), nameof(PacketSource) }) OnPropertyChanged(name);
        UpdateHighlight();
    }

    private InspectorFieldViewModel? FindField(InspectionField model) => flatFields.FirstOrDefault(f =>
        f.Field.Name == model.Name && f.Field.Ranges.SequenceEqual(model.Ranges));

    partial void OnSelectedFieldChanged(InspectorFieldViewModel? value)
    {
        hoveredField = null;
        for (var parent = value?.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        if (value?.Field.Ranges.FirstOrDefault() is { Length: > 0 } range && range.Offset / BytesPerPage != bytePage)
        {
            bytePage = range.Offset / BytesPerPage;
            BuildBytePage();
        }
        UpdateHighlight();
    }

    public void HoverField(InspectorFieldViewModel? field)
    {
        if (ReferenceEquals(hoveredField, field)) return;
        hoveredField = field;
        if (field is not null && field.Field.Ranges.Count > 0 && !field.Field.Ranges.Any(r =>
                r.Offset < (bytePage + 1) * BytesPerPage && r.Offset + r.Length > bytePage * BytesPerPage))
        {
            bytePage = field.Field.Ranges[0].Offset / BytesPerPage;
            BuildBytePage();
        }
        UpdateHighlight();
    }
    public void HoverByte(InspectorByteViewModel? value)
    {
        if (value?.Owner is { } field)
            for (var parent = field.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        HoverField(value?.Owner);
    }
    public void SelectByte(InspectorByteViewModel value)
    {
        hoveredField = null;
        SelectedField = value.Owner;
        UpdateHighlight();
    }

    private void UpdateHighlight()
    {
        var active = ActiveField?.Field;
        foreach (var field in flatFields)
            field.IsHighlighted = active is not null && field.Field.Ranges.Count > 0 && field.Field.Ranges.SequenceEqual(active.Ranges);
        foreach (var row in ByteRows)
            foreach (var value in row.Bytes) value.IsHighlighted = active?.Contains(value.Offset) == true;
        OnPropertyChanged(nameof(ActiveField)); OnPropertyChanged(nameof(FieldName));
        OnPropertyChanged(nameof(FieldValue)); OnPropertyChanged(nameof(FieldRanges));
    }

    private void BuildBytePage()
    {
        ByteRows.Clear();
        if (SelectedPacket is { } selected)
        {
            bytePage = Math.Clamp(bytePage, 0, Math.Max(0, (selected.Packet.Bytes.Length - 1) / BytesPerPage));
            int end = Math.Min(selected.Packet.Bytes.Length, (bytePage + 1) * BytesPerPage);
            for (int offset = bytePage * BytesPerPage; offset < end; offset += 16)
            {
                var values = Enumerable.Range(offset, Math.Min(16, end - offset))
                    .Select(i => new InspectorByteViewModel(i, selected.Packet.Bytes[i], byteOwners[i])).ToArray();
                ByteRows.Add(new($"{offset:X6}", values));
            }
        }
        OnPropertyChanged(nameof(BytePageText)); OnPropertyChanged(nameof(CanPreviousPage)); OnPropertyChanged(nameof(CanNextPage));
        UpdateHighlight();
    }

    [RelayCommand] private void PreviousBytePage() { if (CanPreviousPage) { bytePage--; BuildBytePage(); } }
    [RelayCommand] private void NextBytePage() { if (CanNextPage) { bytePage++; BuildBytePage(); } }
    [RelayCommand] private void ShowFieldBytes()
    {
        if (ActiveField?.Field.Ranges.FirstOrDefault() is not { Length: > 0 } range) return;
        bytePage = range.Offset / BytesPerPage;
        BuildBytePage();
    }
    [RelayCommand] private void SelectRelated(RelatedPacketViewModel related) =>
        SelectedPacket = Packets.FirstOrDefault(p => p.Packet.Id == related.Id) ?? SelectedPacket;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop(); timer.Tick -= OnTimerTick; trace.Changed -= OnTraceChanged;
        shutdown.Cancel();
        shutdown.Dispose();
    }
}
