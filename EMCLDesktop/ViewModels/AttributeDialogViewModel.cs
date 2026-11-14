using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Core.Codecs;
using EMCL.Protocol.Axdr;
using EMCL.Core.Values;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public sealed partial class AttributeDialogViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly MeterObjectViewModel obj;
    private readonly AttributeViewModel attribute;
    private readonly DlmsValue? original;
    private readonly Dictionary<DlmsValueNodeViewModel, string> changes = [];
    private string? collectionText;

    public AttributeDialogViewModel(MainViewModel main, MeterObjectViewModel obj, AttributeViewModel attribute, bool edit)
    {
        this.main = main;
        this.obj = obj;
        this.attribute = attribute;
        original = attribute.PendingValue ?? attribute.LastReadValue;
        Editor = new(attribute.Index, attribute.Name, attribute.WriteType?.ToString(), attribute.Access, attribute.CanRead)
        {
            IsWriteEnabled = attribute.IsWriteEnabled
        };
        if (original is not null) Editor.RememberReadValue(original);
        if (Editor.HasReadFormats && attribute.ReadOctetFormat is { } format &&
            Editor.ReadOctetPreviews.Any(item => item.Format == format))
            Editor.ReadOctetFormat = format;
        if (attribute.PendingValue is null)
            Editor.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(Editor.ReadOctetFormat))
                    attribute.ReadOctetFormat = Editor.ReadOctetFormat;
            };
        Nodes = attribute.PendingValue is null ? attribute.ReadValueNodes : Editor.ReadValueNodes;
        SelectedNode = Nodes.FirstOrDefault();
        if (SelectedNode is not null) SelectedNode.IsExpanded = true;
        CollectionViewIndex = attribute.UseTextValueView ? 1 : 0;
        if (edit) BeginEdit();
    }

    public string Title => attribute.Name;
    public string Context => $"{obj.Obis} · Атрибут {attribute.Index}";
    public string ScalarSummary => original is null ? attribute.Value : Editor.DisplayedValue;
    public AttributeViewModel Editor { get; }
    public IReadOnlyList<DlmsValueNodeViewModel> Nodes { get; }

    [ObservableProperty] public partial string Error { get; private set; } = "";
    [ObservableProperty] public partial string LeafText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPath), nameof(ShowLeafValue), nameof(ShowLeafEditor))]
    public partial DlmsValueNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseCollectionView), nameof(ShowTree), nameof(ShowCollectionText),
        nameof(CollectionText), nameof(ShowScalar), nameof(ShowEditButton),
        nameof(ShowWholeValueButton), nameof(ShowLeafValue), nameof(ShowLeafEditor))]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTree), nameof(ShowWholeValueButton))]
    public partial bool EditWholeValue { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTree), nameof(ShowCollectionText), nameof(CollectionText))]
    public partial int CollectionViewIndex { get; set; }

    public bool CanChooseCollectionView => Nodes.Count > 0 && !IsEditing;
    public bool ShowCollectionText => CanChooseCollectionView && CollectionViewIndex == 1;
    public string CollectionText => ShowCollectionText
        ? collectionText ??= Nodes[0].FormatCollectionText()
        : "";
    public string SelectedPath => Title + SelectedNode?.Path;
    public bool ShowTree => Nodes.Count > 0 && !EditWholeValue && !ShowCollectionText;

    partial void OnCollectionViewIndexChanged(int value) => attribute.UseTextValueView = value == 1;
    public bool ShowScalar => Nodes.Count == 0 && !IsEditing;
    public bool ShowEditButton => attribute.IsWriteEnabled && !IsEditing;
    public bool ShowWholeValueButton => IsEditing && !EditWholeValue;
    public bool ShowLeafValue => !IsEditing && SelectedNode?.IsScalar == true;
    public bool ShowLeafEditor => IsEditing && SelectedNode?.ScalarValue is { Type: not DlmsType.Null };

    partial void OnSelectedNodeChanged(DlmsValueNodeViewModel? value)
    {
        LeafText = value?.ScalarValue is { } scalar
            ? changes.GetValueOrDefault(value) ?? DlmsValueEditor.Format(scalar) : "";
    }

    partial void OnLeafTextChanged(string value)
    {
        if (!ShowLeafEditor || EditWholeValue || SelectedNode?.ScalarValue is not { } scalar) return;
        if (value == DlmsValueEditor.Format(scalar)) changes.Remove(SelectedNode);
        else changes[SelectedNode] = value;
        Error = "";
    }

    public void BeginEdit()
    {
        if (!ShowEditButton) return;
        Editor.ToggleWriteEditor();
        IsEditing = true;
        if (original?.Type is not (DlmsType.Array or DlmsType.Structure))
            EditWholeValue = true;
    }

    public void BeginWholeValueEdit()
    {
        try
        {
            Editor.WriteText = DlmsValueEditor.Format(BuildTreeValue());
            EditWholeValue = true;
            Error = "";
        }
        catch (Exception error) when (IsEditError(error)) { Error = error.Message; }
    }

    public bool Apply()
    {
        if (!IsEditing || !attribute.IsWriteEnabled) return false;
        try
        {
            var value = EditWholeValue
                ? DlmsValueEditor.Parse(Editor.WriteType ?? throw new FormatException("Выберите тип записываемого значения."),
                    Editor.WriteText, main.ValueLimits, Editor.WriteOctetFormat)
                : BuildTreeValue();
            var baseline = attribute.LastReadValue;
            var codec = new AxdrCodec(main.ValueLimits);
            var encoded = codec.EncodeBytes(value);
            if (baseline is not null && encoded.AsSpan().SequenceEqual(codec.EncodeBytes(baseline))) value = null;
            if (main.StageAttribute(obj, attribute, value)) return true;
            Error = "Атрибут больше недоступен для записи.";
            return false;
        }
        catch (Exception error) when (IsEditError(error))
        {
            Error = error.Message;
            return false;
        }
    }

    private static bool IsEditError(Exception error) => error is
        FormatException or ArgumentException or InvalidOperationException or NotSupportedException;

    private DlmsValue BuildTreeValue()
    {
        var root = original!;
        foreach (var (node, text) in changes)
        {
            try
            {
                var value = DlmsValueEditor.Parse(node.ScalarValue!.Type, text, main.ValueLimits);
                string[] indexes = node.Path.Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries);
                root = ReplaceLeaf(root, indexes, 0, value);
            }
            catch (FormatException error)
            {
                throw new FormatException($"{Title}{node.Path}: {error.Message}", error);
            }
        }
        return root;
    }

    private static DlmsValue ReplaceLeaf(DlmsValue current, string[] indexes, int depth, DlmsValue value)
    {
        if (depth == indexes.Length) return value;
        int index = int.Parse(indexes[depth], System.Globalization.CultureInfo.InvariantCulture);
        return new DlmsValue(current.Type, current.Items.SetItem(index,
            ReplaceLeaf(current.Items[index], indexes, depth + 1, value)));
    }
}
