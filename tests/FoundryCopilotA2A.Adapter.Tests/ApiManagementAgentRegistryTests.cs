using System.Collections.Concurrent;
using System.Diagnostics;
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
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AdapterTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
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
        Assert.Contains("\"method\":\"SendStreamingMessage\"", handler.RuntimeBody);
        Assert.Contains("text/event-stream", handler.RuntimeAccept);
        Assert.Contains("Will it rain?", handler.RuntimeBody);

        var invocation = Assert.Single(
            activities,
            activity =>
                activity.DisplayName == "apim.a2a.invoke" &&
                Equals(activity.GetTagItem("apim.api.id"), "weather-api"));
        var traceActivities = activities
            .Where(activity => activity.TraceId == invocation.TraceId)
            .ToArray();
        var resolve = Assert.Single(
            traceActivities,
            activity => activity.DisplayName == "apim.discovery.resolve");
        var refresh = Assert.Single(
            traceActivities,
            activity => activity.DisplayName == "apim.discovery.refresh");

        Assert.Equal(ActivityStatusCode.Ok, invocation.Status);
        Assert.Equal(invocation.SpanId, resolve.ParentSpanId);
        Assert.Equal("refreshed", resolve.GetTagItem("apim.discovery.cache.result"));
        Assert.Equal(1, refresh.GetTagItem("apim.discovery.candidate.count"));
        Assert.Equal(1, refresh.GetTagItem("apim.discovery.agent.count"));
        Assert.Contains(
            traceActivities,
            activity =>
                activity.DisplayName == "apim.discovery.acquire_arm_token" &&
                activity.ParentSpanId == refresh.SpanId);
        Assert.Contains(
            traceActivities,
            activity =>
                activity.DisplayName == "apim.discovery.read_management" &&
                activity.ParentSpanId == refresh.SpanId);
        Assert.Contains(
            traceActivities,
            activity =>
                activity.DisplayName == "apim.discovery.read_agent_cards" &&
                activity.ParentSpanId == refresh.SpanId);
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

    [Fact]
    public async Task RequestsUseTheSnapshotAfterTheRefreshIntervalWithoutRefreshing()
    {
        var handler = new ApimHandler();
        using var client = new HttpClient(handler);
        var time = new ManualTimeProvider();
        var registry = CreateRegistry(client, timeProvider: time);

        await registry.RefreshAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(600));
        var resolved = await registry.TryResolveAsync("apim-weather-api", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(1, handler.AgentCardRequests);
    }

    [Fact]
    public async Task FailedBackgroundRefreshKeepsTheLastSnapshotUntilMaxStale()
    {
        var handler = new ApimHandler();
        using var client = new HttpClient(handler);
        var time = new ManualTimeProvider();
        var registry = CreateRegistry(client, timeProvider: time);
        await registry.RefreshAsync(CancellationToken.None);

        handler.FailManagement = true;
        time.Advance(TimeSpan.FromSeconds(400));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RefreshAsync(CancellationToken.None));
        Assert.NotNull(await registry.TryResolveAsync("apim-weather-api", CancellationToken.None));

        // Past MaxStaleSeconds the request refreshes itself and reports the failure.
        time.Advance(TimeSpan.FromSeconds(600));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.TryResolveAsync("apim-weather-api", CancellationToken.None));
    }

    [Fact]
    public async Task AgentCardsAreReadConcurrentlyWithinTheLimitInStableOrder()
    {
        var handler = new ApimHandler(apiCount: 6, cardDelay: TimeSpan.FromMilliseconds(100));
        using var client = new HttpClient(handler);
        var registry = CreateRegistry(client, maxConcurrentCardReads: 3);

        var agents = await registry.GetAgentsAsync(CancellationToken.None);

        Assert.Equal(6, agents.Count);
        Assert.Equal(6, handler.AgentCardRequests);
        Assert.InRange(handler.MaxConcurrentCardReads, 2, 3);
        Assert.Equal(
            agents.Select(agent => agent.DisplayName).Order(StringComparer.OrdinalIgnoreCase),
            agents.Select(agent => agent.DisplayName));
    }

    [Fact]
    public async Task InvokerStreamsAnswerDeltasAndSuppressesTheRepeatedFinalAnswer()
    {
        const string stream =
            """
            data: {"jsonrpc":"2.0","id":"1","result":{"task":{"id":"t","contextId":"c","status":{"state":"TASK_STATE_SUBMITTED"}}}}

            data: {"jsonrpc":"2.0","id":"1","result":{"artifactUpdate":{"artifact":{"artifactId":"a","parts":[{"text":"Checking radar...","metadata":{"isInformative":true}}]},"append":false}}}

            data: {"jsonrpc":"2.0","id":"1","result":{"artifactUpdate":{"artifact":{"artifactId":"a","parts":[{"text":"Sun"}]},"append":true}}}

            data: {"jsonrpc":"2.0","id":"1","result":{"artifactUpdate":{"artifact":{"artifactId":"a","parts":[{"text":"ny"}]},"append":true,"lastChunk":true}}}

            data: {"jsonrpc":"2.0","id":"1","result":{"task":{"id":"t","artifacts":[{"artifactId":"a","parts":[{"text":"Sunny"}]}],"status":{"state":"TASK_STATE_COMPLETED"}}}}

            data: {"jsonrpc":"2.0","id":"1","result":{"statusUpdate":{"status":{"state":"TASK_STATE_COMPLETED"}}}}

            """;
        var handler = new ApimHandler(runtimeEventStream: stream);
        using var client = new HttpClient(handler);
        var invoker = new ApiManagementA2AInvoker(
            CreateRegistry(client),
            new StubHttpClientFactory(client));

        var updates = await CollectAsync(invoker);

        Assert.Equal(["Checking radar...", "Sun", "ny"], updates.Select(update => update.Text));
        Assert.True(updates[0].IsInformative);
        Assert.All(updates.Skip(1), update => Assert.False(update.IsInformative));
        Assert.Contains("\"method\":\"SendStreamingMessage\"", handler.RuntimeBody);
    }

    [Theory]
    [InlineData("""data: {"jsonrpc":"2.0","id":"1","result":{"statusUpdate":{"status":{"state":"TASK_STATE_FAILED","message":{"parts":[{"text":"backend down"}]}}}}}""", "backend down")]
    [InlineData("""data: {"jsonrpc":"2.0","id":"1","error":{"code":-32603,"message":"boom"}}""", "boom")]
    [InlineData("""data: {"jsonrpc":"2.0","id":"1","result":{"statusUpdate":{"status":{"state":"TASK_STATE_COMPLETED"}}}}""", "no text response")]
    public async Task InvokerSurfacesStreamFailures(string streamEvent, string expected)
    {
        var handler = new ApimHandler(runtimeEventStream: streamEvent + "\n\n");
        using var client = new HttpClient(handler);
        var invoker = new ApiManagementA2AInvoker(
            CreateRegistry(client),
            new StubHttpClientFactory(client));

        var exception = await Assert.ThrowsAsync<AdapterRequestException>(() => CollectAsync(invoker));

        Assert.Contains(expected, exception.Message);
    }

    private static async Task<List<CopilotInvocationUpdate>> CollectAsync(ApiManagementA2AInvoker invoker)
    {
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

        return updates;
    }

    private static ApiManagementAgentRegistry CreateRegistry(
        HttpClient client,
        string[]? apiIds = null,
        TimeProvider? timeProvider = null,
        int maxConcurrentCardReads = 4) =>
        new(
            new StubCredential(),
            new StubHttpClientFactory(client),
            Options.Create(new ApiManagementDiscoveryOptions
            {
                Enabled = true,
                SubscriptionId = "00000000-0000-0000-0000-000000000000",
                ResourceGroup = "resource-group",
                ServiceName = "gateway",
                ApiIds = apiIds ?? [],
                RefreshSeconds = 300,
                MaxStaleSeconds = 900,
                MaxConcurrentCardReads = maxConcurrentCardReads
            }),
            timeProvider ?? TimeProvider.System,
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

    private sealed class ApimHandler(
        bool subscriptionRequired = false,
        bool withCitations = false,
        int apiCount = 1,
        string? runtimeEventStream = null,
        TimeSpan cardDelay = default) : HttpMessageHandler
    {
        private int _agentCardRequests;
        private int _activeCardReads;
        private int _maxConcurrentCardReads;

        public int AgentCardRequests => _agentCardRequests;

        public int MaxConcurrentCardReads => _maxConcurrentCardReads;

        public bool FailManagement { get; set; }

        public List<string?> ManagementAuthorizations { get; } = [];

        public string? RuntimeAuthorization { get; private set; }

        public string RuntimeBody { get; private set; } = string.Empty;

        public string? RuntimeAccept { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "management.azure.com")
            {
                if (FailManagement)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                lock (ManagementAuthorizations)
                {
                    ManagementAuthorizations.Add(request.Headers.Authorization?.ToString());
                }
                if (uri.AbsolutePath.EndsWith("/apis", StringComparison.Ordinal))
                {
                    var apis = Enumerable.Range(0, apiCount).Select(index =>
                        $$"""
                        {
                          "name": "{{ApiName(index)}}",
                          "properties": {
                            "displayName": "Weather API",
                            "path": "agents/{{ApiPath(index)}}",
                            "isCurrent": true,
                            "subscriptionRequired": {{subscriptionRequired.ToString().ToLowerInvariant()}}
                          }
                        }
                        """);
                    return Json($$"""{ "value": [{{string.Join(",", apis)}}] }""");
                }

                return Json(
                    """{"properties":{"gatewayUrl":"https://gateway.example"}}""");
            }

            if (uri.AbsolutePath.EndsWith(
                "/.well-known/agent-card.json",
                StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _agentCardRequests);
                var active = Interlocked.Increment(ref _activeCardReads);
                int observed;
                do
                {
                    observed = _maxConcurrentCardReads;
                }
                while (active > observed &&
                       Interlocked.CompareExchange(ref _maxConcurrentCardReads, active, observed) != observed);
                try
                {
                    if (cardDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(cardDelay, cancellationToken);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeCardReads);
                }

                var path = uri.AbsolutePath.Split('/')[2];
                var index = path == "weather" ? 0 : int.Parse(path["weather".Length..]);
                var name = index == 0 ? "Weather specialist" : $"Weather specialist {index}";
                return Json(
                    $$"""
                    {
                      "name": "{{name}}",
                      "url": "https://gateway.example/agents/{{path}}/a2a"
                    }
                    """);
            }

            RuntimeAuthorization = request.Headers.Authorization?.ToString();
            RuntimeAccept = request.Headers.Accept.ToString();
            RuntimeBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (runtimeEventStream is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(runtimeEventStream, Encoding.UTF8, "text/event-stream")
                };
            }
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

        private static string ApiName(int index) => index == 0 ? "weather-api" : $"weather-api-{index}";

        private static string ApiPath(int index) => index == 0 ? "weather" : $"weather{index}";
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
