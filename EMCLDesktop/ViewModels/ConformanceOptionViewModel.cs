using CommunityToolkit.Mvvm.ComponentModel;
using EMCL.Protocol.Apdu;

namespace EMCLDesktop.ViewModels;

public partial class ConformanceOptionViewModel(string name, DlmsConformance flag) : ObservableObject
{
    public string Name { get; } = name;
    public DlmsConformance Flag { get; } = flag;
    [ObservableProperty] public partial bool IsSelected { get; set; } = true;
}
