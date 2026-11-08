using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCL.Objects.ProfileGeneric;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public sealed partial class MeterObjectViewModel(LogicalName logicalName, ushort classId, byte classVersion,
    string? className) : ObservableObject
{
    private static readonly IReadOnlyList<ProfileReadModeOption> readModes =
    [
        new(ProfileReadMode.All, "Весь буфер"),
        new(ProfileReadMode.First, "Первые N записей"),
        new(ProfileReadMode.Last, "Последние N записей"),
        new(ProfileReadMode.Entries, "По номерам записей"),
        new(ProfileReadMode.Dates, "По датам")
    ];

    public LogicalName LogicalName { get; } = logicalName;
    public string Obis => LogicalName.ToString();
    public ushort ClassId { get; } = classId;
    public byte ClassVersion { get; } = classVersion;
    public string ClassName { get; } = className ?? "Неизвестный класс";
    public string Title => $"{Obis}  ·  {ClassName}";
    public bool IsProfileGeneric => ClassId == CosemClassIds.ProfileGeneric;
    public IReadOnlyList<ProfileReadModeOption> ProfileReadModes => readModes;
    public ObservableCollection<ProfileCaptureColumnViewModel> ProfileCaptureColumns { get; } = [];
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsProfileReadEnabled { get; set; } = true;
    [ObservableProperty] public partial ProfileReadModeOption SelectedProfileReadMode { get; set; } = readModes[0];
    [ObservableProperty] public partial string ProfileCountText { get; set; } = "10";
    [ObservableProperty] public partial string ProfileFirstEntryText { get; set; } = "1";
    [ObservableProperty] public partial string ProfileLastEntryText { get; set; } = "10";
    [ObservableProperty] public partial DateTimeOffset? ProfileFromDate { get; set; } = DateTimeOffset.Now.AddDays(-7);
    [ObservableProperty] public partial DateTimeOffset? ProfileToDate { get; set; } = DateTimeOffset.Now;
    [ObservableProperty] public partial string ProfileTimeZone { get; set; } = DateTimeOffset.Now.ToString("zzz");
    [ObservableProperty] public partial ProfileCaptureColumnViewModel? SelectedProfileCaptureColumn { get; set; }
    [ObservableProperty] public partial ProfileTableViewModel? ProfileTable { get; set; }
    [ObservableProperty] public partial string ProfileTableStatus { get; set; } =
        "Считайте buffer и capture_objects, чтобы увидеть таблицу.";
    public bool HasProfileTable => ProfileTable is not null;
    public bool IsProfileCountMode => SelectedProfileReadMode.Mode is ProfileReadMode.First or ProfileReadMode.Last;
    public bool IsProfileEntryRangeMode => SelectedProfileReadMode.Mode == ProfileReadMode.Entries;
    public bool IsProfileDateRangeMode => SelectedProfileReadMode.Mode == ProfileReadMode.Dates;
    public DlmsValue? ProfileBuffer { get; set; }
    public DlmsValue? ProfileCaptureObjects { get; set; }
    public ObservableCollection<AttributeViewModel> Attributes { get; } = [];
    public ObservableCollection<MethodViewModel> Methods { get; } = [];

    partial void OnProfileTableChanged(ProfileTableViewModel? value) =>
        OnPropertyChanged(nameof(HasProfileTable));

    partial void OnSelectedProfileReadModeChanged(ProfileReadModeOption value)
    {
        OnPropertyChanged(nameof(IsProfileCountMode));
        OnPropertyChanged(nameof(IsProfileEntryRangeMode));
        OnPropertyChanged(nameof(IsProfileDateRangeMode));
    }
}

public enum ProfileReadMode { All, First, Last, Entries, Dates }

public sealed record ProfileReadModeOption(ProfileReadMode Mode, string Title)
{
    public override string ToString() => Title;
}

public sealed record ProfileCaptureColumnViewModel(CaptureObject Capture, string Name, DlmsType WireType)
{
    public string Title => $"{Capture.LogicalName} · {Name}" +
        (Capture.DataIndex == 0 ? "" : $" · элемент {Capture.DataIndex}");
}

public sealed partial class AttributeViewModel(sbyte index, string? name, string? type,
    string access, bool canRead) : ObservableObject
{
    private bool hasOpenedWriteEditor;

    public sbyte Index { get; } = index;
    public string Name { get; } = name ?? "Неизвестный атрибут";
    public bool HasDeclaredType { get; } = type is not null;
    [ObservableProperty] public partial string Type { get; set; } = type ?? "Тип не указан";
    public string Access { get; } = access;
    public bool CanRead { get; } = canRead;
    [ObservableProperty] public partial bool IsReadEnabled { get; set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedValue))]
    public partial string Value { get; set; } = "Не считано";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedValue))]
    public partial OctetStringFormat? ReadOctetFormat { get; set; } = OctetStringFormat.Hex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteHint))]
    public partial OctetStringFormat WriteOctetFormat { get; private set; } = OctetStringFormat.Hex;
    public IReadOnlyList<OctetStringFormat> OctetFormats => OctetStringFormat.Options;
    public IReadOnlyList<DlmsType> WriteTypes => DlmsValueEditor.Types;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteHint), nameof(IsWritingOctets))]
    public partial DlmsType? WriteType { get; set; } =
        Enum.TryParse<DlmsType>(type, out var declaredType) ? declaredType : null;
    [ObservableProperty] public partial string WriteText { get; set; } = "";
    [ObservableProperty] public partial string WriteStatus { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleWriteEditor))]
    public partial bool IsWriteEnabled { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleWriteEditor), nameof(WriteEditorAction))]
    public partial bool IsWriteEditorOpen { get; set; }
    [ObservableProperty] public partial string? WriteUnavailableReason { get; set; } = "Подключитесь к счётчику для записи.";
    [ObservableProperty] public partial bool CanUseReadValue { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadOctets), nameof(DisplayedValue))]
    public partial DlmsValue? LastReadValue { get; private set; }
    public bool HasReadOctets => LastReadValue?.Type == DlmsType.OctetString;
    public bool IsWritingOctets => WriteType == DlmsType.OctetString;
    public string DisplayedValue
    {
        get
        {
            if (ReadOctetFormat is not { } format || format == OctetStringFormat.Hex ||
                LastReadValue is not { Type: DlmsType.OctetString } octets)
                return Value;
            if (format.Format(octets.Bytes.AsSpan()) is not { } text)
                return $"{format}: значение не представимо как текст. HEX\n{Value}";

            const int previewLength = 256;
            if (text.Length <= previewLength) return text;
            int end = char.IsHighSurrogate(text[previewLength - 1]) ? previewLength - 1 : previewLength;
            return $"{text[..end]}… ({octets.Bytes.Length} байт)";
        }
    }
    public bool CanToggleWriteEditor => IsWriteEditorOpen || IsWriteEnabled;
    public string WriteEditorAction => IsWriteEditorOpen ? "Свернуть" : "Изменить";
    public string WriteHint => WriteType switch
    {
        DlmsType.OctetString when WriteOctetFormat != OctetStringFormat.Hex =>
            $"Текст в кодировке {WriteOctetFormat}. Пробелы сохраняются.",
        { } selected => DlmsValueEditor.Hint(selected),
        _ => "Выберите тип. Для неизвестного типа сначала считайте значение или укажите тип вручную."
    };

    partial void OnWriteTypeChanged(DlmsType? value) => WriteStatus = "";
    partial void OnWriteTextChanged(string value) => WriteStatus = "";

    public void ToggleWriteEditor()
    {
        if (!CanToggleWriteEditor) return;
        if (!hasOpenedWriteEditor)
        {
            WriteOctetFormat = ReadOctetFormat ?? OctetStringFormat.Hex;
            UseReadValue();
            hasOpenedWriteEditor = true;
        }
        IsWriteEditorOpen = !IsWriteEditorOpen;
    }

    public void RememberReadValue(DlmsValue value)
    {
        LastReadValue = value;
        CanUseReadValue = true;
        if (WriteType is null) WriteType = value.Type;
    }

    public void ForgetReadValue()
    {
        LastReadValue = null;
        CanUseReadValue = false;
    }

    public void UseReadValue()
    {
        if (LastReadValue is not { } value) return;
        try
        {
            string? text = value.Type == DlmsType.OctetString
                ? WriteOctetFormat.Format(value.Bytes.AsSpan()) : DlmsValueEditor.Format(value);
            if (text is null)
            {
                WriteOctetFormat = OctetStringFormat.Hex;
                text = DlmsValueEditor.Format(value);
            }
            WriteType = value.Type;
            WriteText = text;
        }
        catch (Exception error)
        {
            WriteStatus = $"Не удалось подготовить значение: {error.Message}";
        }
    }

    public bool TryChangeWriteOctetFormat(OctetStringFormat format)
    {
        if (format == WriteOctetFormat) return true;
        try
        {
            var bytes = WriteOctetFormat.Parse(WriteText);
            string text = format.Format(bytes)
                ?? throw new FormatException($"Байты черновика не представимы в {format}.");
            WriteOctetFormat = format;
            WriteText = text;
            WriteStatus = "";
            return true;
        }
        catch (FormatException error)
        {
            WriteStatus = $"Формат черновика сохранён. {error.Message}";
            return false;
        }
    }
}

public sealed class ObjectGroupViewModel(string className, ushort classId, byte classVersion,
    IReadOnlyList<MeterObjectViewModel> objects)
{
    public string ClassName { get; } = className;
    public string Subtitle { get; } = $"Класс {classId} · версия {classVersion} · объектов: {objects.Count}";
    public IReadOnlyList<MeterObjectViewModel> Objects { get; } = objects;
}

public sealed partial class MethodViewModel : ObservableObject
{
    public MethodViewModel(sbyte index, string? name, string? parameterType, string access)
    {
        Index = index;
        Name = name ?? "Неизвестный метод";
        ParameterType = parameterType ?? "Тип параметра не указан";
        Access = access;
        if (Enum.TryParse<DlmsType>(parameterType, out var type) && Enum.IsDefined(type))
        {
            ArgumentType = type;
            IncludeParameter = true;
        }
    }

    public sbyte Index { get; }
    public string Name { get; }
    public string ParameterType { get; }
    public string Access { get; }
    public IReadOnlyList<DlmsType> ParameterTypes => DlmsValueEditor.Types;
    [ObservableProperty] public partial bool IncludeParameter { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParameterHint))]
    public partial DlmsType? ArgumentType { get; set; }
    [ObservableProperty] public partial string ParameterText { get; set; } = "";
    [ObservableProperty] public partial bool IsInvokeEnabled { get; set; }
    [ObservableProperty] public partial string? InvokeUnavailableReason { get; set; }
    [ObservableProperty] public partial string InvokeStatus { get; set; } = "";

    public string ParameterHint => ArgumentType is { } type ? DlmsValueEditor.Hint(type) : "Выберите тип параметра.";
}
