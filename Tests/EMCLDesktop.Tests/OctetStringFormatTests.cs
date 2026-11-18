using EMCLDesktop.Services;
using Xunit;

namespace EMCLDesktop.Tests;

public class OctetStringFormatTests
{
    [Theory]
    [InlineData("UTF-8", "C080")]
    [InlineData("ASCII", "FF")]
    [InlineData("Windows-1251", "00")]
    public void Format_BinaryOrInvalidText_ReturnsNull(string encoding, string hex)
    {
        // Arrange
        var format = OctetStringFormat.Options.Single(option => option.Label == encoding);
        var bytes = Convert.FromHexString(hex);

        // Act
        var text = format.Format(bytes);

        // Assert
        Assert.Null(text);
    }

    [Fact]
    public void Format_DlmsDateTimeWithNegativeDeviation_ShowsPositiveUtcOffset()
    {
        // Arrange
        var bytes = Convert.FromHexString("07EA0A07030D1F300CFF4C80");

        // Act
        var text = OctetStringFormat.DlmsDateTime.Format(bytes);

        // Assert
        Assert.Equal("2026-10-07 13:31:48.12 · ср · UTC+03:00 · статус 0x80", text);
    }

    [Fact]
    public void Format_DlmsDateTimeWithPositiveDeviation_ShowsNegativeUtcOffset()
    {
        // Arrange
        var bytes = Convert.FromHexString("07EA0A07030D1F3000014A00");

        // Act
        var text = OctetStringFormat.DlmsDateTime.Format(bytes);

        // Assert
        Assert.NotNull(text);
        Assert.Contains("UTC-05:30", text);
    }

    [Fact]
    public void Format_DlmsDateTimeWithSpecialFields_PreservesSpecialValues()
    {
        // Arrange
        var bytes = Convert.FromHexString("FFFF0AFEFF0DFF30FF800000");

        // Act
        var text = OctetStringFormat.DlmsDateTime.Format(bytes);

        // Assert
        Assert.StartsWith("****-10-[последний день] 13:**:48", text);
    }

    [Fact]
    public void Format_InvalidDlmsDateTime_ReturnsNull()
    {
        // Arrange
        var bytes = Convert.FromHexString("07EA021F030D1F30FF800000");

        // Act
        var text = OctetStringFormat.DlmsDateTime.Format(bytes);

        // Assert
        Assert.Null(text);
    }

    [Theory]
    [InlineData("FFFFFFFFFF", "****-**-**", "**.**.****")]
    [InlineData("FFFF0AFEFF", "****-10-[последний день]", "[последний день].10.****")]
    [InlineData("07E8021D04", "2024-02-29 · чт", "29.02.2024 · чт")]
    [InlineData("FFFF021DFF", "****-02-29", "29.02.****")]
    [InlineData("07EAFEFDFF", "2026-[начало летнего времени]-[предпоследний день]", "[предпоследний день].[начало летнего времени].2026")]
    public void Format_ValidDlmsDate_PreservesCalendarFieldsInIsoAndLocalFormats(string hex, string iso, string local)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var isoDate = OctetStringFormat.DlmsDate.Format(bytes);
        var localDate = OctetStringFormat.LocalDate.Format(bytes);

        // Assert
        Assert.Equal(iso, isoDate);
        Assert.Equal(local, localDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("07EA0B09")]
    [InlineData("07EA0B090100")]
    [InlineData("07E7021D03")]
    [InlineData("07EA000901")]
    [InlineData("07EA0D0901")]
    [InlineData("07EA0B0001")]
    [InlineData("07EA0B2001")]
    [InlineData("07EA0B0900")]
    [InlineData("07EA0B0908")]
    public void Format_InvalidDlmsDate_ReturnsNullInIsoAndLocalFormats(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var isoDate = OctetStringFormat.DlmsDate.Format(bytes);
        var localDate = OctetStringFormat.LocalDate.Format(bytes);

        // Assert
        Assert.Null(isoDate);
        Assert.Null(localDate);
    }
}
