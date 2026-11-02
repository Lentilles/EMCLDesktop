using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCL.Objects.ProfileGeneric;

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
    public sbyte Index { get; } = index;
    public string Name { get; } = name ?? "Неизвестный атрибут";
    public bool HasDeclaredType { get; } = type is not null;
    [ObservableProperty] public partial string Type { get; set; } = type ?? "Тип не указан";
    public string Access { get; } = access;
    public bool CanRead { get; } = canRead;
    [ObservableProperty] public partial bool IsReadEnabled { get; set; } = true;
    [ObservableProperty] public partial string Value { get; set; } = "Не считано";
}

public sealed class ObjectGroupViewModel(string className, ushort classId, byte classVersion,
    IReadOnlyList<MeterObjectViewModel> objects)
{
    public string ClassName { get; } = className;
    public string Subtitle { get; } = $"Класс {classId} · версия {classVersion} · объектов: {objects.Count}";
    public IReadOnlyList<MeterObjectViewModel> Objects { get; } = objects;
}

public sealed class MethodViewModel(sbyte index, string? name, string? parameterType, string access)
{
    public sbyte Index { get; } = index;
    public string Name { get; } = name ?? "Неизвестный метод";
    public string ParameterType { get; } = parameterType ?? "Тип параметра не указан";
    public string Access { get; } = access;
}
