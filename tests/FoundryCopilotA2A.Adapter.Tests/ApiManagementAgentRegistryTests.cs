using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class ApiManagementAgentRegistryTests
{
    [Fact]
    public async Task DiscoversPublicA2AAgentCardsAndCachesTheResolvedRuntime()
    {
        var handler = new ApimHandler();
        using var client = new HttpClient(handler);
        var registry = CreateRegistry(client);

        var agents = await registry.GetAgentsAsync(CancellationToken.None);
        var resolved = await registry.TryResolveAsync("apim-weather-api", CancellationToken.None);

        var agent = Assert.Single(agents);
        Assert.Equal("apim-weather-api", agent.Id);
        Assert.Equal("Weather specialist", agent.DisplayName);
        Assert.Equal("apiManagement", agent.Provider);
        Assert.NotNull(resolved);
        Assert.Equal("weather-api", resolved.ApiId);
        Assert.Equal(
            "https://gateway.example/agents/weather/a2a",
            resolved.Endpoint.AbsoluteUri);
        Assert.Equal(1, handler.AgentCardRequests);
        Assert.All(
            handler.ManagementAuthorizations,
            authorization => Assert.Equal("Bearer management-token", authorization));
    }

    [Fact]
    public async Task ExplicitSubscriptionKeyApiIsRejected()
    {
        var handler = new ApimHandler(subscriptionRequired: true);
        using var client = new HttpClient(handler);
        var registry = CreateRegistry(client, ["weather-api"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.GetAgentsAsync(CancellationToken.None));

        Assert.Contains("requires a subscription key", exception.Message);
        Assert.Equal(0, handler.AgentCardRequests);
    }

    [Fact]
    public async Task InvokerForwardsTheDelegatedTokenToTheDiscoveredRuntime()
    {
        var handler = new ApimHandler();
        using var client = new HttpClient(handler);
        var registry = CreateRegistry(client);
        var invoker = new ApiManagementA2AInvoker(
            registry,
            new StubHttpClientFactory(client));
        var updates = new List<CopilotInvocationUpdate>();

        await foreach (var update in invoker.StreamAsync(
            "Will it rain?",
            new A2ARequestMetadata
            {
                AgentId = "apim-weather-api",
                ContextId = "context",
                MessageId = "message",
                UserId = "tenant|user",
                PayloadHash = "hash",
                BearerToken = "delegated-token"
            },
            CancellationToken.None))
        {
            updates.Add(update);
        }

        var result = Assert.Single(updates);
        Assert.Equal("Sunny", result.Text);
        Assert.Equal("Bearer delegated-token", handler.RuntimeAuthorization);
        Assert.Contains("\"method\":\"SendMessage\"", handler.RuntimeBody);
        Assert.Contains("Will it rain?", handler.RuntimeBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterAttributesDiscoveredAgentRepliesToTheAgentCardName(bool withCitations)
    {
        using var outboundClient = new HttpClient(new ApimHandler(withCitations: withCitations));
        var registry = CreateRegistry(outboundClient);
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ApiManagementAgentRegistry>();
                services.AddSingleton(registry);
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(outboundClient));
            }));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = "weather",
                method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER",
                        messageId = "weather",
                        contextId = "weather-context",
                        parts = new[] { new { text = "Will it rain?" } }
                    }
                }
            })
        };
        request.Headers.Add("A2A-Version", "1.0");
        request.Headers.Add(AdapterConstants.AgentHeaderName, "apim-weather-api");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var part = body.RootElement.GetProperty("result").GetProperty("message").GetProperty("parts")[0];
        Assert.Equal("Responding agent: Weather specialist\n\nSunny", part.GetProperty("text").GetString());
        var metadata = part.GetProperty("metadata");
        Assert.Equal("apim-weather-api", metadata.GetProperty("agentId").GetString());
        Assert.Equal("Weather specialist", metadata.GetProperty("agentName").GetString());
        var citations = A2AResponseText.Read(body.RootElement).Citations;
        if (withCitations)
        {
            Assert.Equal(CitationTests.Sample().Sources[0], Assert.Single(citations!.Sources));
        }
        else
        {
            Assert.Null(citations);
        }
    }

    private static ApiManagementAgentRegistry CreateRegistry(
        HttpClient client,
        string[]? apiIds = null) =>
        new(
            new StubCredential(),
            new StubHttpClientFactory(client),
            Options.Create(new ApiManagementDiscoveryOptions
            {
                Enabled = true,
                SubscriptionId = "00000000-0000-0000-0000-000000000000",
                ResourceGroup = "resource-group",
                ServiceName = "gateway",
                ApiIds = apiIds ?? []
            }),
            TimeProvider.System,
            NullLogger<ApiManagementAgentRegistry>.Instance);

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("management-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ApimHandler(bool subscriptionRequired = false, bool withCitations = false) : HttpMessageHandler
    {
        public int AgentCardRequests { get; private set; }

        public List<string?> ManagementAuthorizations { get; } = [];

        public string? RuntimeAuthorization { get; private set; }

        public string RuntimeBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "management.azure.com")
            {
                ManagementAuthorizations.Add(request.Headers.Authorization?.ToString());
                if (uri.AbsolutePath.EndsWith("/apis", StringComparison.Ordinal))
                {
                    return Json(
                        $$"""
                        {
                          "value": [{
                            "name": "weather-api",
                            "properties": {
                              "displayName": "Weather API",
                              "path": "agents/weather",
                              "isCurrent": true,
                              "subscriptionRequired": {{subscriptionRequired.ToString().ToLowerInvariant()}}
                            }
                          }]
                        }
                        """);
                }

                return Json(
                    """{"properties":{"gatewayUrl":"https://gateway.example"}}""");
            }

            if (uri.AbsolutePath.EndsWith(
                "/.well-known/agent-card.json",
                StringComparison.Ordinal))
            {
                AgentCardRequests++;
                return Json(
                    """
                    {
                      "name": "Weather specialist",
                      "url": "https://gateway.example/agents/weather/a2a"
                    }
                    """);
            }

            RuntimeAuthorization = request.Headers.Authorization?.ToString();
            RuntimeBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (withCitations)
            {
                return Json(CitationTests.UpstreamResponse("Sunny"));
            }
            return Json(
                """
                {
                  "jsonrpc": "2.0",
                  "id": "response",
                  "result": {
                    "message": {
                      "parts": [{ "text": "Sunny" }]
                    }
                  }
                }
                """);
        }

        private static HttpResponseMessage Json(string value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            };
    }
}
