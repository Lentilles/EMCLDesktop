using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Core.Values;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public sealed partial class DlmsValueNodeViewModel : DlmsValuePreviewViewModel
{
    private const int MaxChildren = 100;
    private readonly DlmsValue? scalar;
    private readonly string scalarText = "";
    private readonly ImmutableArray<DlmsValue> items;
    private readonly int start;
    private readonly int count;
    private IReadOnlyList<DlmsValueNodeViewModel>? children;

    private DlmsValueNodeViewModel(string label, DlmsValue scalar)
    {
        Label = label;
        this.scalar = scalar;
        scalarText = DlmsDisplay.Value(scalar);
        RefreshOctetPreviews(scalar);
    }

    private DlmsValueNodeViewModel(string label, ImmutableArray<DlmsValue> items, int start, int count)
    {
        Label = label;
        this.items = items;
        this.start = start;
        this.count = count;
    }

    public string Label { get; }
    public string Path { get; private init; } = "";
    private DlmsType? CollectionType { get; init; }
    internal string? CompactSummary => CollectionType is { } type ? $"{type} [{items.Length}]" : null;
    internal string FormatCollectionText() => CollectionType is { } type ? DlmsDisplay.Collection(type, items, full: true) : "";
    public DlmsValue? ScalarValue => scalar;
    public bool IsScalar => scalar is not null;
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    protected override DlmsValue? PreviewValue => scalar;
    protected override string PreviewText => scalarText;

    public IReadOnlyList<DlmsValueNodeViewModel> Children => children ??= CreateChildren();

    internal static DlmsValueNodeViewModel Collection(DlmsType type, ImmutableArray<DlmsValue> items) =>
        new(DlmsDisplay.CollectionTitle(type, items.Length), items, 0, items.Length)
        {
            CollectionType = type
        };

    private IReadOnlyList<DlmsValueNodeViewModel> CreateChildren()
    {
        if (count == 0) return [];

        int groupSize = 1;
        while ((count - 1) / groupSize >= MaxChildren)
            groupSize *= MaxChildren;

        var nodes = new DlmsValueNodeViewModel[(count - 1) / groupSize + 1];
        for (int i = 0; i < nodes.Length; i++)
        {
            int index = start + i * groupSize;
            int length = Math.Min(groupSize, count - i * groupSize);
            nodes[i] = length == 1 ? Item(index, items[index], Path)
                : new($"[{index}–{index + length - 1}]", items, index, length) { Path = Path };
        }
        return nodes;
    }

    private static DlmsValueNodeViewModel Item(int index, DlmsValue value, string parentPath)
    {
        var items = DlmsDisplay.CollectionItems(value);
        string path = $"{parentPath}[{index}]";
        return items.IsDefault ? new($"[{index}] {value.Type}", value) { Path = path }
            : new($"[{index}] {DlmsDisplay.CollectionTitle(value.Type, items.Length)}", items, 0, items.Length) { Path = path };
    }
}
