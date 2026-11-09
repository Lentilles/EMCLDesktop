using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Core.Values;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public abstract partial class DlmsValuePreviewViewModel : ObservableObject
{
    private IReadOnlyList<OctetStringPreview>? octetPreviews;
    private DlmsType? previewType;

    protected abstract DlmsValue? PreviewValue { get; }
    protected abstract string PreviewText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedValue))]
    public partial OctetStringFormat? ReadOctetFormat { get; set; } = OctetStringFormat.Hex;

    public IReadOnlyList<OctetStringPreview> ReadOctetPreviews => octetPreviews ??=
        (PreviewValue?.Type == DlmsType.Date ? OctetStringFormat.DateReadOptions : OctetStringFormat.ReadOptions)
            .Select(format => new OctetStringPreview(format, PreviewValue)).ToArray();
    public bool HasReadOctets => PreviewValue?.Type == DlmsType.OctetString;
    public bool HasReadFormats => HasReadOctets || PreviewValue?.Type == DlmsType.Date;
    public bool HasReadNumber => DlmsDisplay.IsNumeric(PreviewValue?.Type);
    public string NumericHexValue => PreviewValue is { } value && HasReadNumber ? DlmsDisplay.NumericHex(value) : "";

    public string DisplayedValue => PreviewValue switch
    {
        { Type: DlmsType.Date } date when ReadOctetFormat is { } format =>
            DlmsDisplay.OctetPreview(date, format) ?? $"{DlmsDisplay.UnavailableOctetPreview(format)}. HEX\n{DlmsDisplay.Value(date)}",
        { Type: DlmsType.OctetString } octets when ReadOctetFormat is { } format && format != OctetStringFormat.Hex =>
            DlmsDisplay.OctetPreview(octets, format) ?? $"{DlmsDisplay.UnavailableOctetPreview(format)}. HEX\n{PreviewText}",
        { } number when DlmsDisplay.IsNumeric(number.Type) => DlmsDisplay.NumericDecimal(number),
        _ => PreviewText
    };

    protected void RefreshOctetPreviews(DlmsValue? value)
    {
        if (previewType != value?.Type)
        {
            bool dateOptionsChanged = (previewType == DlmsType.Date) != (value?.Type == DlmsType.Date);
            previewType = value?.Type;
            if (dateOptionsChanged)
            {
                if (octetPreviews is not null)
                    foreach (var preview in octetPreviews) preview.UpdateValue(null);
                octetPreviews = null;
                ReadOctetFormat = previewType == DlmsType.Date ? OctetStringFormat.DlmsDate : OctetStringFormat.Hex;
                OnPropertyChanged(nameof(ReadOctetPreviews));
            }
        }
        if (octetPreviews is null) return;
        // Update the open dropdown without replacing its items.
        foreach (var preview in octetPreviews) preview.UpdateValue(value);
    }
}

public sealed class OctetStringPreview(OctetStringFormat format, DlmsValue? value) : ObservableObject
{
    private DlmsValue? currentValue = value;

    public OctetStringFormat Format { get; } = format;
    public string Text => currentValue is { Type: DlmsType.OctetString or DlmsType.Date } octets
        ? DlmsDisplay.OctetPreview(octets, Format) ?? DlmsDisplay.UnavailableOctetPreview(Format)
        : "";

    internal void UpdateValue(DlmsValue? value) => SetProperty(ref currentValue, value, nameof(Text));

    public override string ToString() => Format.Label;
}
