using System.Buffers;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading.Channels;
using EMCL.Client.Association;
using EMCL.Client.Sessions;
using EMCL.Composition;
using EMCL.Core.Access;
using EMCL.Core.Codecs;
using EMCL.Core.Objects;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Protocol.Acse;
using EMCL.Protocol.Apdu;
using EMCL.Protocol.Axdr;
using EMCL.Transport.Channels;
using EMCLDesktop.Services;
using EMCLDesktop.ViewModels;

internal static class AttributeWriteChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Attribute write: " + message);
            count++;
        }
        void Invalid(DlmsType type, string text)
        {
            try { DlmsValueEditor.Parse(type, text); }
            catch (FormatException) { count++; return; }
            throw new Exception($"Editor accepted invalid {type}: {text}");
        }
        var codec = new AxdrCodec();
        foreach (var (type, text) in new (DlmsType, string)[]
        {
            (DlmsType.Int8, "-128"), (DlmsType.UInt8, "255"), (DlmsType.Int16, "-32768"),
            (DlmsType.UInt16, "65535"), (DlmsType.Int32, "-2147483648"), (DlmsType.UInt32, "4294967295"),
            (DlmsType.Int64, "-9223372036854775808"), (DlmsType.UInt64, "18446744073709551615"),
            (DlmsType.Enum, "255"), (DlmsType.Boolean, "false"), (DlmsType.Null, "null"),
            (DlmsType.Float32, "1.25"), (DlmsType.Float64, "-2.5e30"),
            (DlmsType.BitString, "101010101"), (DlmsType.OctetString, "01 00 FF"),
            (DlmsType.OctetString, ""), (DlmsType.Utf8String, "  Значение\n "),
            (DlmsType.VisibleString, "  text  "), (DlmsType.DateTime, "07E80B02060A1E0000800000"),
            (DlmsType.Date, "07E80B0206"), (DlmsType.Time, "0A1E0000"),
            (DlmsType.CompactArray, "13120400010002"),
            (DlmsType.Structure, "[{\"type\":\"Int8\",\"value\":-3},{\"type\":\"Array\",\"value\":[{\"type\":\"UInt64\",\"value\":18446744073709551615},{\"type\":\"OctetString\",\"value\":\"00FF\"}]}]")
        })
        {
            var value = DlmsValueEditor.Parse(type, text);
            var reparsed = DlmsValueEditor.Parse(type, DlmsValueEditor.Format(value));
            Check(codec.EncodeBytes(value).SequenceEqual(codec.EncodeBytes(reparsed)), $"Round trip lost {type}");
        }
        foreach (var (type, text) in new (DlmsType, string)[]
        {
            (DlmsType.Int8, "128"), (DlmsType.UInt8, "-1"), (DlmsType.UInt16, "65536"),
            (DlmsType.Int32, "2147483648"), (DlmsType.UInt64, "18446744073709551616"),
            (DlmsType.Boolean, "2"), (DlmsType.BitString, "102"), (DlmsType.OctetString, "F"),
            (DlmsType.Date, "00"), (DlmsType.VisibleString, "Привет"), (DlmsType.Float32, "NaN"),
            (DlmsType.Float64, "Infinity"), (DlmsType.Float64, "1,25"), (DlmsType.Null, ""),
            (DlmsType.Array, "[{\"type\":\"UInt8\",\"value\":256}]"),
            (DlmsType.Array, "[{\"type\":\"Missing\",\"value\":1}]"),
            (DlmsType.CompactArray, "1101"), (DlmsType.CompactArray, "1312040001000200")
        }) Invalid(type, text);

        var attr = new AttributeViewModel(2, "value", null, "", true);
        Check(attr.WriteType is null && !attr.CanUseReadValue, "Unknown type was guessed");
        var large = DlmsValue.Octets(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        attr.RememberReadValue(large);
        attr.ToggleWriteEditor();
        Check(!attr.IsWriteEditorOpen, "Editor opened without write access");
        attr.IsWriteEnabled = true;
        attr.ToggleWriteEditor();
        Check(attr.IsWriteEditorOpen &&
            DlmsValueEditor.Parse(attr.WriteType!.Value, attr.WriteText).Bytes.SequenceEqual(large.Bytes),
            "Editor copied a shortened display value");
        attr.WriteText = "draft";
        attr.RememberReadValue(DlmsValue.Octets([1, 2]));
        Check(attr.WriteText == "draft", "A read overwrote the draft");
        attr.WriteText = "";
        attr.ToggleWriteEditor();
        attr.ToggleWriteEditor();
        Check(attr.IsWriteEditorOpen && attr.WriteText == "", "Reopening replaced an intentionally empty draft");
        attr.UseReadValue();
        Check(attr.WriteText == "0102", "Editor did not copy the latest read value on request");
        attr.ForgetReadValue();
        Check(!attr.CanUseReadValue, "Stale pre-write value remained copyable");

        var windows1251 = OctetStringFormat.Options.Single(format => format.Label == "Windows-1251");
        var utf8 = OctetStringFormat.Options.Single(format => format.Label == "UTF-8");
        var ascii = OctetStringFormat.Options.Single(format => format.Label == "ASCII");
        foreach (var (format, input, hex) in new[]
        {
            (windows1251, "Привет", "CFF0E8E2E5F2"),
            (utf8, "Привет ☀", "D09FD180D0B8D0B2D0B5D18220E29880"),
            (ascii, " ABC \n", "20414243200A")
        })
        {
            var value = DlmsValueEditor.Parse(DlmsType.OctetString, input, octetFormat: format);
            Check(value.Type == DlmsType.OctetString && value.Bytes.SequenceEqual(Convert.FromHexString(hex)) &&
                format.Format(value.Bytes.AsSpan()) == input, $"{format} octets lost bytes or whitespace");
        }
        Check(utf8.Format([0xC0, 0x80]) is null && ascii.Format([0xFF]) is null &&
            windows1251.Format([0]) is null, "Binary octets were rendered as replacement or invisible control characters");
        try
        {
            DlmsValueEditor.Parse(DlmsType.OctetString, "Я", octetFormat: ascii);
            throw new Exception("Attribute write: ASCII accepted an unencodable character");
        }
        catch (FormatException) { count++; }

        var octets = new AttributeViewModel(2, "value", null, "", true) { Value = "CFF0E8E2E5F2", IsWriteEnabled = true };
        octets.RememberReadValue(DlmsValue.Octets(Convert.FromHexString(octets.Value)));
        Check(octets.HasReadOctets && octets.DisplayedValue == octets.Value, "OctetString did not default to HEX");
        var previews = octets.ReadOctetPreviews;
        Check(previews.Select(preview => preview.Format).SequenceEqual(OctetStringFormat.ReadOptions) &&
            previews.Single(preview => preview.Format == OctetStringFormat.Hex).Text == octets.Value &&
            previews.Single(preview => preview.Format == windows1251).Text == "Привет" &&
            previews.Single(preview => preview.Format == ascii).Text == "Значение не представимо как текст" &&
            octets.ReadOctetFormat == OctetStringFormat.Hex, "The format list did not preview all options without selecting them");
        octets.ReadOctetFormat = ascii;
        Check(octets.DisplayedValue.Contains(octets.Value), "Invalid text preview did not fall back to HEX");
        octets.ReadOctetFormat = windows1251;
        octets.ToggleWriteEditor();
        Check(octets.DisplayedValue == "Привет" && octets.WriteText == "Привет" && octets.WriteOctetFormat == windows1251,
            "Editor did not start with the selected OctetString representation");
        var draftPreviews = octets.WriteOctetPreviews;
        Check(draftPreviews.Single(preview => preview.Format == OctetStringFormat.Hex).Text == octets.Value &&
            draftPreviews.Single(preview => preview.Format == windows1251).Text == "Привет",
            "The combined draft field did not preview its bytes in every encoding");
        Check(!octets.TryChangeWriteOctetFormat(utf8) && octets.WriteText == "Привет" && octets.WriteOctetFormat == windows1251,
            "An invalid format change replaced the draft");
        Check(octets.TryChangeWriteOctetFormat(OctetStringFormat.Hex) && octets.WriteText == octets.Value,
            "Changing the draft format changed its bytes");
        Check(octets.TryChangeWriteOctetFormat(utf8) == false && octets.WriteOctetFormat == OctetStringFormat.Hex,
            "An unrepresentable draft changed the combined field encoding");
        octets.WriteText = "D0AF";
        Check(draftPreviews.Single(preview => preview.Format == utf8).Text == "Я" &&
            octets.TryChangeWriteOctetFormat(utf8) && octets.WriteText == "Я",
            "Typing bytes did not refresh the dropdown or convert the full draft");
        octets.WriteText = "Привет";
        Check(ReferenceEquals(octets.WriteOctetPreviews, draftPreviews) &&
            draftPreviews.Single(preview => preview.Format == OctetStringFormat.Hex).Text == "D09FD180D0B8D0B2D0B5D182" &&
            DlmsValueEditor.Parse(DlmsType.OctetString, octets.WriteText, octetFormat: octets.WriteOctetFormat)
                .Bytes.SequenceEqual(Convert.FromHexString("D09FD180D0B8D0B2D0B5D182")),
            "Typing in the selected UTF-8 encoding did not produce the expected write bytes");
        Check(octets.TryChangeWriteOctetFormat(OctetStringFormat.Hex), "Could not return the draft to HEX");
        octets.WriteText = "F";
        Check(draftPreviews.All(preview => preview.Text.Length == 0) &&
            !octets.TryChangeWriteOctetFormat(ascii) && octets.WriteText == "F",
            "An incomplete HEX draft left stale previews or was replaced on format change");
        octets.RememberReadValue(DlmsValue.Octets([0x41]));
        Check(ReferenceEquals(octets.ReadOctetPreviews, previews) &&
            previews.Single(preview => preview.Format == ascii).Text == "A",
            "Reading again did not refresh the previews in place");
        octets.ForgetReadValue();
        Check(!octets.HasReadOctets && previews.All(preview => preview.Text.Length == 0),
            "A forgotten value left stale format previews");

        const string dateHex = "07EA0A07030D1F30FF800000";
        const string datePreview = "2026-10-07 13:31:48 · ср · смещение UTC не задано · статус 0x00";
        var dateFormat = OctetStringFormat.DlmsDateTime;
        var dateValue = DlmsValue.Octets(Convert.FromHexString(dateHex));
        var dateOctets = new AttributeViewModel(2, "time", null, "", true) { Value = dateHex, IsWriteEnabled = true };
        dateOctets.RememberReadValue(dateValue);
        dateOctets.ReadOctetFormat = dateFormat;
        Check(dateOctets.ReadOctetPreviews.Single(preview => preview.Format == dateFormat).Text == datePreview &&
            dateOctets.DisplayedValue == datePreview,
            "DLMS date-time preview lost fields with unspecified hundredths and deviation");
        dateOctets.ToggleWriteEditor();
        Check(!dateOctets.WriteOctetFormats.Contains(dateFormat) && dateOctets.WriteOctetFormat == OctetStringFormat.Hex &&
            dateOctets.WriteType == DlmsType.OctetString && dateOctets.WriteText == dateHex,
            "A date-time preview altered the original OctetString in the write editor");
        Check(dateFormat.Format(Convert.FromHexString("07EA0A07030D1F300CFF4C80")) ==
            "2026-10-07 13:31:48.12 · ср · UTC+03:00 · статус 0x80" &&
            dateFormat.Format(Convert.FromHexString("07EA0A07030D1F3000014A00"))?.Contains("UTC-05:30") == true,
            "DLMS deviation did not convert to the local UTC offset");
        Check(dateFormat.Format(Convert.FromHexString("FFFF0AFEFF0DFF30FF800000"))?.StartsWith("****-10-[последний день] 13:**:48") == true &&
            dateFormat.Format(Convert.FromHexString("07EA021F030D1F30FF800000")) is null,
            "DLMS date-time preview did not handle special or invalid dates");

        const string calendarHex = "07EA0B0901";
        var calendarValue = DlmsValueEditor.Parse(DlmsType.Date, calendarHex);
        var calendar = new AttributeViewModel(2, "date", "Date", "", true)
            { Value = calendarHex, IsWriteEnabled = true };
        calendar.RememberReadValue(calendarValue);
        Check(calendar.HasReadFormats && !calendar.HasReadOctets &&
            calendar.ReadOctetFormat == OctetStringFormat.DlmsDate && calendar.DisplayedValue == "2026-11-09 · пн",
            "Date did not default to a readable calendar date");
        var calendarPreviews = calendar.ReadOctetPreviews;
        Check(calendarPreviews.Select(preview => preview.Format).SequenceEqual(OctetStringFormat.DateReadOptions) &&
            calendarPreviews[0].Text == calendarHex && calendarPreviews[1].Text == "2026-11-09 · пн" &&
            calendarPreviews[2].Text == "09.11.2026 · пн",
            "Date dropdown did not preview HEX, ISO and local dates together");
        calendar.ReadOctetFormat = OctetStringFormat.LocalDate;
        Check(calendar.DisplayedValue == "09.11.2026 · пн", "Date selection did not update the value field");
        calendar.ToggleWriteEditor();
        Check(calendar.WriteType == DlmsType.Date && calendar.WriteText == calendarHex &&
            codec.EncodeBytes(DlmsValueEditor.Parse(calendar.WriteType.Value, calendar.WriteText))
                .SequenceEqual(codec.EncodeBytes(calendarValue)),
            "A calendar preview altered the Date type or bytes copied for writing");
        calendar.ReadOctetFormat = OctetStringFormat.Hex;
        Check(calendar.DisplayedValue == calendarHex, "Date HEX view changed the original bytes");
        calendar.ReadOctetFormat = OctetStringFormat.LocalDate;
        calendar.RememberReadValue(DlmsValueEditor.Parse(DlmsType.Date, "07EA0B0A02"));
        Check(ReferenceEquals(calendar.ReadOctetPreviews, calendarPreviews) &&
            calendar.DisplayedValue == "10.11.2026 · вт" && calendarPreviews[1].Text == "2026-11-10 · вт",
            "Refreshing Date lost its format or left stale dropdown values");
        foreach (var (hex, iso, local) in new (string, string, string)[]
        {
            ("FFFFFFFFFF", "****-**-**", "**.**.****"),
            ("FFFF0AFEFF", "****-10-[последний день]", "[последний день].10.****"),
            ("07EAFEFDFF", "2026-[начало летнего времени]-[предпоследний день]",
                "[предпоследний день].[начало летнего времени].2026"),
            ("07E8021D04", "2024-02-29 · чт", "29.02.2024 · чт"),
            ("FFFF021DFF", "****-02-29", "29.02.****")
        })
            Check(OctetStringFormat.DlmsDate.Format(Convert.FromHexString(hex)) == iso &&
                OctetStringFormat.LocalDate.Format(Convert.FromHexString(hex)) == local,
                $"Date preview replaced a special DLMS field or rejected a valid leap day: {hex}");
        foreach (string hex in new[] { "", "07EA0B09", "07EA0B090100", "07E7021D03", "07EA000901", "07EA0D0901", "07EA0B0001", "07EA0B2001", "07EA0B0900", "07EA0B0908" })
            Check(OctetStringFormat.DlmsDate.Format(Convert.FromHexString(hex)) is null &&
                OctetStringFormat.LocalDate.Format(Convert.FromHexString(hex)) is null,
                $"An invalid Date was displayed as a calendar date: {hex}");
        calendar.RememberReadValue(DlmsValueEditor.Parse(DlmsType.Date, "07E7021D03"));
        Check(calendar.DisplayedValue.EndsWith("07E7021D03") && calendar.DisplayedValue.Contains("DLMS date"),
            "Invalid Date did not explain the problem and preserve its raw bytes");
        calendar.RememberReadValue(DlmsValue.Octets([0x41]));
        Check(calendar.ReadOctetFormat == OctetStringFormat.Hex &&
            calendar.ReadOctetPreviews.Select(preview => preview.Format).SequenceEqual(OctetStringFormat.ReadOptions),
            "Changing the response type left Date options on an OctetString");
        calendar.RememberReadValue(calendarValue);
        var refreshedCalendarPreviews = calendar.ReadOctetPreviews;
        calendar.ForgetReadValue();
        Check(!calendar.HasReadFormats && refreshedCalendarPreviews.All(preview => preview.Text.Length == 0),
            "Forgetting Date left a visible dropdown or stale date previews");
        var calendarTree = new AttributeViewModel(2, "dates", null, "", true);
        calendarTree.RememberReadValue(DlmsValue.Structure(calendarValue, DlmsValue.Array(calendarValue)));
        var calendarNodes = calendarTree.ReadValueNodes.Single().Children;
        var nestedCalendar = calendarNodes[1].Children.Single();
        calendarNodes[0].ReadOctetFormat = OctetStringFormat.LocalDate;
        Check(calendarNodes[0].HasReadFormats && calendarNodes[0].DisplayedValue == "09.11.2026 · пн" &&
            nestedCalendar.HasReadFormats && nestedCalendar.DisplayedValue == "2026-11-09 · пн" &&
            nestedCalendar.ReadOctetPreviews[0].Text == calendarHex,
            "Nested Date fields did not expose independent calendar formats");

        var compound = new AttributeViewModel(2, "value", null, "", true);
        var nested = DlmsValue.Array(DlmsValue.Structure(
            DlmsValue.Octets([0xDF]), DlmsValue.Array(DlmsValue.Octets([0xD0, 0xAF])), DlmsValue.Null, dateValue));
        compound.RememberReadValue(nested);
        var fields = compound.ReadValueNodes.Single().Children.Single().Children;
        var windowsLeaf = fields[0];
        var utf8Leaf = fields[1].Children.Single();
        Check(compound.HasReadValueTree && windowsLeaf.HasReadOctets && utf8Leaf.HasReadOctets &&
            fields[2].DisplayedValue == "null" && windowsLeaf.DisplayedValue == "DF" && utf8Leaf.DisplayedValue == "D0AF",
            "Nested values did not expose typed leaves with default HEX previews");
        Check(windowsLeaf.ReadOctetPreviews.Single(preview => preview.Format == windows1251).Text == "Я" &&
            utf8Leaf.ReadOctetPreviews.Single(preview => preview.Format == utf8).Text == "Я" &&
            !fields[2].HasReadOctets, "Nested fields did not expose their own format previews");
        windowsLeaf.ReadOctetFormat = windows1251;
        Check(windowsLeaf.DisplayedValue == "Я" && utf8Leaf.DisplayedValue == "D0AF", "Leaf formats were not independent");
        utf8Leaf.ReadOctetFormat = utf8;
        windowsLeaf.ReadOctetFormat = ascii;
        Check(utf8Leaf.DisplayedValue == "Я" && windowsLeaf.DisplayedValue.EndsWith("DF"),
            "Nested text preview lost encoding or HEX fallback");
        fields[3].ReadOctetFormat = dateFormat;
        utf8Leaf.ReadOctetFormat = dateFormat;
        Check(fields[3].ReadOctetPreviews.Single(preview => preview.Format == dateFormat).Text == datePreview &&
            fields[3].DisplayedValue == datePreview &&
            utf8Leaf.DisplayedValue.EndsWith("D0AF"), "Nested DLMS date-time preview or HEX fallback is missing");
        Check(compound.ShowReadValueTree, "Collection did not default to tree view");
        compound.UseTextValueView = true;
        Check(compound.HasReadValueTree && !compound.ShowReadValueTree, "Text view did not hide the tree");
        compound.UseTextValueView = false;
        Check(compound.ShowReadValueTree && compound.ReadValueNodes.Single().Children.Single().Children[0] == windowsLeaf,
            "Switching back did not preserve the tree nodes");
        compound.UseReadValue();
        Check(codec.EncodeBytes(DlmsValueEditor.Parse(compound.WriteType!.Value, compound.WriteText))
            .SequenceEqual(codec.EncodeBytes(nested)), "Tree display formats altered the value copied to the write editor");
        compound.RememberReadValue(DlmsValue.Array(Enumerable.Repeat(DlmsValue.Null, 101).ToArray()));
        var ranges = compound.ReadValueNodes.Single().Children;
        Check(ranges.Count == 2 && ranges[0].Children.Count == 100 && ranges[1].Label.StartsWith("[100]"),
            "A large array did not expose every original index");
        compound.ForgetReadValue();
        Check(!compound.HasReadValueTree && !compound.ShowReadValueTree, "A forgotten value left a stale tree");

        foreach (var (numericType, dec, hex) in new (DlmsType, string, string)[]
        {
            (DlmsType.Int8, "-128", "80"), (DlmsType.Int16, "-2", "FFFE"),
            (DlmsType.Int32, "-2", "FFFFFFFE"), (DlmsType.Int64, "-9223372036854775808", "8000000000000000"),
            (DlmsType.UInt8, "2", "02"), (DlmsType.UInt16, "2", "0002"),
            (DlmsType.UInt32, "2", "00000002"), (DlmsType.UInt64, "18446744073709551615", "FFFFFFFFFFFFFFFF"),
            (DlmsType.DeltaInt8, "-2", "FE"), (DlmsType.DeltaInt16, "-2", "FFFE"),
            (DlmsType.DeltaInt32, "-2", "FFFFFFFE"), (DlmsType.DeltaUInt8, "7", "07"),
            (DlmsType.DeltaUInt16, "7", "0007"), (DlmsType.DeltaUInt32, "7", "00000007"),
            (DlmsType.Float32, "1.25", "3FA00000"), (DlmsType.Float64, "-1.25", "BFF4000000000000"),
            (DlmsType.Bcd, "18", "12"), (DlmsType.Enum, "255", "FF")
        })
        {
            var value = DlmsValueEditor.Parse(numericType, dec);
            var numericAttribute = new AttributeViewModel(2, "number", null, "", true) { Value = dec };
            numericAttribute.RememberReadValue(value);
            compound.RememberReadValue(DlmsValue.Structure(value, value));
            var numbers = compound.ReadValueNodes.Single().Children;
            var table = ProfileGenericTableBuilder.Build(DlmsValue.Array(),
                DlmsValue.Array(DlmsValue.Structure(value, DlmsValue.Structure(value, value))), []);
            var cells = table.Rows.Single().Cells;
            Check(numericAttribute.HasReadNumber && numericAttribute.DisplayedValue == dec &&
                numericAttribute.NumericHexValue == $"0x{hex}" &&
                numbers.All(number => number.HasReadNumber && number.DisplayedValue == dec && number.NumericHexValue == $"0x{hex}") &&
                cells[0].HasNumber && cells[0].Value == dec && cells[0].NumericHexValue == $"0x{hex}" &&
                cells[1].Value.Contains(dec + Environment.NewLine) && cells[1].Value.Contains($"0x{hex}") &&
                !cells[1].Value.Contains("DEC ") && !cells[1].Value.Contains("HEX "),
                $"{numericType} did not separate decimal and complete HEX values without labels");
            numericAttribute.UseReadValue();
            Check(numericAttribute.WriteText == dec && numericAttribute.DisplayedValue == dec,
                $"{numericType} display changed the value copied to the editor");
        }

        for (byte mode = 0; mode <= 6; mode++)
        {
            var access = new AttributeAccess(2, mode, []);
            Check(MeterConnection.AllowsWrite(access, 1, ClientAuthentication.None) == (mode is 2 or 3), "v1 anonymous rights");
            Check(MeterConnection.AllowsWrite(access, 1, ClientAuthentication.High) == (mode is 2 or 3 or 5 or 6), "v1 authenticated rights");
        }
        for (int mode = 0; mode <= 255; mode++)
            Check(MeterConnection.AllowsWrite(new AttributeAccess(2, (byte)mode, []), 3, ClientAuthentication.High) ==
                (mode is 2 or 3), "v3 protected or readonly write allowed");

        await using (var settings = new MainViewModel { Host = "localhost", TcpPort = "4059" })
        await using (var restored = new MainViewModel())
        {
            var option = settings.ConformanceOptions.Single(o => o.Flag == DlmsConformance.BlockTransferWithSet);
            Check(option.IsSelected, "SET blocks are not enabled for new connections");
            foreach (bool enabled in new[] { false, true })
            {
                option.IsSelected = enabled;
                await using var json = new MemoryStream();
                await settings.SaveConfigurationAsync(json);
                json.Position = 0;
                await restored.LoadConfigurationAsync(json);
                Check(!restored.HasStatusError && restored.ConformanceOptions.Single(o =>
                    o.Flag == DlmsConformance.BlockTransferWithSet).IsSelected == enabled,
                    "Saved SET block capability was not restored");
            }
        }

        foreach (var outcome in new[] { "ok", "octet-text", "reject", "lost", "read-fails", "write-only", "no-get", "no-set", "no-tree",
            "boundary", "boundary-plus", "blocks-ok", "blocks-reject-first", "blocks-reject-middle", "blocks-reject-last",
            "blocks-lost", "blocks-no-support", "blocks-limit", "blocks-too-small", "blocks-bad-number", "blocks-bad-invoke",
            "blocks-bad-final", "blocks-cancel", "blocks-timeout", "cancel-before" })
        {
            var channel = new MeterChannel(outcome);
            var transferLimits = new ProtocolLimits { MaxBlockCount = outcome == "blocks-limit" ? 2 : 4096 };
            var session = new DlmsClientSession(channel, new ClientSessionOptions
            {
                Limits = transferLimits,
                RequestTimeout = TimeSpan.FromMilliseconds(outcome == "blocks-timeout" ? 250 : 2000),
                Association = new LnAssociationOptions { Authentication = ClientAuthentication.None }
            });
            await session.AssociateAsync();
            var name = LogicalName.Parse("1.0.0.2.0.255");
            var options = new MeterConnectionOptions("test", 1, "Wrapper", 16, 1, null, false, 1,
                "0.0.40.0.0.255", ClientAuthentication.None, "", "UTF-8", 1, 1, 3600, 1024,
                LnAssociationOptions.SupportedConformance, false, transferLimits);
            var meter = (MeterConnection)Activator.CreateInstance(typeof(MeterConnection), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [session, ClientAuthentication.None, LogicalName.Parse(options.AssociationObis), TimeSpan.FromHours(1), options], null)!;
            if (outcome != "no-tree")
            {
                var entry = DlmsValue.Structure(DlmsValue.Number(DlmsType.UInt16, 1), DlmsValue.Number(DlmsType.UInt8, 0),
                    DlmsValue.Octets([1, 0, 0, 2, 0, 255]), DlmsValue.Structure(DlmsValue.Array(
                        DlmsValue.Structure(DlmsValue.Number(DlmsType.Int8, 2),
                            DlmsValue.Number(DlmsType.Enum, outcome == "write-only" ? 2 : 3), DlmsValue.Null)), DlmsValue.Array()));
                var imported = new EmclBuilder().CreateImporter().Import(DlmsValue.Array(entry), "test", "test", 1);
                typeof(MeterConnection).GetProperty(nameof(MeterConnection.Association))!.SetValue(meter, imported);
            }
            await using var vm = new MainViewModel();
            typeof(MainViewModel).GetField("connection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, meter);
            void CancelWrite() => ((CancellationTokenSource)typeof(MainViewModel)
                .GetField("shutdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Cancel();
            channel.CancelWrite = CancelWrite;
            vm.IsConnected = true;
            var obj = new MeterObjectViewModel(name, 1, 0, "Data");
            var written = outcome == "octet-text" ? DlmsValue.Octets([0xDF]) :
                channel.UsesSmallPdu ? DlmsValue.Octets(Enumerable.Range(0, channel.PayloadLength).Select(i => (byte)i).ToArray()) :
                DlmsValue.Number(DlmsType.UInt16, 77);
            var draft = DlmsValueEditor.Format(written);
            var attribute = new AttributeViewModel(2, "value", null, "", true)
                { WriteType = written.Type, WriteText = draft, Value = "Old read" };
            attribute.RememberReadValue(DlmsValue.Number(DlmsType.UInt16, 11));
            obj.Attributes.Add(attribute);
            vm.Objects.Add(obj);
            vm.SelectedObject = obj;
            Check(attribute.IsWriteEnabled == (outcome is not ("no-set" or "no-tree")), "Write availability ignored live association");
            if (outcome == "octet-text")
            {
                Check(attribute.TryChangeWriteOctetFormat(windows1251), "Could not choose Windows-1251 for writing");
                attribute.ReadOctetFormat = windows1251;
                attribute.WriteText = draft = "Я";
            }
            if (outcome == "cancel-before") CancelWrite();
            await vm.WriteAttributeAsync(attribute);
            Check(!vm.IsBusy && attribute.WriteText == draft, "Write lost draft or busy state");
            bool unsent = outcome is "no-set" or "no-tree" or "blocks-no-support" or "blocks-limit" or "blocks-too-small" or "cancel-before";
            Check(channel.Sets == (unsent ? 0 : 1), "SET retried or bypassed a guard");
            if (outcome is "blocks-ok" or "boundary" or "boundary-plus")
            {
                Check(!vm.HasStatusError && channel.WritesApplied == 1 && channel.Gets == 1 &&
                    attribute.LastReadValue is not null && codec.EncodeBytes(attribute.LastReadValue).SequenceEqual(codec.EncodeBytes(written)),
                    "Large SET was not applied exactly once and read back");
                Check(channel.BlockRequests == (outcome == "boundary" ? 0 : outcome == "boundary-plus" ? 2 : 3),
                    "Incorrect block count or normal-PDU boundary");
            }
            if (outcome.StartsWith("blocks-reject"))
                Check(vm.HasStatusError && channel.WritesApplied == 0 && channel.Gets == 0 &&
                    attribute.WriteStatus.Contains("ReadWriteDenied") && vm.IsConnected,
                    "Block rejection lost meter result or faulted the connection");
            if (outcome is "blocks-lost" or "blocks-bad-final" or "blocks-bad-number" or "blocks-bad-invoke" or "blocks-cancel" or "blocks-timeout")
            {
                int expectedBlocks = outcome is "blocks-lost" or "blocks-bad-final" ? 3 :
                    outcome is "blocks-cancel" or "blocks-timeout" ? 2 : 1;
                int expectedApplied = outcome is "blocks-lost" or "blocks-bad-final" ? 1 : 0;
                Check(vm.HasStatusError && channel.WritesApplied == expectedApplied && channel.BlockRequests == expectedBlocks && channel.Gets == 0 &&
                    !vm.IsConnected && attribute.WriteStatus.Contains("не подтверждена") && attribute.LastReadValue is null,
                    "Missing final confirmation retried an applied SET or reported a definite outcome");
                if (outcome.StartsWith("blocks-bad-"))
                    Check(!attribute.WriteStatus.Contains("The SET outcome is unconfirmed"), "Protocol failure cause was hidden by the wrapper");
            }
            if (outcome is "blocks-no-support" or "blocks-limit" or "blocks-too-small")
                Check(vm.HasStatusError && channel.BlockRequests == 0 && channel.WritesApplied == 0 && vm.IsConnected &&
                    attribute.WriteStatus.Contains("не выполнена") && attribute.LastReadValue?.AsUInt64() == 11,
                    "A failed preflight sent SET or discarded the last read");
            if (outcome == "cancel-before")
                Check(!vm.HasStatusError && channel.Gets == 0 && vm.IsConnected && attribute.WriteStatus.Contains("до отправки") &&
                    attribute.LastReadValue?.AsUInt64() == 11, "Cancellation before sending was reported as an uncertain write");
            if (outcome == "ok")
                Check(channel.Gets == 1 && attribute.Value == $"78{Environment.NewLine}0x004E" && attribute.DisplayedValue == "78" && attribute.NumericHexValue == "0x004E" && !vm.HasStatusError,
                    "Write did not show the actual read-back value");
            if (outcome == "octet-text")
                Check(channel.Gets == 1 && attribute.Value == "DF" && attribute.DisplayedValue == "Я" && !vm.HasStatusError,
                    "SET sent an incorrect OctetString type or encoding");
            if (outcome == "reject")
                Check(vm.HasStatusError && channel.Gets == 0 && attribute.Value == "Old read" &&
                    attribute.WriteStatus.Contains("ReadWriteDenied"), "Refusal lost the device error or changed read value");
            if (outcome == "lost")
                Check(vm.HasStatusError && !vm.IsConnected && attribute.WriteStatus.Contains("не подтверждена"),
                    "Lost reply was shown as a definite failure or success");
            if (outcome == "read-fails")
                Check(vm.HasStatusError && attribute.WriteStatus.Contains("Запись подтверждена, но") &&
                    attribute.LastReadValue is null, "Read-back failure masked confirmed SET");
            if (outcome is "write-only" or "no-get")
                Check(channel.Gets == 0 && !vm.HasStatusError && attribute.Value.Contains("не считано"),
                    "Read-back was attempted without read access or negotiated GET");
        }
        return count;
    }

    private sealed class MeterChannel(string outcome) : IApduChannel
    {
        private readonly Channel<ReadOnlyMemory<byte>> replies = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly ApduCodec codec = new();
        public int Sets { get; private set; }
        public int Gets { get; private set; }
        public int BlockRequests { get; private set; }
        public int WritesApplied { get; private set; }
        public bool UsesSmallPdu => outcome.StartsWith("blocks-") || outcome.StartsWith("boundary");
        public int PayloadLength => outcome == "boundary" ? 49 : outcome == "boundary-plus" ? 50 : 140;
        public Action? CancelWrite { get; set; }
        private readonly MemoryStream blockData = new();
        private uint expectedBlock = 1;
        private DlmsValue? stored;

        private DlmsApdu? AcceptBlock(byte invoke, bool last, uint number, ImmutableArray<byte> data)
        {
            BlockRequests++;
            if (number != expectedBlock++) throw new Exception("SET block number repeated or skipped");
            blockData.Write(data.AsSpan());
            if (outcome == "blocks-bad-number") return new SetBlockResponse(invoke, number + 1);
            if (outcome == "blocks-bad-invoke") return new SetBlockResponse((byte)(invoke ^ 0x80), number);
            if (outcome is "blocks-cancel" or "blocks-timeout" && number == 2)
            {
                if (outcome == "blocks-cancel") CancelWrite!();
                return null;
            }
            if (outcome == "blocks-reject-first" && number == 1 ||
                outcome == "blocks-reject-middle" && number == 2 ||
                outcome == "blocks-reject-last" && last)
                return new SetBlockResponse(invoke, number, CosemResultCode.ReadWriteDenied);
            if (!last) return new SetBlockResponse(invoke, number);
            var decoded = new AxdrCodec().Decode(new ReadOnlySequence<byte>(blockData.ToArray()));
            if (decoded.Status != DecodeStatus.Done || decoded.Consumed != blockData.Length ||
                !decoded.Value!.Bytes.SequenceEqual(Enumerable.Range(0, PayloadLength).Select(i => (byte)i)))
                throw new Exception("SET blocks changed the encoded value");
            stored = decoded.Value;
            WritesApplied++;
            if (outcome == "blocks-lost") { replies.Writer.TryComplete(); return null; }
            if (outcome == "blocks-bad-final") return new SetBlockResponse(invoke, number);
            return new SetBlockResponse(invoke, number, CosemResultCode.Success);
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken = default)
        {
            DlmsApdu? response;
            int pduLimit = outcome == "blocks-too-small" ? 19 : UsesSmallPdu ? 64 : 1024;
            if (apdu.Span[0] == (byte)ApduTag.SetRequest && apdu.Length > pduLimit)
                throw new Exception("SET exceeded the server PDU");
            var decoded = codec.Decode(new ReadOnlySequence<byte>(apdu));
            switch (decoded.Value)
            {
                case AcseApdu { Kind: AcseKind.Aarq }:
                    var flags = LnAssociationOptions.SupportedConformance & ~DlmsConformance.PriorityManagement;
                    if (outcome == "no-get") flags &= ~DlmsConformance.Get;
                    if (outcome == "no-set") flags &= ~DlmsConformance.Set;
                    if (outcome == "blocks-no-support") flags &= ~DlmsConformance.BlockTransferWithSet;
                    response = AcseApdu.Aare(codec.EncodeBytes(new InitiateResponse(null, DlmsNegotiation.Version6, (uint)flags, (ushort)pduLimit)));
                    break;
                case AcseApdu { Kind: AcseKind.Rlrq }:
                    response = AcseApdu.Release(response: true);
                    break;
                case SetFirstBlockRequest first:
                    Sets++;
                    if (first.Operation.ClassId != 1 || first.Operation.LogicalName.ToString() != "1.0.0.2.0.255" || first.Operation.Index != 2)
                        throw new Exception("SET block target changed");
                    response = AcceptBlock(first.InvokeIdAndPriority, first.LastBlock, first.BlockNumber, first.Data);
                    break;
                case SetBlockRequest block:
                    response = AcceptBlock(block.InvokeIdAndPriority, block.LastBlock, block.BlockNumber, block.Data);
                    break;
                case LnRequest request:
                    if (request.Operation.Kind == CosemOperationKind.Write)
                    {
                        Sets++;
                        if (outcome is "boundary" or "octet-text")
                        {
                            stored = request.Operation.Value;
                            WritesApplied++;
                            response = new LnResponse(CosemOperationKind.Write, request.InvokeIdAndPriority, OperationResult.Ok());
                            break;
                        }
                        if (request.Operation.Value?.Type != DlmsType.UInt16 || request.Operation.Value.AsUInt64() != 77 ||
                            request.Operation.LogicalName.ToString() != "1.0.0.2.0.255" || request.Operation.Index != 2)
                            throw new Exception("SET target or wire value changed");
                        if (outcome == "lost") throw new IOException("Connection lost after sending SET");
                        response = new LnResponse(request.Operation.Kind, request.InvokeIdAndPriority,
                            outcome == "reject" ? new OperationResult(CosemResultCode.ReadWriteDenied) : OperationResult.Ok());
                    }
                    else
                    {
                        Gets++;
                        response = new LnResponse(request.Operation.Kind, request.InvokeIdAndPriority,
                            outcome == "read-fails" ? new OperationResult(CosemResultCode.ReadWriteDenied) :
                                OperationResult.Ok(stored ?? DlmsValue.Number(DlmsType.UInt16, 78)));
                    }
                    break;
                default: throw new Exception("Unexpected APDU in write checks");
            }
            if (response is not null) replies.Writer.TryWrite(codec.EncodeBytes(response));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken = default) =>
            await replies.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync() { replies.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
