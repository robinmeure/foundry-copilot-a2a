using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class AgentCardCorsTests(A2AAdapterFactory factory) : IClassFixture<A2AAdapterFactory>
{
    [Theory]
    [InlineData("/.well-known/agent-card.json")]
    [InlineData("/a2a-agents/mock/.well-known/agent-card.json")]
    [InlineData("/a2a-agents/mock/a2a/.well-known/agent-card.json")]
    [InlineData("/a2a-agents/mock/.well-known/agent.json")]
    [InlineData("/a2a-agents/mock/a2a/.well-known/agent.json")]
    public async Task PublicCardsAreReadableBeforeAuthoringPortalsAuthenticate(string path)
    {
        using var client = CreateClient();
        foreach (var origin in new[] { "https://copilotstudio.microsoft.com", "https://other-authoring.example" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            Assert.Contains("\"name\"", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("/.well-known/agent-card.json")]
    [InlineData("/a2a-agents/mock/.well-known/agent-card.json")]
    [InlineData("/a2a-agents/mock/a2a/.well-known/agent-card.json")]
    [InlineData("/a2a-agents/mock/.well-known/agent.json")]
    [InlineData("/a2a-agents/mock/a2a/.well-known/agent.json")]
    public async Task CardPreflightAllowsOnlyReadRequests(string path)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", "https://copilotstudio.microsoft.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "a2a-version,content-type");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("/a2a/copilot-studio", "POST")]
    [InlineData("/a2a-agents/mock/a2a", "POST")]
    [InlineData("/api/traces/00000000000000000000000000000001", "GET")]
    public async Task RuntimeAndTracesStillRequireTokensAndDoNotAllowAuthoringOrigins(string path, string method)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", "https://copilotstudio.microsoft.com");
        if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("/a2a/copilot-studio", "POST")]
    [InlineData("/a2a-agents/mock/a2a", "POST")]
    [InlineData("/api/agents", "GET")]
    [InlineData("/api/traces/00000000000000000000000000000001", "GET")]
    public async Task NonCardPreflightsKeepTheConfiguredSpaOriginAllowlist(string path, string method)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", "https://copilotstudio.microsoft.com");
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private HttpClient CreateClient() => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Adapter:AllowedOrigins:0", "http://localhost:5173");
        builder.UseSetting("Authentication:Enabled", "true");
        builder.UseSetting("Authentication:Authority", "https://login.microsoftonline.com/test-tenant/v2.0");
        builder.UseSetting("Authentication:Audience", "api://test-client");
    }).CreateClient();
}
