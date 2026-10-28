using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class WorkflowInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationUsesOneScopeForExposureAndOptionalPreauthorization(bool preauthorize)
    {
        var scopeId = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            EntraCommands.BuildAdapterRegistrationPatch("application", scopeId, preauthorize));
        var root = document.RootElement;
        Assert.Equal("api://application", root.GetProperty("identifierUris")[0].GetString());
        Assert.True(root.GetProperty("isFallbackPublicClient").GetBoolean());
        var api = root.GetProperty("api");
        Assert.Equal(2, api.GetProperty("requestedAccessTokenVersion").GetInt32());
        var scope = Assert.Single(api.GetProperty("oauth2PermissionScopes").EnumerateArray());
        Assert.Equal(scopeId, scope.GetProperty("id").GetGuid());
        Assert.Equal("access_as_user", scope.GetProperty("value").GetString());
        Assert.True(scope.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(preauthorize, api.TryGetProperty("preAuthorizedApplications", out var applications));
        if (preauthorize)
        {
            var application = Assert.Single(applications.EnumerateArray());
            Assert.Equal(scopeId, application.GetProperty("delegatedPermissionIds")[0].GetGuid());
        }
    }

    [Fact]
    public void ConnectionAddressEscapesEachUrlSegmentWithoutChangingItsResourceId()
    {
        var address = new FoundryConnectionAddress("subscription", "resource group", "account", "project", "connection");

        Assert.Contains("/resourceGroups/resource group/", address.ResourceId);
        Assert.Contains("/resourceGroups/resource%20group/", address.ManagementUrl("v1"));
        Assert.EndsWith("/connections/connection?api-version=v1", address.ManagementUrl("v1"));
    }

    [Fact]
    public void SingleBackendDoesNotAcquireSwitchingSettings()
    {
        var backend = HubCommands.ReadBackend(CommandArguments.Parse(
            ["--backend-url", "https://adapter.example/"]));

        Assert.Equal("https://adapter.example", backend.Url);
        Assert.Null(backend.Mode);
        Assert.Null(backend.AppServiceUrl);
        Assert.Null(backend.DevTunnelUrl);
    }

    [Fact]
    public void SwitchableBackendRetainsBothDestinationsAndSelectedMode()
    {
        var backend = HubCommands.ReadBackend(CommandArguments.Parse(
        [
            "--app-service-backend-url", "https://adapter.example/",
            "--dev-tunnel-backend-url", "https://adapter.devtunnels.ms/",
            "--backend-mode", "devtunnel"
        ]));

        Assert.Equal("https://adapter.example", backend.Url);
        Assert.Equal(backend.Url, backend.AppServiceUrl);
        Assert.Equal("https://adapter.devtunnels.ms", backend.DevTunnelUrl);
        Assert.Equal("devtunnel", backend.Mode);
    }

    [Theory]
    [InlineData("--app-service-backend-url", "https://adapter.example")]
    [InlineData("--dev-tunnel-backend-url", "https://adapter.devtunnels.ms")]
    [InlineData("--backend-mode", "appservice")]
    public void PartialSwitchingConfigurationIsRejected(string option, string value)
    {
        Assert.Throws<CliException>(() =>
            HubCommands.ReadBackend(CommandArguments.Parse([option, value])));
        Assert.Throws<CliException>(() => HubCommands.ReadBackend(CommandArguments.Parse(
            ["--backend-url", "https://adapter.example", option, value])));
    }

    [Fact]
    public void OriginParsingPreservesFirstOccurrenceAndExistingNormalization()
    {
        var origins = HubCommands.ReadAllowedOrigins(CommandArguments.Parse(
        [
            "--allowed-origins",
            " https://EXAMPLE.test/ ,https://example.test,http://localhost:5173/,,https://other.test/"
        ]));

        Assert.Equal(["https://example.test", "http://localhost:5173", "https://other.test"], origins);
        Assert.Empty(HubCommands.ReadAllowedOrigins(CommandArguments.Parse([])));
        Assert.Throws<CliException>(() => HubCommands.ReadAllowedOrigins(
            CommandArguments.Parse(["--allowed-origins", "ftp://example.test"])));
    }
}
