using EMCL.Core.Objects;
using EMCL.Core.Codecs;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;
using EMCLDesktop.Services;

internal static class ProfileTableChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Profile table: " + message);
            checks++;
        }

        DlmsValue Capture(ushort classId, byte obis) => DlmsValue.Structure(
            DlmsValue.Number(DlmsType.UInt16, classId), DlmsValue.Octets([0, 0, obis, 0, 0, 255]),
            DlmsValue.Number(DlmsType.Int8, 2), DlmsValue.Number(DlmsType.UInt16, 0));
        var captures = DlmsValue.Array(Capture(CosemClassIds.Clock, 1), Capture(1, 2), Capture(1, 3), Capture(1, 4));
        const string clockHex = "07EA0B090110141EFF800000";
        var clock = DlmsValue.Octets(Convert.FromHexString(clockHex));
        var sourceRows = Enumerable.Range(0, 101).Select(index => index == 50 ? DlmsValue.Structure(clock)
            : DlmsValue.Structure(clock,
                index == 51 ? DlmsValue.Number(DlmsType.UInt8, 7)
                : index == 52 ? DlmsValue.Octets([0, 0xFF])
                : index == 53 ? DlmsValue.Octets([]) : DlmsValue.Octets([0xDF]),
                DlmsValue.Octets([0xD0, 0xAF]), DlmsValue.Number(DlmsType.UInt16, 42))).ToArray();
        var buffer = DlmsValue.Array(sourceRows);
        var codec = new AxdrCodec();
        byte[] original = codec.EncodeBytes(buffer);
        var table = ProfileGenericTableBuilder.Build(captures, buffer, []);
        var first = table.Rows[0].Cells;
        var windows1251 = OctetStringFormat.Options.Single(format => format.Label == "Windows-1251");
        var utf8 = OctetStringFormat.Options.Single(format => format.Label == "UTF-8");
        var ascii = OctetStringFormat.Options.Single(format => format.Label == "ASCII");

        Check(table.Columns.Select(column => column.HasOctets).SequenceEqual([true, true, true, false]),
            "Encoding selectors were not limited to columns containing octet strings");
        Check(table.Columns[1].OctetFormats.SequenceEqual(OctetStringFormat.ReadOptions),
            "The column did not expose the supported read formats");
        Check(table.Columns[0].OctetFormat == OctetStringFormat.DlmsDateTime &&
            first[0].Value == "2026-11-09 16:20:30 · пн · смещение UTC не задано · статус 0x00",
            "The clock column lost its default date-time display");
        Check(first[1].Value == "DF" && first[2].Value == "D0AF" && first[3].Value == "42" && first[3].HasNumber && first[3].NumericHexValue == "0x002A",
            "Column defaults changed raw octets or numbers");
        int changed = 0, unrelatedChanged = 0;
        first[1].PropertyChanged += (_, e) => { if (e.PropertyName == "Value") changed++; };
        first[2].PropertyChanged += (_, e) => { if (e.PropertyName == "Value") unrelatedChanged++; };
        table.Columns[1].OctetFormat = windows1251;
        Check(first[1].Value == "Я" && changed == 1 && unrelatedChanged == 0 && first[2].Value == "D0AF",
            "Changing a column did not notify cells or affected another column");
        Check(table.Rows[50].Cells.Skip(1).All(cell => cell.Value == "—") &&
            table.Rows[51].Cells[1].Value == "7" && table.Rows[51].Cells[1].NumericHexValue == "0x07",
            "A column encoding changed missing cells or a numeric value in a mixed column");
        Check(table.Rows[52].Cells[1].Value.Contains("не представимо") &&
            table.Rows[52].Cells[1].Value.EndsWith("00FF") && table.Rows[53].Cells[1].Value == "",
            "Binary or empty octets were lost on decoding");
        table.NextPage();
        Check(table.Rows.Count == 1 && table.Rows[0].Number == 101 && table.Rows[0].Cells[1].Value == "Я" &&
            table.Columns[1].OctetFormat == windows1251,
            "The column encoding was not applied to rows on another page");
        table.Columns[2].OctetFormat = utf8;
        Check(table.Rows[0].Cells[2].Value == "Я" && table.Rows[0].Cells[1].Value == "Я",
            "Two columns could not use independent encodings");
        table.PreviousPage();
        Check(ReferenceEquals(first[1], table.Rows[0].Cells[1]) && first[1].Value == "Я" && first[2].Value == "Я",
            "Returning to a page discarded its cells or column formats");
        table.Columns[1].OctetFormat = ascii;
        Check(first[1].Value.Contains("не представимо") && first[1].Value.EndsWith("DF") && first[2].Value == "Я",
            "An invalid text conversion lost raw bytes or affected another column");
        table.Columns[0].OctetFormat = OctetStringFormat.Hex;
        table.Columns[1].OctetFormat = OctetStringFormat.Hex;
        Check(first[0].Value == clockHex && first[1].Value == "DF" && first[2].Value == "Я" &&
            original.SequenceEqual(codec.EncodeBytes(buffer)),
            "Changing column formats altered the profile's source bytes");

        var noCaptures = ProfileGenericTableBuilder.Build(DlmsValue.Array(),
            DlmsValue.Array(DlmsValue.Structure(DlmsValue.Octets([0xDF]))), []);
        noCaptures.Columns[0].OctetFormat = windows1251;
        Check(noCaptures.Columns[0].HasOctets && noCaptures.Rows[0].Cells[0].Value == "Я",
            "An extra column without capture metadata could not select an encoding");
        var empty = ProfileGenericTableBuilder.Build(captures, DlmsValue.Array(), []);
        Check(empty.RowCount == 0 && empty.Columns.All(column => !column.HasOctets),
            "An empty profile exposed encoding selectors without octet values");

        var compact = new DlmsValue(DlmsType.CompactArray, new DlmsCompactArray(
            new(DlmsType.Structure, [CompactTypeDescription.Scalar(DlmsType.OctetString)]),
            [DlmsValue.Structure(DlmsValue.Octets([0xDF]))]));
        var compactTable = ProfileGenericTableBuilder.Build(DlmsValue.Array(), compact, []);
        compactTable.Columns[0].OctetFormat = windows1251;
        Check(compactTable.Rows[0].Cells[0].Value == "Я", "Compact profile rows did not use their column format");
        var lateOctets = ProfileGenericTableBuilder.Build(DlmsValue.Array(), DlmsValue.Array(
            Enumerable.Range(0, 101).Select(index => DlmsValue.Structure(index < 100 ? DlmsValue.Null
                : DlmsValue.Octets([0xDF]))).ToArray()), []);
        lateOctets.Columns[0].OctetFormat = windows1251;
        lateOctets.NextPage();
        Check(lateOctets.Columns[0].HasOctets && lateOctets.Rows[0].Cells[0].Value == "Я",
            "Octets appearing only on later pages did not enable column encoding selection");
        return checks;
    }
}
