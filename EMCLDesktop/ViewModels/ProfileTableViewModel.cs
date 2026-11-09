using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Core.Values;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public sealed partial class ProfileColumnViewModel(string obis, string name,
    OctetStringFormat? initialFormat = null) : ObservableObject
{
    private readonly List<ProfileCellViewModel> octetCells = [];

    public string Obis { get; } = obis;
    public string Name { get; } = name;
    public IReadOnlyList<OctetStringFormat> OctetFormats => OctetStringFormat.ReadOptions;
    [ObservableProperty] public partial bool HasOctets { get; private set; }
    [ObservableProperty]
    public partial OctetStringFormat? OctetFormat { get; set; } = initialFormat ?? OctetStringFormat.Hex;

    internal void RegisterOctetCell(ProfileCellViewModel cell)
    {
        octetCells.Add(cell);
        HasOctets = true;
    }

    partial void OnOctetFormatChanged(OctetStringFormat? value)
    {
        foreach (var cell in octetCells) cell.RefreshFormat();
    }
}

public sealed class ProfileCellViewModel : ObservableObject
{
    private readonly string text;
    private readonly DlmsValue? octets;
    private readonly ProfileColumnViewModel? column;

    public ProfileCellViewModel(string value) => text = value;

    internal ProfileCellViewModel(DlmsValue value, ProfileColumnViewModel column)
    {
        text = DlmsDisplay.IsNumeric(value.Type) ? DlmsDisplay.NumericDecimal(value) : DlmsDisplay.Value(value);
        NumericHexValue = DlmsDisplay.IsNumeric(value.Type) ? DlmsDisplay.NumericHex(value) : "";
        if (value.Type == DlmsType.OctetString)
        {
            octets = value;
            this.column = column;
            column.RegisterOctetCell(this);
        }
    }

    public string NumericHexValue { get; } = "";
    public bool HasNumber => NumericHexValue.Length != 0;

    public string Value => octets is not null && column?.OctetFormat is { } format && format != OctetStringFormat.Hex
        ? DlmsDisplay.OctetPreview(octets, format) ?? $"{DlmsDisplay.UnavailableOctetPreview(format)}. HEX\n{text}"
        : text;

    internal void RefreshFormat() => OnPropertyChanged(nameof(Value));
}

public sealed record ProfileRowViewModel(int Number, IReadOnlyList<ProfileCellViewModel> Cells);

public sealed class ProfileTableViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly IReadOnlyList<ProfileRowViewModel> allRows;
    private int pageIndex;

    public ProfileTableViewModel(IReadOnlyList<ProfileColumnViewModel> columns,
        IReadOnlyList<ProfileRowViewModel> rows)
    {
        Columns = columns;
        allRows = rows;
    }

    public IReadOnlyList<ProfileColumnViewModel> Columns { get; }
    public IReadOnlyList<ProfileRowViewModel> Rows =>
        allRows.Skip(pageIndex * PageSize).Take(PageSize).ToArray();
    public int RowCount => allRows.Count;
    public int PageCount => Math.Max(1, (RowCount + PageSize - 1) / PageSize);
    public string PageSummary => RowCount == 0 ? "Строк нет" :
        $"Страница {pageIndex + 1} из {PageCount} · строки {pageIndex * PageSize + 1}–{Math.Min(RowCount, (pageIndex + 1) * PageSize)}";
    public bool CanPreviousPage => pageIndex > 0;
    public bool CanNextPage => pageIndex + 1 < PageCount;

    public void PreviousPage()
    {
        if (!CanPreviousPage) return;
        pageIndex--;
        NotifyPageChanged();
    }

    public void NextPage()
    {
        if (!CanNextPage) return;
        pageIndex++;
        NotifyPageChanged();
    }

    private void NotifyPageChanged()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
    }
}
