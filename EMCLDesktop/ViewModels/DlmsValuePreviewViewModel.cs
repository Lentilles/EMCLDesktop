using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Core.Values;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public abstract partial class DlmsValuePreviewViewModel : ObservableObject
{
    protected abstract DlmsValue? PreviewValue { get; }
    protected abstract string PreviewText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedValue))]
    public partial OctetStringFormat? ReadOctetFormat { get; set; } = OctetStringFormat.Hex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedValue))]
    public partial bool UseNumericHex { get; set; }

    public IReadOnlyList<OctetStringFormat> ReadOctetFormats => OctetStringFormat.ReadOptions;
    public bool HasReadOctets => PreviewValue?.Type == DlmsType.OctetString;
    public bool HasReadNumber => DlmsDisplay.IsNumeric(PreviewValue?.Type);
    public bool CanChooseReadFormat => HasReadOctets || HasReadNumber;

    public string DisplayedValue => PreviewValue switch
    {
        { Type: DlmsType.OctetString } octets => DlmsDisplay.OctetPreview(octets, ReadOctetFormat, PreviewText),
        { } number when UseNumericHex && DlmsDisplay.IsNumeric(number.Type) => DlmsDisplay.NumericHex(number),
        _ => PreviewText
    };
}
