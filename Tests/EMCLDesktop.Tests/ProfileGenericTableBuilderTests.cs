using EMCL.Core.Objects;
using EMCL.Core.Codecs;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public class ProfileGenericTableBuilderTests
{
    private const string ClockHex = "07EA0B090110141EFF800000";
    private static readonly OctetStringFormat Windows1251 = OctetStringFormat.Options.Single(format => format.Label == "Windows-1251");
    private static readonly OctetStringFormat Utf8 = OctetStringFormat.Options.Single(format => format.Label == "UTF-8");
    private static readonly OctetStringFormat Ascii = OctetStringFormat.Options.Single(format => format.Label == "ASCII");

    [Fact]
    public void Build_MixedRows_UsesFormatsOnlyForOctetColumns()
    {
        // Arrange
        var (captures, buffer) = CreateProfile();

        // Act
        var table = ProfileGenericTableBuilder.Build(captures, buffer, []);

        // Assert
        var first = table.Rows[0].Cells;
        Assert.Equal([true, true, true, false], table.Columns.Select(column => column.HasOctets));
        Assert.Equal(OctetStringFormat.ReadOptions, table.Columns[1].OctetFormats);
        Assert.Equal(OctetStringFormat.DlmsDateTime, table.Columns[0].OctetFormat);
        Assert.Equal("2026-11-09 16:20:30 · пн · смещение UTC не задано · статус 0x00", first[0].Value);
        Assert.Equal("DF", first[1].Value);
        Assert.Equal("D0AF", first[2].Value);
        Assert.Equal("42", first[3].Value);
        Assert.True(first[3].HasNumber, "Column defaults changed raw octets or numbers");
        Assert.Equal("0x002A", first[3].NumericHexValue);
    }

    [Fact]
    public void OctetFormat_Changed_NotifiesOnlyCellsInSelectedColumn()
    {
        // Arrange
        var table = BuildProfile();
        var first = table.Rows[0].Cells;
        int changed = 0, unrelatedChanged = 0;
        first[1].PropertyChanged += (_, e) => { if (e.PropertyName == "Value") changed++; };
        first[2].PropertyChanged += (_, e) => { if (e.PropertyName == "Value") unrelatedChanged++; };

        // Act
        table.Columns[1].OctetFormat = Windows1251;

        // Assert
        Assert.Equal("Я", first[1].Value);
        Assert.Equal(1, changed);
        Assert.Equal(0, unrelatedChanged);
        Assert.Equal("D0AF", first[2].Value);
    }

    [Fact]
    public void OctetFormat_MixedColumn_PreservesMissingNumericAndBinaryCells()
    {
        // Arrange
        var table = BuildProfile();

        // Act
        table.Columns[1].OctetFormat = Windows1251;

        // Assert
        Assert.True(table.Rows[50].Cells.Skip(1).All(cell => cell.Value == "—"), "A column encoding changed missing cells or a numeric value in a mixed column");
        Assert.Equal("7", table.Rows[51].Cells[1].Value);
        Assert.Equal("0x07", table.Rows[51].Cells[1].NumericHexValue);
        Assert.Contains("не представимо", table.Rows[52].Cells[1].Value);
        Assert.EndsWith("00FF", table.Rows[52].Cells[1].Value);
        Assert.Equal("", table.Rows[53].Cells[1].Value);
    }

    [Fact]
    public void NextPage_ConfiguredColumnFormat_AppliesFormatToLaterRows()
    {
        // Arrange
        var table = BuildProfile();
        table.Columns[1].OctetFormat = Windows1251;

        // Act
        table.NextPage();

        // Assert
        Assert.Single(table.Rows);
        Assert.Equal(101, table.Rows[0].Number);
        Assert.Equal("Я", table.Rows[0].Cells[1].Value);
        Assert.True(table.Columns[1].OctetFormat == Windows1251, "The column encoding was not applied to rows on another page");
    }

    [Fact]
    public void OctetFormat_MultipleColumns_UsesIndependentEncodings()
    {
        // Arrange
        var table = BuildProfile();
        table.Columns[1].OctetFormat = Windows1251;
        table.NextPage();

        // Act
        table.Columns[2].OctetFormat = Utf8;

        // Assert
        Assert.Equal("Я", table.Rows[0].Cells[2].Value);
        Assert.Equal("Я", table.Rows[0].Cells[1].Value);
    }

    [Fact]
    public void PreviousPage_FormattedColumns_RestoresSameCellsWithFormats()
    {
        // Arrange
        var table = BuildProfile();
        var first = table.Rows[0].Cells;
        table.Columns[1].OctetFormat = Windows1251;
        table.NextPage();
        table.Columns[2].OctetFormat = Utf8;

        // Act
        table.PreviousPage();

        // Assert
        Assert.Same(table.Rows[0].Cells[1], first[1]);
        Assert.Equal("Я", first[1].Value);
        Assert.Equal("Я", first[2].Value);
    }

    [Fact]
    public void OctetFormat_UnrepresentableText_PreservesHexAndOtherColumns()
    {
        // Arrange
        var table = BuildProfile();
        var first = table.Rows[0].Cells;
        table.Columns[2].OctetFormat = Utf8;

        // Act
        table.Columns[1].OctetFormat = Ascii;

        // Assert
        Assert.Contains("не представимо", first[1].Value);
        Assert.EndsWith("DF", first[1].Value);
        Assert.Equal("Я", first[2].Value);
    }

    [Fact]
    public void OctetFormat_ResetToHex_PreservesSourceBytes()
    {
        // Arrange
        var (captures, buffer) = CreateProfile();
        var codec = new AxdrCodec();
        byte[] original = codec.EncodeBytes(buffer);
        var table = ProfileGenericTableBuilder.Build(captures, buffer, []);
        var first = table.Rows[0].Cells;
        table.Columns[0].OctetFormat = Ascii;
        table.Columns[1].OctetFormat = Windows1251;
        table.Columns[2].OctetFormat = Utf8;

        // Act
        table.Columns[0].OctetFormat = OctetStringFormat.Hex;
        table.Columns[1].OctetFormat = OctetStringFormat.Hex;

        // Assert
        Assert.True(first[0].Value == ClockHex, "Changing column formats altered the profile's source bytes");
        Assert.Equal("DF", first[1].Value);
        Assert.Equal("Я", first[2].Value);
        Assert.Equal(codec.EncodeBytes(buffer), original);
    }

    [Fact]
    public void OctetFormat_ColumnWithoutCaptureMetadata_FormatsOctets()
    {
        // Arrange
        var noCaptures = ProfileGenericTableBuilder.Build(DlmsValue.Array(),
            DlmsValue.Array(DlmsValue.Structure(DlmsValue.Octets([0xDF]))), []);

        // Act
        noCaptures.Columns[0].OctetFormat = Windows1251;

        // Assert
        Assert.True(noCaptures.Columns[0].HasOctets, "An extra column without capture metadata could not select an encoding");
        Assert.Equal("Я", noCaptures.Rows[0].Cells[0].Value);
    }

    [Fact]
    public void Build_EmptyBuffer_DisablesOctetFormatSelectors()
    {
        // Arrange
        var (captures, _) = CreateProfile();

        // Act
        var empty = ProfileGenericTableBuilder.Build(captures, DlmsValue.Array(), []);

        // Assert
        Assert.Equal(0, empty.RowCount);
        Assert.True(empty.Columns.All(column => !column.HasOctets), "An empty profile exposed encoding selectors without octet values");
    }

    [Fact]
    public void OctetFormat_CompactArray_FormatsRows()
    {
        // Arrange
        var compact = new DlmsValue(DlmsType.CompactArray, new DlmsCompactArray(
            new(DlmsType.Structure, [CompactTypeDescription.Scalar(DlmsType.OctetString)]),
            [DlmsValue.Structure(DlmsValue.Octets([0xDF]))]));
        var compactTable = ProfileGenericTableBuilder.Build(DlmsValue.Array(), compact, []);

        // Act
        compactTable.Columns[0].OctetFormat = Windows1251;

        // Assert
        Assert.Equal("Я", compactTable.Rows[0].Cells[0].Value);
    }

    [Fact]
    public void NextPage_OctetsOnlyOnLaterPage_ExposesColumnFormat()
    {
        // Arrange
        var lateOctets = ProfileGenericTableBuilder.Build(DlmsValue.Array(), DlmsValue.Array(
            Enumerable.Range(0, 101).Select(index => DlmsValue.Structure(index < 100 ? DlmsValue.Null
                : DlmsValue.Octets([0xDF]))).ToArray()), []);
        lateOctets.Columns[0].OctetFormat = Windows1251;

        // Act
        lateOctets.NextPage();

        // Assert
        Assert.True(lateOctets.Columns[0].HasOctets, "Octets appearing only on later pages did not enable column encoding selection");
        Assert.Equal("Я", lateOctets.Rows[0].Cells[0].Value);
    }

    private static ProfileTableViewModel BuildProfile()
    {
        var (captures, buffer) = CreateProfile();
        return ProfileGenericTableBuilder.Build(captures, buffer, []);
    }

    private static (DlmsValue Captures, DlmsValue Buffer) CreateProfile()
    {
        DlmsValue Capture(ushort classId, byte obis) => DlmsValue.Structure(
            DlmsValue.Number(DlmsType.UInt16, classId), DlmsValue.Octets([0, 0, obis, 0, 0, 255]),
            DlmsValue.Number(DlmsType.Int8, 2), DlmsValue.Number(DlmsType.UInt16, 0));
        var captures = DlmsValue.Array(Capture(CosemClassIds.Clock, 1), Capture(1, 2), Capture(1, 3), Capture(1, 4));
        var clock = DlmsValue.Octets(Convert.FromHexString(ClockHex));
        var sourceRows = Enumerable.Range(0, 101).Select(index => index == 50 ? DlmsValue.Structure(clock)
            : DlmsValue.Structure(clock,
                index == 51 ? DlmsValue.Number(DlmsType.UInt8, 7)
                : index == 52 ? DlmsValue.Octets([0, 0xFF])
                : index == 53 ? DlmsValue.Octets([]) : DlmsValue.Octets([0xDF]),
                DlmsValue.Octets([0xD0, 0xAF]), DlmsValue.Number(DlmsType.UInt16, 42))).ToArray();
        var buffer = DlmsValue.Array(sourceRows);
        return (captures, buffer);
    }
}
