using CommunityToolkit.Mvvm.ComponentModel;

namespace EMCLDesktop.ViewModels;

public sealed record ProfileColumnViewModel(string Obis, string Name);

public sealed record ProfileCellViewModel(string Value);

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
