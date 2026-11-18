using System.Text;
using System.Text.Json.Nodes;
using EMCLDesktop.ViewModels;
using Xunit;

namespace EMCLDesktop.Tests;

public class ConfigurationCompatibilityTests
{
    [Fact]
    public async Task LoadConfigurationAsync_SavedHighGmacSettings_RestoresSecuritySettings()
    {
        // Arrange
        await using var source = CreateHighGmacViewModel();
        await using var json = new MemoryStream();
        await source.SaveConfigurationAsync(json);
        json.Position = 0;
        await using var restored = new MainViewModel();

        // Act
        await restored.LoadConfigurationAsync(json);

        // Assert
        Assert.True(restored.IsHighGmac, restored.Status);
        Assert.Equal("17", restored.GmacInitialCounter);
        Assert.Equal(source.GmacBlockCipherKey, restored.GmacBlockCipherKey);
        Assert.Equal(source.GmacServerSystemTitle, restored.GmacServerSystemTitle);
    }

    [Fact]
    public async Task SaveConfigurationAsync_HighGmacAuthentication_OmitsPassword()
    {
        // Arrange
        await using var source = CreateHighGmacViewModel();
        await using var json = new MemoryStream();

        // Act
        await source.SaveConfigurationAsync(json);

        // Assert
        var association = JsonNode.Parse(json.ToArray())!["association"]!;
        Assert.Equal("HighGMAC", association["authentication"]!.GetValue<string>());
        Assert.Null(association["password"]);
    }

    [Fact]
    public async Task Authentication_HighGmacSelected_EnablesGmacAndDisablesPassword()
    {
        // Arrange
        await using var viewModel = new MainViewModel();

        // Act
        viewModel.Authentication = "HighGMAC";

        // Assert
        Assert.True(viewModel.IsHighGmac);
        Assert.False(viewModel.HasPassword);
    }

    [Fact]
    public async Task LoadConfigurationAsync_NewerPartialConfiguration_PreservesUnspecifiedSettings()
    {
        // Arrange
        await using var viewModel = new MainViewModel
        {
            Transport = "TCP",
            Host = "old-host",
            TcpPort = "1234",
            Framing = "HDLC",
            ClientAddress = "48",
            Authentication = "HighGMAC",
            GmacAuthenticationKey = new string('1', 32),
            GmacInitialCounter = "100"
        };
        const string json = """
            { "format": "EMCLDesktop", "schemaVersion": 2,
              "channel": { "connection": { "host": "new-host", "futureField": true } },
              "association": { "logicalName": "0.0.40.0.1.255" }, "futureSection": {} }
            """;

        // Act
        await LoadAsync(viewModel, json);

        // Assert
        Assert.False(viewModel.HasStatusError, viewModel.Status);
        Assert.Equal("new-host", viewModel.Host);
        Assert.Equal("1234", viewModel.TcpPort);
        Assert.Equal("HDLC", viewModel.Framing);
        Assert.Equal("48", viewModel.ClientAddress);
        Assert.Equal("0.0.40.0.1.255", viewModel.AssociationObis);
        Assert.Equal("HighGMAC", viewModel.Authentication);
        Assert.Equal(new string('1', 32), viewModel.GmacAuthenticationKey);
        Assert.Equal("100", viewModel.GmacInitialCounter);
        Assert.Contains("версия формата отличается", viewModel.Status);
        Assert.Contains("незнакомые поля", viewModel.Status);
    }

    [Fact]
    public async Task LoadConfigurationAsync_MissingChannel_PreservesTransportSettings()
    {
        // Arrange
        await using var viewModel = new MainViewModel { Host = "new-host", Framing = "HDLC" };
        const string json = "{\"association\":{\"logicalName\":\"0.0.40.0.2.255\"}}";

        // Act
        await LoadAsync(viewModel, json);

        // Assert
        Assert.False(viewModel.HasStatusError, viewModel.Status);
        Assert.Equal("new-host", viewModel.Host);
        Assert.Equal("HDLC", viewModel.Framing);
        Assert.Equal("0.0.40.0.2.255", viewModel.AssociationObis);
    }

    [Fact]
    public async Task LoadConfigurationAsync_PartialFraming_PreservesOtherFramingSettings()
    {
        // Arrange
        await using var viewModel = new MainViewModel { Framing = "HDLC", ServerAddress = "12", UseHdlcLinkParameters = true };
        const string json = "{\"channel\":{\"framing\":{\"clientAddress\":32}}}";

        // Act
        await LoadAsync(viewModel, json);

        // Assert
        Assert.False(viewModel.HasStatusError, viewModel.Status);
        Assert.Equal("HDLC", viewModel.Framing);
        Assert.Equal("32", viewModel.ClientAddress);
        Assert.Equal("12", viewModel.ServerAddress);
        Assert.True(viewModel.UseHdlcLinkParameters);
    }

    private static MainViewModel CreateHighGmacViewModel() => new()
    {
        Transport = "Serial",
        SerialPortName = "/dev/test-serial",
        Authentication = "HighGMAC",
        GmacAuthenticationKey = new string('1', 32),
        GmacBlockCipherKey = new string('2', 32),
        GmacServerSystemTitle = "494A4B4C4D4E4F50",
        GmacInitialCounter = "17"
    };

    private static async Task LoadAsync(MainViewModel viewModel, string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await viewModel.LoadConfigurationAsync(stream);
    }
}
