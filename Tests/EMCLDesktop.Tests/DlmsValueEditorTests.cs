using EMCL.Core.Codecs;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;
using EMCLDesktop.Services;
using Xunit;

namespace EMCLDesktop.Tests;

public class DlmsValueEditorTests
{
    [Theory]
    [InlineData(DlmsType.Int8, "-128")]
    [InlineData(DlmsType.UInt8, "255")]
    [InlineData(DlmsType.Int16, "-32768")]
    [InlineData(DlmsType.UInt16, "65535")]
    [InlineData(DlmsType.Int32, "-2147483648")]
    [InlineData(DlmsType.UInt32, "4294967295")]
    [InlineData(DlmsType.Int64, "-9223372036854775808")]
    [InlineData(DlmsType.UInt64, "18446744073709551615")]
    [InlineData(DlmsType.Enum, "255")]
    [InlineData(DlmsType.Boolean, "false")]
    [InlineData(DlmsType.Null, "null")]
    [InlineData(DlmsType.Float32, "1.25")]
    [InlineData(DlmsType.Float64, "-2.5e30")]
    [InlineData(DlmsType.BitString, "101010101")]
    [InlineData(DlmsType.OctetString, "01 00 FF")]
    [InlineData(DlmsType.OctetString, "")]
    [InlineData(DlmsType.Utf8String, "  Значение\n ")]
    [InlineData(DlmsType.VisibleString, "  text  ")]
    [InlineData(DlmsType.DateTime, "07E80B02060A1E0000800000")]
    [InlineData(DlmsType.Date, "07E80B0206")]
    [InlineData(DlmsType.Time, "0A1E0000")]
    [InlineData(DlmsType.CompactArray, "13120400010002")]
    [InlineData(DlmsType.Structure, "[{\"type\":\"Int8\",\"value\":-3},{\"type\":\"Array\",\"value\":[{\"type\":\"UInt64\",\"value\":18446744073709551615},{\"type\":\"OctetString\",\"value\":\"00FF\"}]}]")]
    public void Format_ParsedValue_RoundTripsAxdrBytes(DlmsType type, string text)
    {
        // Arrange
        var codec = new AxdrCodec();
        var value = DlmsValueEditor.Parse(type, text);

        // Act
        var formatted = DlmsValueEditor.Format(value);

        // Assert
        var reparsed = DlmsValueEditor.Parse(type, formatted);
        Assert.Equal(codec.EncodeBytes(value), codec.EncodeBytes(reparsed));
    }

    [Theory]
    [InlineData(DlmsType.Int8, "128")]
    [InlineData(DlmsType.UInt8, "-1")]
    [InlineData(DlmsType.UInt16, "65536")]
    [InlineData(DlmsType.Int32, "2147483648")]
    [InlineData(DlmsType.UInt64, "18446744073709551616")]
    [InlineData(DlmsType.Boolean, "2")]
    [InlineData(DlmsType.BitString, "102")]
    [InlineData(DlmsType.OctetString, "F")]
    [InlineData(DlmsType.Date, "00")]
    [InlineData(DlmsType.VisibleString, "Привет")]
    [InlineData(DlmsType.Float32, "NaN")]
    [InlineData(DlmsType.Float64, "Infinity")]
    [InlineData(DlmsType.Float64, "1,25")]
    [InlineData(DlmsType.Null, "")]
    [InlineData(DlmsType.Array, "[{\"type\":\"UInt8\",\"value\":256}]")]
    [InlineData(DlmsType.Array, "[{\"type\":\"Missing\",\"value\":1}]")]
    [InlineData(DlmsType.CompactArray, "1101")]
    [InlineData(DlmsType.CompactArray, "1312040001000200")]
    public void Parse_InvalidInput_ThrowsFormatException(DlmsType type, string text)
    {
        // Arrange
        Func<DlmsValue> parse = () => DlmsValueEditor.Parse(type, text);

        // Act
        var exception = Record.Exception(parse);

        // Assert
        Assert.IsType<FormatException>(exception);
    }

    [Theory]
    [InlineData("Windows-1251", "Привет", "CFF0E8E2E5F2")]
    [InlineData("UTF-8", "Привет ☀", "D09FD180D0B8D0B2D0B5D18220E29880")]
    [InlineData("ASCII", " ABC \n", "20414243200A")]
    public void Parse_TextOctets_EncodesExpectedBytesAndPreservesWhitespace(string encoding, string input, string hex)
    {
        // Arrange
        var format = OctetStringFormat.Options.Single(option => option.Label == encoding);

        // Act
        var value = DlmsValueEditor.Parse(DlmsType.OctetString, input, octetFormat: format);

        // Assert
        Assert.Equal(DlmsType.OctetString, value.Type);
        Assert.Equal(Convert.FromHexString(hex), value.Bytes.ToArray());
        Assert.Equal(input, format.Format(value.Bytes.AsSpan()));
    }

    [Fact]
    public void Parse_UnencodableAscii_ThrowsFormatException()
    {
        // Arrange
        var ascii = OctetStringFormat.Options.Single(format => format.Label == "ASCII");

        // Act
        var exception = Record.Exception(() => DlmsValueEditor.Parse(DlmsType.OctetString, "Я", octetFormat: ascii));

        // Assert
        Assert.IsType<FormatException>(exception);
    }
}
