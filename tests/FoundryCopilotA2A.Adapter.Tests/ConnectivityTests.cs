using System.Net;
using System.Net.Http.Json;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class ConnectivityTests
{
    [Theory]
    [InlineData("https://demo-5099.euw.devtunnels.ms/", true)]
    [InlineData("https://gateway.example.test/agents", false)]
    [InlineData("https://devtunnels.ms.attacker.test", false)]
    [InlineData("http://localhost:5099", false)]
    public void ReportsOnlyConfiguredPublicUrl(string url, bool tunnel)
    {
        var result = ConnectivityDiagnostics.Create(
            new AdapterOptions { PublicBaseUrl = url }, new AuthenticationOptions());

        Assert.Equal(url.TrimEnd('/'), result.PublicBaseUrl);
        Assert.Equal(tunnel, result.IsDevTunnel);
        Assert.Null(result.ConfigurationError);
    }

    [Theory]
    [InlineData("https://user:secret@example.test")]
    [InlineData("https://example.test?token=secret")]
    [InlineData("https://example.test#secret")]
    [InlineData("not a URL")]
    [InlineData("file:///secret")]
    public void WithholdsUnsafeConfiguration(string url)
    {
        var result = ConnectivityDiagnostics.Create(
            new AdapterOptions { PublicBaseUrl = url }, new AuthenticationOptions());

        Assert.Null(result.PublicBaseUrl);
        Assert.False(result.IsDevTunnel);
        Assert.NotNull(result.ConfigurationError);
        Assert.DoesNotContain("secret", result.ConfigurationError);
    }

    [Fact]
    public async Task MockDiagnosticsWorkWithoutAzureAndAreNotCached()
    {
        await using var factory = new A2AAdapterFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/connectivity");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = await response.Content.ReadFromJsonAsync<ConnectivityDiagnostics>();
        Assert.NotNull(result);
        Assert.Equal("Mock", result.Backend);
        Assert.False(result.AuthenticationEnabled);
        Assert.Equal("https://adapter.test", result.PublicBaseUrl);
    }

    [Fact]
    public async Task ConfigurationRequiresAuthenticationOnProtectedAdapters()
    {
        await using var factory = new AuthenticatedAdapterFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync("/api/connectivity");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var authenticated = factory.CreateAuthenticatedClient(Caller.Victim);
        using var response = await authenticated.GetAsync("/api/connectivity");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ConnectivityDiagnostics>();
        Assert.NotNull(result);
        Assert.True(result.AuthenticationEnabled);
    }
}
