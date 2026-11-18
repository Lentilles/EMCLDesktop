using EMCL.Core.Codecs;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public class AttributeViewModelTests
{
    private static readonly OctetStringFormat Windows1251 = OctetStringFormat.Options.Single(format => format.Label == "Windows-1251");
    private static readonly OctetStringFormat Utf8 = OctetStringFormat.Options.Single(format => format.Label == "UTF-8");
    private static readonly OctetStringFormat Ascii = OctetStringFormat.Options.Single(format => format.Label == "ASCII");
    private const string DateHex = "07EA0A07030D1F30FF800000";
    private const string DatePreview = "2026-10-07 13:31:48 · ср · смещение UTC не задано · статус 0x00";
    private static readonly OctetStringFormat DateFormat = OctetStringFormat.DlmsDateTime;
    private const string CalendarHex = "07EA0B0901";

    [Fact]
    public void Constructor_UnknownType_LeavesWriteTypeUndetermined()
    {
        // Arrange
        const string name = "value";

        // Act
        var attr = new AttributeViewModel(2, name, null, "", true);

        // Assert
        Assert.Null(attr.WriteType);
        Assert.False(attr.CanUseReadValue, "Unknown type was guessed");
    }

    [Fact]
    public void ToggleWriteEditor_WithoutWriteAccess_LeavesEditorClosed()
    {
        // Arrange
        var attr = new AttributeViewModel(2, "value", null, "", true);
        var large = DlmsValue.Octets(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        attr.RememberReadValue(large);

        // Act
        attr.ToggleWriteEditor();

        // Assert
        Assert.False(attr.IsWriteEditorOpen, "Editor opened without write access");
    }

    [Fact]
    public void ToggleWriteEditor_WithLongReadValue_CopiesFullValue()
    {
        // Arrange
        var attr = new AttributeViewModel(2, "value", null, "", true);
        var large = DlmsValue.Octets(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        attr.RememberReadValue(large);
        attr.IsWriteEnabled = true;

        // Act
        attr.ToggleWriteEditor();

        // Assert
        Assert.True(attr.IsWriteEditorOpen, "Editor copied a shortened display value");
        Assert.Equal(large.Bytes.ToArray(), DlmsValueEditor.Parse(attr.WriteType!.Value, attr.WriteText).Bytes.ToArray());
    }

    [Fact]
    public void RememberReadValue_ExistingWriteDraft_PreservesDraft()
    {
        // Arrange
        var attr = new AttributeViewModel(2, "value", null, "", true);
        attr.WriteText = "draft";

        // Act
        attr.RememberReadValue(DlmsValue.Octets([1, 2]));

        // Assert
        Assert.Equal("draft", attr.WriteText);
    }

    [Fact]
    public void ToggleWriteEditor_ReopenedWithEmptyDraft_PreservesEmptyDraft()
    {
        // Arrange
        var attr = new AttributeViewModel(2, "value", null, "", true);
        attr.IsWriteEnabled = true;
        var large = DlmsValue.Octets(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        attr.RememberReadValue(large);
        attr.ToggleWriteEditor();
        attr.WriteText = "";
        attr.ToggleWriteEditor();

        // Act
        attr.ToggleWriteEditor();

        // Assert
        Assert.True(attr.IsWriteEditorOpen, "Reopening replaced an intentionally empty draft");
        Assert.Equal("", attr.WriteText);
    }

    [Fact]
    public void UseReadValue_LatestRead_CopiesLatestValue()
    {
        // Arrange
        var attr = new AttributeViewModel(2, "value", null, "", true);
        attr.RememberReadValue(DlmsValue.Octets([1, 2]));
        attr.WriteText = "draft";

        // Act
        attr.UseReadValue();

        // Assert
        Assert.Equal("0102", attr.WriteText);
    }

    [Fact]
    public void ForgetReadValue_ExistingRead_DisablesCopy()
    {
        // Arrange
        var attr = new AttributeViewModel(2, "value", null, "", true);
        attr.RememberReadValue(DlmsValue.Octets([1, 2]));

        // Act
        attr.ForgetReadValue();

        // Assert
        Assert.False(attr.CanUseReadValue, "Stale pre-write value remained copyable");
    }

    [Fact]
    public void RememberReadValue_Octets_DefaultsToHexAndPreviewsOtherFormats()
    {
        // Arrange
        var octets = new AttributeViewModel(2, "value", null, "", true) { Value = "CFF0E8E2E5F2", IsWriteEnabled = true };

        // Act
        octets.RememberReadValue(DlmsValue.Octets(Convert.FromHexString(octets.Value)));

        // Assert
        Assert.True(octets.HasReadOctets, "OctetString did not default to HEX");
        Assert.True(octets.DisplayedValue == octets.Value, "OctetString did not default to HEX");
        var previews = octets.ReadOctetPreviews;
        Assert.Equal(OctetStringFormat.ReadOptions, previews.Select(preview => preview.Format));
        Assert.True(previews.Single(preview => preview.Format == OctetStringFormat.Hex).Text == octets.Value, "The format list did not preview all options without selecting them");
        Assert.Equal("Привет", previews.Single(preview => preview.Format == Windows1251).Text);
        Assert.Equal("Значение не представимо как текст", previews.Single(preview => preview.Format == Ascii).Text);
        Assert.Equal(OctetStringFormat.Hex, octets.ReadOctetFormat);
    }

    [Fact]
    public void ReadOctetFormat_UnrepresentableText_DisplaysHexFallback()
    {
        // Arrange
        var octets = CreateOctetAttribute();

        // Act
        octets.ReadOctetFormat = Ascii;

        // Assert
        Assert.Contains(octets.Value, octets.DisplayedValue);
    }

    [Fact]
    public void ToggleWriteEditor_RepresentableTextRead_UsesSelectedEncodingAndShowsDraftPreviews()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        octets.ReadOctetFormat = Windows1251;

        // Act
        octets.ToggleWriteEditor();

        // Assert
        Assert.Equal("Привет", octets.DisplayedValue);
        Assert.Equal("Привет", octets.WriteText);
        Assert.True(octets.WriteOctetFormat == Windows1251, "Editor did not start with the selected OctetString representation");
        var draftPreviews = octets.WriteOctetPreviews;
        Assert.True(draftPreviews.Single(preview => preview.Format == OctetStringFormat.Hex).Text == octets.Value, "The combined draft field did not preview its bytes in every encoding");
        Assert.Equal("Привет", draftPreviews.Single(preview => preview.Format == Windows1251).Text);
    }

    [Fact]
    public void TryChangeWriteOctetFormat_UnrepresentableDraft_PreservesDraftAndFormat()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        octets.ReadOctetFormat = Windows1251;
        octets.ToggleWriteEditor();

        // Act
        var changed = octets.TryChangeWriteOctetFormat(Utf8);

        // Assert
        Assert.False(changed);
        Assert.Equal("Привет", octets.WriteText);
        Assert.Equal(Windows1251, octets.WriteOctetFormat);
    }

    [Fact]
    public void TryChangeWriteOctetFormat_TextToHex_PreservesBytes()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        octets.ReadOctetFormat = Windows1251;
        octets.ToggleWriteEditor();

        // Act
        var changed = octets.TryChangeWriteOctetFormat(OctetStringFormat.Hex);

        // Assert
        Assert.True(changed);
        Assert.Equal(octets.Value, octets.WriteText);
    }

    [Fact]
    public void TryChangeWriteOctetFormat_InvalidUtf8Bytes_LeavesHexFormat()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        octets.ToggleWriteEditor();

        // Act
        var changed = octets.TryChangeWriteOctetFormat(Utf8);

        // Assert
        Assert.False(changed);
        Assert.Equal(OctetStringFormat.Hex, octets.WriteOctetFormat);
    }

    [Fact]
    public void TryChangeWriteOctetFormat_ValidUtf8Bytes_ConvertsDraftToText()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        octets.ToggleWriteEditor();
        var draftPreviews = octets.WriteOctetPreviews;
        octets.WriteText = "D0AF";

        // Act
        var changed = octets.TryChangeWriteOctetFormat(Utf8);

        // Assert
        Assert.True(changed);
        Assert.Equal("Я", octets.WriteText);
        Assert.Equal("Я", draftPreviews.Single(preview => preview.Format == Utf8).Text);
    }

    [Fact]
    public void WriteText_Utf8Draft_RefreshesExistingPreviewsAndEncodesBytes()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        octets.ToggleWriteEditor();
        var draftPreviews = octets.WriteOctetPreviews;
        octets.WriteText = "D0AF";
        octets.TryChangeWriteOctetFormat(Utf8);

        // Act
        octets.WriteText = "Привет";

        // Assert
        Assert.Same(draftPreviews, octets.WriteOctetPreviews);
        Assert.Equal("D09FD180D0B8D0B2D0B5D182", draftPreviews.Single(preview => preview.Format == OctetStringFormat.Hex).Text);
        Assert.Equal(Convert.FromHexString("D09FD180D0B8D0B2D0B5D182"), DlmsValueEditor.Parse(DlmsType.OctetString, octets.WriteText, octetFormat: octets.WriteOctetFormat) .Bytes);
    }

    [Fact]
    public void TryChangeWriteOctetFormat_IncompleteHex_PreservesDraftAndClearsPreviews()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        octets.ToggleWriteEditor();
        var draftPreviews = octets.WriteOctetPreviews;
        octets.WriteText = "F";

        // Act
        var changed = octets.TryChangeWriteOctetFormat(Ascii);

        // Assert
        Assert.False(changed);
        Assert.Equal("F", octets.WriteText);
        Assert.All(draftPreviews, preview => Assert.Empty(preview.Text));
    }

    [Fact]
    public void RememberReadValue_SecondOctetRead_RefreshesExistingPreviews()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        var previews = octets.ReadOctetPreviews;

        // Act
        octets.RememberReadValue(DlmsValue.Octets([0x41]));

        // Assert
        Assert.Same(previews, octets.ReadOctetPreviews);
        Assert.Equal("A", previews.Single(preview => preview.Format == Ascii).Text);
    }

    [Fact]
    public void ForgetReadValue_Octets_ClearsPreviews()
    {
        // Arrange
        var octets = CreateOctetAttribute();
        var previews = octets.ReadOctetPreviews;

        // Act
        octets.ForgetReadValue();

        // Assert
        Assert.False(octets.HasReadOctets, "A forgotten value left stale format previews");
        Assert.True(previews.All(preview => preview.Text.Length == 0), "A forgotten value left stale format previews");
    }

    [Fact]
    public void ReadOctetFormat_DlmsDateTime_FormatsUnspecifiedFields()
    {
        // Arrange
        var dateValue = DlmsValue.Octets(Convert.FromHexString(DateHex));
        var dateOctets = new AttributeViewModel(2, "time", null, "", true) { Value = DateHex, IsWriteEnabled = true };
        dateOctets.RememberReadValue(dateValue);

        // Act
        dateOctets.ReadOctetFormat = DateFormat;

        // Assert
        Assert.True(dateOctets.ReadOctetPreviews.Single(preview => preview.Format == DateFormat).Text == DatePreview, "DLMS date-time preview lost fields with unspecified hundredths and deviation");
        Assert.True(dateOctets.DisplayedValue == DatePreview, "DLMS date-time preview lost fields with unspecified hundredths and deviation");
    }

    [Fact]
    public void ToggleWriteEditor_DlmsDateTimePreview_CopiesOriginalOctetsInHex()
    {
        // Arrange
        var dateValue = DlmsValue.Octets(Convert.FromHexString(DateHex));
        var dateOctets = new AttributeViewModel(2, "time", null, "", true) { Value = DateHex, IsWriteEnabled = true };
        dateOctets.RememberReadValue(dateValue);
        dateOctets.ReadOctetFormat = DateFormat;

        // Act
        dateOctets.ToggleWriteEditor();

        // Assert
        Assert.DoesNotContain(DateFormat, dateOctets.WriteOctetFormats);
        Assert.Equal(OctetStringFormat.Hex, dateOctets.WriteOctetFormat);
        Assert.Equal(DlmsType.OctetString, dateOctets.WriteType);
        Assert.True(dateOctets.WriteText == DateHex, "A date-time preview altered the original OctetString in the write editor");
    }

    [Fact]
    public void RememberReadValue_Date_DefaultsToCalendarDateAndShowsFormatPreviews()
    {
        // Arrange
        var calendarValue = DlmsValueEditor.Parse(DlmsType.Date, CalendarHex);
        var calendar = new AttributeViewModel(2, "date", "Date", "", true)
            { Value = CalendarHex, IsWriteEnabled = true };

        // Act
        calendar.RememberReadValue(calendarValue);

        // Assert
        Assert.True(calendar.HasReadFormats, "Date did not default to a readable calendar date");
        Assert.False(calendar.HasReadOctets, "Date did not default to a readable calendar date");
        Assert.Equal(OctetStringFormat.DlmsDate, calendar.ReadOctetFormat);
        Assert.Equal("2026-11-09 · пн", calendar.DisplayedValue);
        var calendarPreviews = calendar.ReadOctetPreviews;
        Assert.Equal(OctetStringFormat.DateReadOptions, calendarPreviews.Select(preview => preview.Format));
        Assert.True(calendarPreviews[0].Text == CalendarHex, "Date dropdown did not preview HEX, ISO and local dates together");
        Assert.Equal("2026-11-09 · пн", calendarPreviews[1].Text);
        Assert.Equal("09.11.2026 · пн", calendarPreviews[2].Text);
    }

    [Theory]
    [InlineData("Local", "09.11.2026 · пн")]
    [InlineData("Hex", CalendarHex)]
    public void ReadOctetFormat_LocalOrHexDate_UpdatesDisplayedValue(string formatName, string expected)
    {
        // Arrange
        var calendar = CreateDateAttribute();
        var format = formatName == "Local" ? OctetStringFormat.LocalDate : OctetStringFormat.Hex;

        // Act
        calendar.ReadOctetFormat = format;

        // Assert
        Assert.Equal(expected, calendar.DisplayedValue);
    }

    [Fact]
    public void ToggleWriteEditor_CalendarPreview_PreservesDateTypeAndOriginalBytes()
    {
        // Arrange
        var calendar = CreateDateAttribute();
        var codec = new AxdrCodec();
        var calendarValue = DlmsValueEditor.Parse(DlmsType.Date, CalendarHex);
        calendar.ReadOctetFormat = OctetStringFormat.LocalDate;

        // Act
        calendar.ToggleWriteEditor();

        // Assert
        Assert.Equal(DlmsType.Date, calendar.WriteType);
        Assert.True(calendar.WriteText == CalendarHex, "A calendar preview altered the Date type or bytes copied for writing");
        Assert.Equal(codec.EncodeBytes(calendarValue), codec.EncodeBytes(DlmsValueEditor.Parse(DlmsType.Date, calendar.WriteText)));
    }

    [Fact]
    public void RememberReadValue_NewDate_PreservesFormatAndRefreshesPreviews()
    {
        // Arrange
        var calendar = CreateDateAttribute();
        var calendarPreviews = calendar.ReadOctetPreviews;
        calendar.ReadOctetFormat = OctetStringFormat.LocalDate;

        // Act
        calendar.RememberReadValue(DlmsValueEditor.Parse(DlmsType.Date, "07EA0B0A02"));

        // Assert
        Assert.Same(calendarPreviews, calendar.ReadOctetPreviews);
        Assert.Equal("10.11.2026 · вт", calendar.DisplayedValue);
        Assert.Equal("2026-11-10 · вт", calendarPreviews[1].Text);
    }

    [Fact]
    public void RememberReadValue_InvalidDate_ShowsDiagnosticAndRawBytes()
    {
        // Arrange
        var calendar = CreateDateAttribute();
        calendar.ReadOctetFormat = OctetStringFormat.LocalDate;

        // Act
        calendar.RememberReadValue(DlmsValueEditor.Parse(DlmsType.Date, "07E7021D03"));

        // Assert
        Assert.EndsWith("07E7021D03", calendar.DisplayedValue);
        Assert.Contains("DLMS date", calendar.DisplayedValue);
    }

    [Fact]
    public void RememberReadValue_DateChangesToOctets_ReplacesAvailableFormats()
    {
        // Arrange
        var calendar = CreateDateAttribute();
        calendar.ReadOctetFormat = OctetStringFormat.LocalDate;

        // Act
        calendar.RememberReadValue(DlmsValue.Octets([0x41]));

        // Assert
        Assert.Equal(OctetStringFormat.Hex, calendar.ReadOctetFormat);
        Assert.Equal(OctetStringFormat.ReadOptions, calendar.ReadOctetPreviews.Select(preview => preview.Format));
    }

    [Fact]
    public void ForgetReadValue_Date_HidesFormatsAndClearsPreviews()
    {
        // Arrange
        var calendar = CreateDateAttribute();
        var refreshedCalendarPreviews = calendar.ReadOctetPreviews;

        // Act
        calendar.ForgetReadValue();

        // Assert
        Assert.False(calendar.HasReadFormats, "Forgetting Date left a visible dropdown or stale date previews");
        Assert.True(refreshedCalendarPreviews.All(preview => preview.Text.Length == 0), "Forgetting Date left a visible dropdown or stale date previews");
    }

    [Fact]
    public void ReadOctetFormat_NestedDate_ChangesOnlySelectedNode()
    {
        // Arrange
        var calendarValue = DlmsValueEditor.Parse(DlmsType.Date, CalendarHex);
        var calendarTree = new AttributeViewModel(2, "dates", null, "", true);
        calendarTree.RememberReadValue(DlmsValue.Structure(calendarValue, DlmsValue.Array(calendarValue)));
        var calendarNodes = calendarTree.ReadValueNodes.Single().Children;
        var nestedCalendar = calendarNodes[1].Children.Single();

        // Act
        calendarNodes[0].ReadOctetFormat = OctetStringFormat.LocalDate;

        // Assert
        Assert.True(calendarNodes[0].HasReadFormats, "Nested Date fields did not expose independent calendar formats");
        Assert.Equal("09.11.2026 · пн", calendarNodes[0].DisplayedValue);
        Assert.True(nestedCalendar.HasReadFormats, "Nested Date fields did not expose independent calendar formats");
        Assert.Equal("2026-11-09 · пн", nestedCalendar.DisplayedValue);
        Assert.True(nestedCalendar.ReadOctetPreviews[0].Text == CalendarHex, "Nested Date fields did not expose independent calendar formats");
    }

    [Fact]
    public void RememberReadValue_CompoundValue_CreatesTypedLeavesWithIndependentPreviews()
    {
        // Arrange
        var compound = new AttributeViewModel(2, "value", null, "", true);
        var dateValue = DlmsValue.Octets(Convert.FromHexString(DateHex));
        var nested = DlmsValue.Array(DlmsValue.Structure(
            DlmsValue.Octets([0xDF]), DlmsValue.Array(DlmsValue.Octets([0xD0, 0xAF])), DlmsValue.Null, dateValue));

        // Act
        compound.RememberReadValue(nested);

        // Assert
        var fields = compound.ReadValueNodes.Single().Children.Single().Children;
        var windowsLeaf = fields[0];
        var utf8Leaf = fields[1].Children.Single();
        Assert.True(compound.ShowReadValueTree);
        Assert.True(compound.HasReadValueTree, "Nested values did not expose typed leaves with default HEX previews");
        Assert.True(windowsLeaf.HasReadOctets, "Nested values did not expose typed leaves with default HEX previews");
        Assert.True(utf8Leaf.HasReadOctets, "Nested values did not expose typed leaves with default HEX previews");
        Assert.Equal("null", fields[2].DisplayedValue);
        Assert.Equal("DF", windowsLeaf.DisplayedValue);
        Assert.Equal("D0AF", utf8Leaf.DisplayedValue);
        Assert.Equal("Я", windowsLeaf.ReadOctetPreviews.Single(preview => preview.Format == Windows1251).Text);
        Assert.Equal("Я", utf8Leaf.ReadOctetPreviews.Single(preview => preview.Format == Utf8).Text);
        Assert.False(fields[2].HasReadOctets, "Nested fields did not expose their own format previews");
    }

    [Fact]
    public void ReadOctetFormat_NestedOctets_KeepsFormatsIndependent()
    {
        // Arrange
        var (compound, _) = CreateCompoundAttribute();
        var fields = compound.ReadValueNodes.Single().Children.Single().Children;
        var windowsLeaf = fields[0];
        var utf8Leaf = fields[1].Children.Single();

        // Act
        windowsLeaf.ReadOctetFormat = Windows1251;

        // Assert
        Assert.Equal("Я", windowsLeaf.DisplayedValue);
        Assert.Equal("D0AF", utf8Leaf.DisplayedValue);
    }

    [Fact]
    public void ReadOctetFormat_NestedUnrepresentableOctets_FallsBackToHex()
    {
        // Arrange
        var (compound, _) = CreateCompoundAttribute();
        var fields = compound.ReadValueNodes.Single().Children.Single().Children;
        var windowsLeaf = fields[0];
        var utf8Leaf = fields[1].Children.Single();

        // Act
        utf8Leaf.ReadOctetFormat = Utf8;
        windowsLeaf.ReadOctetFormat = Ascii;

        // Assert
        Assert.Equal("Я", utf8Leaf.DisplayedValue);
        Assert.EndsWith("DF", windowsLeaf.DisplayedValue);
    }

    [Fact]
    public void ReadOctetFormat_NestedDateTime_FormatsDateAndPreservesUnrepresentableOctets()
    {
        // Arrange
        var (compound, _) = CreateCompoundAttribute();
        var fields = compound.ReadValueNodes.Single().Children.Single().Children;
        var windowsLeaf = fields[0];
        var utf8Leaf = fields[1].Children.Single();

        // Act
        fields[3].ReadOctetFormat = DateFormat;
        utf8Leaf.ReadOctetFormat = DateFormat;

        // Assert
        Assert.True(fields[3].ReadOctetPreviews.Single(preview => preview.Format == DateFormat).Text == DatePreview, "Nested DLMS date-time preview or HEX fallback is missing");
        Assert.True(fields[3].DisplayedValue == DatePreview, "Nested DLMS date-time preview or HEX fallback is missing");
        Assert.EndsWith("D0AF", utf8Leaf.DisplayedValue);
    }

    [Fact]
    public void UseTextValueView_CompoundValue_HidesTree()
    {
        // Arrange
        var (compound, _) = CreateCompoundAttribute();

        // Act
        compound.UseTextValueView = true;

        // Assert
        Assert.True(compound.HasReadValueTree);
        Assert.False(compound.ShowReadValueTree);
    }

    [Fact]
    public void UseTextValueView_ReturnToTree_PreservesSelectedNodes()
    {
        // Arrange
        var (compound, _) = CreateCompoundAttribute();
        var fields = compound.ReadValueNodes.Single().Children.Single().Children;
        var windowsLeaf = fields[0];
        compound.UseTextValueView = true;

        // Act
        compound.UseTextValueView = false;

        // Assert
        Assert.True(compound.ShowReadValueTree, "Switching back did not preserve the tree nodes");
        Assert.True(compound.ReadValueNodes.Single().Children.Single().Children[0] == windowsLeaf, "Switching back did not preserve the tree nodes");
    }

    [Fact]
    public void UseReadValue_FormattedTree_CopiesOriginalAxdrBytes()
    {
        // Arrange
        var (compound, nested) = CreateCompoundAttribute();
        var codec = new AxdrCodec();
        var fields = compound.ReadValueNodes.Single().Children.Single().Children;
        var windowsLeaf = fields[0];
        var utf8Leaf = fields[1].Children.Single();
        windowsLeaf.ReadOctetFormat = Windows1251;
        utf8Leaf.ReadOctetFormat = Utf8;
        fields[3].ReadOctetFormat = DateFormat;

        // Act
        compound.UseReadValue();

        // Assert
        Assert.Equal(codec.EncodeBytes(nested), codec.EncodeBytes(DlmsValueEditor.Parse(compound.WriteType!.Value, compound.WriteText)));
    }

    [Fact]
    public void RememberReadValue_LargeArray_ExposesAllIndicesInRanges()
    {
        // Arrange
        var compound = new AttributeViewModel(2, "value", null, "", true);

        // Act
        compound.RememberReadValue(DlmsValue.Array(Enumerable.Repeat(DlmsValue.Null, 101).ToArray()));

        // Assert
        var ranges = compound.ReadValueNodes.Single().Children;
        Assert.Equal(2, ranges.Count);
        Assert.Equal(100, ranges[0].Children.Count);
        Assert.StartsWith("[100]", ranges[1].Label);
    }

    [Fact]
    public void ForgetReadValue_CompoundValue_ClearsTree()
    {
        // Arrange
        var (compound, _) = CreateCompoundAttribute();

        // Act
        compound.ForgetReadValue();

        // Assert
        Assert.False(compound.HasReadValueTree, "A forgotten value left a stale tree");
        Assert.False(compound.ShowReadValueTree, "A forgotten value left a stale tree");
    }

    [Theory]
    [MemberData(nameof(NumericValues))]
    public void RememberReadValue_Number_ExposesDecimalAndFullHex(DlmsType numericType, string dec, string hex)
    {
        // Arrange
        var value = DlmsValueEditor.Parse(numericType, dec);
        var numericAttribute = new AttributeViewModel(2, "number", null, "", true) { Value = dec };

        // Act
        numericAttribute.RememberReadValue(value);

        // Assert
        Assert.True(numericAttribute.HasReadNumber);
        Assert.Equal(dec, numericAttribute.DisplayedValue);
        Assert.Equal($"0x{hex}", numericAttribute.NumericHexValue);
    }

    [Theory]
    [MemberData(nameof(NumericValues))]
    public void RememberReadValue_NestedNumbers_ExposeDecimalAndFullHex(DlmsType numericType, string dec, string hex)
    {
        // Arrange
        var value = DlmsValueEditor.Parse(numericType, dec);
        var compound = new AttributeViewModel(2, "value", null, "", true);

        // Act
        compound.RememberReadValue(DlmsValue.Structure(value, value));

        // Assert
        var numbers = compound.ReadValueNodes.Single().Children;
        Assert.All(numbers, number =>
        {
            Assert.True(number.HasReadNumber);
            Assert.Equal(dec, number.DisplayedValue);
            Assert.Equal($"0x{hex}", number.NumericHexValue);
        });
    }

    [Theory]
    [MemberData(nameof(NumericValues))]
    public void Build_ProfileWithNumbers_ExposesDecimalAndFullHexWithoutLabels(DlmsType numericType, string dec, string hex)
    {
        // Arrange
        var value = DlmsValueEditor.Parse(numericType, dec);

        // Act
        var table = ProfileGenericTableBuilder.Build(DlmsValue.Array(),
            DlmsValue.Array(DlmsValue.Structure(value, DlmsValue.Structure(value, value))), []);

        // Assert
        var cells = table.Rows.Single().Cells;
        Assert.True(cells[0].HasNumber);
        Assert.Equal(dec, cells[0].Value);
        Assert.Equal($"0x{hex}", cells[0].NumericHexValue);
        Assert.Contains(dec + Environment.NewLine, cells[1].Value);
        Assert.Contains($"0x{hex}", cells[1].Value);
        Assert.DoesNotContain("DEC ", cells[1].Value);
        Assert.DoesNotContain("HEX ", cells[1].Value);
    }

    [Theory]
    [InlineData(DlmsType.Int8, "-128")]
    [InlineData(DlmsType.Int16, "-2")]
    [InlineData(DlmsType.Int32, "-2")]
    [InlineData(DlmsType.Int64, "-9223372036854775808")]
    [InlineData(DlmsType.UInt8, "2")]
    [InlineData(DlmsType.UInt16, "2")]
    [InlineData(DlmsType.UInt32, "2")]
    [InlineData(DlmsType.UInt64, "18446744073709551615")]
    [InlineData(DlmsType.DeltaInt8, "-2")]
    [InlineData(DlmsType.DeltaInt16, "-2")]
    [InlineData(DlmsType.DeltaInt32, "-2")]
    [InlineData(DlmsType.DeltaUInt8, "7")]
    [InlineData(DlmsType.DeltaUInt16, "7")]
    [InlineData(DlmsType.DeltaUInt32, "7")]
    [InlineData(DlmsType.Float32, "1.25")]
    [InlineData(DlmsType.Float64, "-1.25")]
    [InlineData(DlmsType.Bcd, "18")]
    [InlineData(DlmsType.Enum, "255")]
    public void UseReadValue_Number_CopiesDecimalValue(DlmsType numericType, string dec)
    {
        // Arrange
        var value = DlmsValueEditor.Parse(numericType, dec);
        var numericAttribute = new AttributeViewModel(2, "number", null, "", true) { Value = dec };
        numericAttribute.RememberReadValue(value);

        // Act
        numericAttribute.UseReadValue();

        // Assert
        Assert.True(numericAttribute.WriteText == dec && numericAttribute.DisplayedValue == dec,
            $"{numericType} display changed the value copied to the editor");
    }

    public static TheoryData<DlmsType, string, string> NumericValues => new()
    {
        { DlmsType.Int8, "-128", "80" },
        { DlmsType.Int16, "-2", "FFFE" },
        { DlmsType.Int32, "-2", "FFFFFFFE" },
        { DlmsType.Int64, "-9223372036854775808", "8000000000000000" },
        { DlmsType.UInt8, "2", "02" },
        { DlmsType.UInt16, "2", "0002" },
        { DlmsType.UInt32, "2", "00000002" },
        { DlmsType.UInt64, "18446744073709551615", "FFFFFFFFFFFFFFFF" },
        { DlmsType.DeltaInt8, "-2", "FE" },
        { DlmsType.DeltaInt16, "-2", "FFFE" },
        { DlmsType.DeltaInt32, "-2", "FFFFFFFE" },
        { DlmsType.DeltaUInt8, "7", "07" },
        { DlmsType.DeltaUInt16, "7", "0007" },
        { DlmsType.DeltaUInt32, "7", "00000007" },
        { DlmsType.Float32, "1.25", "3FA00000" },
        { DlmsType.Float64, "-1.25", "BFF4000000000000" },
        { DlmsType.Bcd, "18", "12" },
        { DlmsType.Enum, "255", "FF" },
    };

    private static AttributeViewModel CreateOctetAttribute()
    {
        var octets = new AttributeViewModel(2, "value", null, "", true) { Value = "CFF0E8E2E5F2", IsWriteEnabled = true };
        octets.RememberReadValue(DlmsValue.Octets(Convert.FromHexString(octets.Value)));
        return octets;
    }

    private static AttributeViewModel CreateDateAttribute()
    {
        var calendarValue = DlmsValueEditor.Parse(DlmsType.Date, CalendarHex);
        var calendar = new AttributeViewModel(2, "date", "Date", "", true)
            { Value = CalendarHex, IsWriteEnabled = true };
        calendar.RememberReadValue(calendarValue);
        return calendar;
    }

    private static (AttributeViewModel Attribute, DlmsValue Value) CreateCompoundAttribute()
    {
        var dateValue = DlmsValue.Octets(Convert.FromHexString(DateHex));
        var compound = new AttributeViewModel(2, "value", null, "", true);
        var nested = DlmsValue.Array(DlmsValue.Structure(
            DlmsValue.Octets([0xDF]), DlmsValue.Array(DlmsValue.Octets([0xD0, 0xAF])), DlmsValue.Null, dateValue));
        compound.RememberReadValue(nested);
        return (compound, nested);
    }
}
