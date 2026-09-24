using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class PublicTunnelServiceTests {
    [Fact]
    public void Constructor_WithNullSettings_ThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() => new PublicTunnelService(null!));
    }

    [Fact]
    public async Task StartTunnelAsync_WithCustomDomain_SetsUrlAndRuns() {
        SettingsService settingsService = new(null);
        PublicTunnelService tunnelService = new(settingsService);

        string expectedDomain = "https://lora.mydomain.org";
        string url = await tunnelService.StartTunnelAsync(8420, "custom", expectedDomain);

        Assert.True(tunnelService.IsTunnelRunning);
        Assert.Equal(expectedDomain, url);
        Assert.Equal(expectedDomain, tunnelService.ActivePublicUrl);
        Assert.Equal("custom", tunnelService.TunnelType);

        await tunnelService.StopTunnelAsync();
        Assert.False(tunnelService.IsTunnelRunning);
    }

    [Fact]
    public async Task StartTunnelAsync_WhenAlreadyRunning_ReturnsActiveUrl() {
        SettingsService settingsService = new(null);
        PublicTunnelService tunnelService = new(settingsService);

        string customUrl = "https://test.customdomain.com";
        await tunnelService.StartTunnelAsync(8420, "custom", customUrl);

        string secondCall = await tunnelService.StartTunnelAsync(8420, "custom", "https://different.com");
        Assert.Equal(customUrl, secondCall);

        await tunnelService.StopTunnelAsync();
    }

    [Fact]
    public async Task StopTunnelAsync_WhenNotRunning_DoesNotThrow() {
        SettingsService settingsService = new(null);
        PublicTunnelService tunnelService = new(settingsService);

        var exception = await Record.ExceptionAsync(() => tunnelService.StopTunnelAsync());
        Assert.Null(exception);
    }

    [Fact]
    public void AppSettings_PublicTunnelProperties_DefaultsAreExpected() {
        AppSettings settings = new();

        Assert.False(settings.EnablePublicInternetTunnel);
        Assert.Equal("cloudflare", settings.PublicTunnelType);
        Assert.Empty(settings.PublicCustomDomainUrl);
        Assert.True(settings.RequireAuthForWebAccess);
    }

    [Fact]
    public void AppSettings_CustomServerSetup_PreservesDomainAndToken() {
        AppSettings settings = new() {
            ServerAccessToken = "super_secret_token_123",
            RequireAuthForWebAccess = true,
            PublicCustomDomainUrl = "https://lora.customserver.net"
        };

        Assert.Equal("super_secret_token_123", settings.ServerAccessToken);
        Assert.True(settings.RequireAuthForWebAccess);
        Assert.Equal("https://lora.customserver.net", settings.PublicCustomDomainUrl);
    }
}
