using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public class AttributeDialogViewModelTests
{
    [Fact]
    public async Task SelectedNode_NestedReadOnlyLeaf_ShowsPathAndEditAction()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var (_, _, editor) = CreateDialog(vm);

        // Act
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();

        // Assert
        Assert.False(editor.ShowLeafEditor, "Compound preview did not navigate to a nested field");
        Assert.True(editor.ShowEditButton, "Compound preview did not navigate to a nested field");
        Assert.Equal("structure[0][0]", editor.SelectedPath);
    }

    [Fact]
    public async Task CollectionViewIndex_TextSelected_ShowsNestedValue()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var (_, _, editor) = CreateDialog(vm);
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();

        // Act
        editor.CollectionViewIndex = 1;

        // Assert
        Assert.True(editor.ShowCollectionText, "Text view lost a nested value");
        Assert.False(editor.ShowTree, "Text view lost a nested value");
        Assert.Contains("[0] UInt16: 11", editor.CollectionText);
    }

    [Fact]
    public async Task CollectionViewIndex_ReturnToTree_PreservesSelectedLeaf()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var (_, _, editor) = CreateDialog(vm);
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();
        var selected = editor.SelectedNode;
        editor.CollectionViewIndex = 1;

        // Act
        editor.CollectionViewIndex = 0;

        // Assert
        Assert.True(editor.ShowTree, "Switching back to the tree lost the selected field");
        Assert.False(editor.ShowCollectionText, "Switching back to the tree lost the selected field");
        Assert.True(editor.SelectedNode == selected, "Switching back to the tree lost the selected field");
    }

    [Fact]
    public async Task BeginEdit_SelectedNestedLeaf_RetainsCurrentValue()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var (_, _, editor) = CreateDialog(vm);
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();

        // Act
        editor.BeginEdit();

        // Assert
        Assert.True(editor.ShowLeafEditor, "Editing did not retain the selected leaf");
        Assert.Equal("11", editor.LeafText);
    }

    [Fact]
    public async Task Apply_InvalidNestedLeaf_RejectsDraft()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var (structure, _, editor) = CreateDialog(vm);
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();
        editor.BeginEdit();
        editor.LeafText = "65536";

        // Act
        var staged = editor.Apply();

        // Assert
        Assert.False(staged);
        Assert.False(structure.HasPendingValue);
    }

    [Fact]
    public async Task Apply_ValidNestedLeaf_StagesChangeWithoutMutatingReadValue()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var (structure, original, editor) = CreateDialog(vm);
        editor.SelectedNode = editor.Nodes.Single().Children.Single().Children.Single();
        editor.BeginEdit();
        editor.LeafText = "77";

        // Act
        var staged = editor.Apply();

        // Assert
        Assert.True(staged, editor.Error);
        Assert.True(structure.HasPendingValue);
        Assert.Equal(11UL, original.Items[0].Items[0].AsUInt64());
        Assert.Equal(77UL, structure.PendingValue!.Items[0].Items[0].AsUInt64());
    }

    [Fact]
    public async Task Constructor_LargeCollectionTextView_PreservesAllItemsAndOctetBytes()
    {
        // Arrange
        await using var vm = new MainViewModel();
        var obj = new MeterObjectViewModel(LogicalName.Parse("1.0.99.1.0.255"), 7, 1, "Profile Generic");
        var buffer = new AttributeViewModel(2, "buffer", "Array", "", true) { UseTextValueView = true };
        var rows = Enumerable.Repeat(DlmsValue.Null, 5001).Append(DlmsValue.Octets(new byte[65]));
        buffer.RememberReadValue(new DlmsValue(DlmsType.Array, rows));

        // Act
        var viewer = new AttributeDialogViewModel(vm, obj, buffer, edit: false);

        // Assert
        Assert.True(viewer.ShowCollectionText, "Text mode truncated collection items or individual octets");
        Assert.Contains("[5001] OctetString: " + new string('0', 130), viewer.CollectionText);
    }

    private static (AttributeViewModel Attribute, DlmsValue Original, AttributeDialogViewModel Editor) CreateDialog(MainViewModel vm)
    {
        var obj = new MeterObjectViewModel(LogicalName.Parse("1.0.0.2.0.255"), 1, 0, "Data");
        var structure = new AttributeViewModel(3, "structure", "Structure", "", true);
        obj.Attributes.Add(structure);
        vm.Objects.Add(obj);
        vm.SelectedObject = obj;
        structure.IsWriteEnabled = true;
        var original = DlmsValue.Structure(DlmsValue.Array(DlmsValue.Number(DlmsType.UInt16, 11)));
        structure.RememberReadValue(original);
        return (structure, original, new AttributeDialogViewModel(vm, obj, structure, edit: false));
    }
}
