using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class A2AAdapterTests : IClassFixture<A2AAdapterFactory>
{
    private readonly A2AAdapterFactory _factory;
    private readonly HttpClient _client;

    public A2AAdapterTests(A2AAdapterFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AgentCardExposesJsonRpcEndpoint()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/agent-card.json");
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Specialist Agent Router", content);
        Assert.Contains("https://adapter.test/a2a/copilot-studio", content);
        Assert.Contains("JSONRPC", content);
        Assert.Contains("\"supportedInterfaces\"", content);
    }

    [Fact]
    public async Task AgentCardWithoutVersionHeaderSupportsFoundryPreview()
    {
        using var response = await _client.GetAsync("/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"url\":\"https://adapter.test/a2a/copilot-studio\"", content);
        Assert.Contains("\"protocolVersion\":\"0.3.0\"", content);
        Assert.Contains("\"preferredTransport\":\"JSONRPC\"", content);
        Assert.Contains("\"supportedInterfaces\"", content);
    }

    [Fact]
    public async Task AgentCatalogExposesNamesWithoutConnectionDetails()
    {
        using var response = await _client.GetAsync(AdapterConstants.AgentsPath);

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"defaultAgentId\":\"mock\"", content);
        Assert.Contains("\"displayName\":\"Mock Copilot Studio\"", content);
        Assert.Contains("\"provider\":\"copilotStudio\"", content);
        Assert.DoesNotContain("directConnectUrl", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"endpoint\"", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSecret", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfiguredFoundryAgentAppearsInCatalogWithoutItsEndpoint()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Foundry:Agents:web:Id", "web-research");
            builder.UseSetting("Foundry:Agents:web:DisplayName", "Foundry Web Research");
            builder.UseSetting(
                "Foundry:Agents:web:Endpoint",
                "https://account.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/a2a");
        }).CreateClient();

        using var response = await client.GetAsync(AdapterConstants.AgentsPath);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"id\":\"web-research\"", content);
        Assert.Contains("\"displayName\":\"Foundry Web Research\"", content);
        Assert.Contains("\"provider\":\"foundry\"", content);
        Assert.DoesNotContain("services.ai.azure.com", content);
    }

    [Fact]
    public async Task CatalogAndTargetSpecificRouteExposeConfiguredChain()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Foundry:Agents:web:Id", "web-research");
            builder.UseSetting("Foundry:Agents:web:DisplayName", "Foundry Web Research");
            builder.UseSetting(
                "Foundry:Agents:web:Endpoint",
                "https://account.services.ai.azure.com/api/projects/project/agents/agent/endpoint/protocols/a2a");
            builder.UseSetting("Foundry:Agents:web:ChainTargets:0", "mock");
        }).CreateClient();

        using var catalogResponse = await client.GetAsync(AdapterConstants.AgentsPath);
        var catalog = await catalogResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"chainTargets\":[\"mock\"]", catalog);

        using var cardResponse = await client.GetAsync(
            $"{AdapterConstants.ChainAgentBasePath("mock")}/.well-known/agent-card.json");
        var card = await cardResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, cardResponse.StatusCode);
        Assert.Contains("\"name\":\"Mock Copilot Studio\"", card);
        Assert.Contains(
            $"https://adapter.test{AdapterConstants.ChainAgentRuntimePath("mock")}",
            card);
        Assert.Contains(
            "\"protocolBinding\":\"JSONRPC\",\"protocolVersion\":\"1.0\"",
            card);

        // Remote callers resolve the card either as a sibling of the runtime or by appending
        // /.well-known/agent-card.json to the target URL. Both must return the same chain-bound
        // card, otherwise discovery falls back to the root card and the generic router route.
        using var targetRelativeCardResponse = await client.GetAsync(
            $"{AdapterConstants.ChainAgentRuntimePath("mock")}/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.OK, targetRelativeCardResponse.StatusCode);
        Assert.Equal(card, await targetRelativeCardResponse.Content.ReadAsStringAsync());

        // The card format version stays 0.3.0 while supportedInterfaces advertises the 1.0
        // binding, matching the shape the A2A library emits for the root card.
        Assert.Contains("\"protocolVersion\":\"0.3.0\"", card);
        Assert.Contains("\"supportsAuthenticatedExtendedCard\"", card);
        Assert.Contains("\"additionalInterfaces\"", card);

        using var routeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            AdapterConstants.ChainAgentRuntimePath("mock"))
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = NewId(),
                method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER",
                        parts = new[] { new { text = "chain route" } },
                        messageId = NewId(),
                        contextId = "ctx-chain-route"
                    }
                }
            })
        };
        routeRequest.Headers.Add("A2A-Version", "1.0");
        using var routeResponse = await client.SendAsync(routeRequest);
        var routeBody = await routeResponse.Content.ReadAsStringAsync();
        Assert.Contains("mock-copilot-studio", routeBody);
    }

    [Fact]
    public async Task ChainAgentCardAdvertisesOAuthSecuritySchemeWhenAuthenticationEnabled()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Adapter:PublicBaseUrl", "https://adapter.test");
            builder.UseSetting("Authentication:Enabled", "true");
            builder.UseSetting(
                "Authentication:Authority",
                "https://login.microsoftonline.com/test-tenant/v2.0");
            builder.UseSetting("Authentication:Audience", "api://test-client");
        }).CreateClient();

        using var response = await client.GetAsync(
            $"{AdapterConstants.ChainAgentBasePath("mock")}/.well-known/agent-card.json");
        var card = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A protected runtime that advertises no security scheme tells callers it is anonymous,
        // so they never attach a credential and never prompt the user to consent.
        Assert.Contains("\"securitySchemes\"", card);
        Assert.Contains("\"type\":\"oauth2\"", card);
        Assert.Contains("api://test-client/access_as_user", card);

        // Authority is the issuer form ending in /v2.0; the authorize and token endpoints hang
        // off the tenant root, so the version segment must not be duplicated.
        Assert.Contains(
            "https://login.microsoftonline.com/test-tenant/oauth2/v2.0/authorize",
            card);
        Assert.Contains(
            "https://login.microsoftonline.com/test-tenant/oauth2/v2.0/token",
            card);
        Assert.DoesNotContain("/v2.0/oauth2/v2.0/", card);
    }

    [Fact]
    public async Task ConfiguredBrowserOriginCanCallTheRuntime()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Adapter:AllowedOrigins:0", "http://localhost:5173");
        }).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/a2a/copilot-studio");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add(
            "Access-Control-Request-Headers",
            "authorization,content-type,a2a-version,x-copilot-agent");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "http://localhost:5173",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task JsonRpcMessageIsDelegatedToMockCopilotStudio()
    {
        var invoker = Invoker;
        var before = invoker.InvocationCount;

        using var response = await SendMessageAsync(NewId(), "ctx-1", "hello from Foundry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains($"mock-copilot-studio[{before + 1}]: hello from Foundry", content);
        Assert.Contains("ctx-1", content);
    }

    [Fact]
    public async Task RelayedConversationHistoryReachesTheDelegatedAgent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = NewId(),
                    method = "SendMessage",
                    @params = new
                    {
                        message = new
                        {
                            role = "ROLE_USER",
                            parts = new[] { new { text = "and the second one?" } },
                            messageId = NewId(),
                            contextId = "ctx-history",
                            metadata = new
                            {
                                history = new[]
                                {
                                    new { role = "user", text = "first question" },
                                    new { role = "assistant", text = "first answer" },
                                    // Unusable entries must be dropped instead of failing the turn.
                                    new { role = "system", text = "ignored" },
                                    new { role = "user", text = "" }
                                }
                            }
                        }
                    }
                },
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        request.Headers.Add("A2A-Version", "1.0");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("and the second one?", content);
        Assert.Contains("context: 2 earlier turns", content);
    }

    [Fact]
    public async Task A2AResponseLinksToACompleteSanitizedTrace()
    {
        const string prompt = "trace-body-must-not-be-exported";
        using var response = await SendMessageAsync(NewId(), "ctx-trace-view", prompt);

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues(AdapterConstants.TraceHeaderName, out var values));
        var traceId = Assert.Single(values);

        using var traceResponse = await _client.GetAsync(
            $"{AdapterConstants.TracesPath}/{traceId}");
        traceResponse.EnsureSuccessStatusCode();
        var trace = await traceResponse.Content.ReadFromJsonAsync<TraceSnapshot>();

        Assert.NotNull(trace);
        Assert.True(trace.Complete);
        Assert.Equal(traceId, trace.TraceId);
        Assert.Contains(trace.Spans, span => span.Name == "a2a.adapter.get_response");
        Assert.Contains(trace.Spans, span => span.Name == "copilot_studio.mock.invoke");
        Assert.DoesNotContain(
            prompt,
            JsonSerializer.Serialize(trace),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAgentSelectionIsRejectedWithoutCallingBackend()
    {
        var before = Invoker.InvocationCount;

        using var response = await SendMessageAsync(
            NewId(),
            "ctx-unknown-agent",
            "do not delegate",
            agentId: "not-configured");
        var content = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("not configured", content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Invoker.InvocationCount);
    }

    [Fact]
    public async Task AdapterEmitsSafeDomainMethodSpans()
    {
        const string prompt = "trace-content-must-not-be-exported";
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, "FoundryCopilotA2A.Adapter", StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (stoppedActivities)
                {
                    stoppedActivities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        using var response = await SendMessageAsync(NewId(), "ctx-tracing", prompt);

        response.EnsureSuccessStatusCode();
        string[] activityNames;
        string[] exportedValues;
        lock (stoppedActivities)
        {
            activityNames = stoppedActivities.Select(activity => activity.OperationName).ToArray();
            exportedValues = stoppedActivities
                .SelectMany(activity => activity.TagObjects)
                .Select(tag => tag.Value?.ToString() ?? string.Empty)
                .ToArray();
        }

        Assert.Contains("a2a.adapter.get_response", activityNames);
        Assert.Contains("a2a.idempotency.get_or_add", activityNames);
        Assert.Contains("copilot_studio.mock.invoke", activityNames);
        Assert.DoesNotContain(exportedValues, value => value.Contains(prompt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateMessageIdIsInvokedOnlyOnce()
    {
        var invoker = Invoker;
        var before = invoker.InvocationCount;
        var messageId = NewId();

        await SendMessageAsync(messageId, "ctx-idempotent", "perform one action");
        await SendMessageAsync(messageId, "ctx-idempotent", "perform one action");

        Assert.Equal(before + 1, invoker.InvocationCount);
    }

    [Fact]
    public async Task ConcurrentDuplicateMessageIdIsInvokedOnlyOnce()
    {
        var invoker = Invoker;
        var before = invoker.InvocationCount;
        var messageId = NewId();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => SendMessageAsync(messageId, "ctx-concurrent", "perform one concurrent action")));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(before + 1, invoker.InvocationCount);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task V03JsonRpcMessageIsDelegatedForFoundryPreview()
    {
        using var response = await SendV03MessageAsync(NewId(), "ctx-v03", "hello from Foundry preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("mock-copilot-studio", content);
        Assert.Contains("hello from Foundry preview", content);
        Assert.Contains("\"kind\":\"message\"", content);
    }

    /// <summary>
    /// Regression test for the confirmed disclosure defect: a caller who replays another
    /// caller's messageId with different content must never be served the cached response.
    /// </summary>
    [Fact]
    public async Task ReplayedMessageIdWithDifferentContentIsRejected()
    {
        var messageId = NewId();

        using var first = await SendMessageAsync(messageId, "ctx-replay", "VICTIM SECRET QUESTION");
        var firstContent = await first.Content.ReadAsStringAsync();
        Assert.Contains("VICTIM SECRET QUESTION", firstContent);

        using var second = await SendMessageAsync(messageId, "ctx-replay", "attacker replay");
        var secondContent = await second.Content.ReadAsStringAsync();

        Assert.DoesNotContain("VICTIM SECRET QUESTION", secondContent);

        // The refusal must also be diagnosable: a generic "no response events" error would hide
        // the real cause from the caller and from operators.
        using var document = JsonDocument.Parse(secondContent);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Contains("already been used with different content", error.GetProperty("message").GetString());
    }

    /// <summary>
    /// A replay must not become billable work on the delegated backend either.
    /// </summary>
    [Fact]
    public async Task ReplayedMessageIdWithDifferentContentDoesNotReachTheBackend()
    {
        var messageId = NewId();
        using var first = await SendMessageAsync(messageId, "ctx-no-backend", "original");
        first.EnsureSuccessStatusCode();

        var before = Invoker.InvocationCount;
        using var second = await SendMessageAsync(messageId, "ctx-no-backend", "attacker replay");

        Assert.Equal(before, Invoker.InvocationCount);
    }

    /// <summary>
    /// The cache key must include the conversation, so the same messageId under a different
    /// contextId is a genuinely different request rather than a cache hit.
    /// </summary>
    [Fact]
    public async Task SameMessageIdInDifferentContextIsNotDeduplicated()
    {
        var invoker = Invoker;
        var before = invoker.InvocationCount;
        var messageId = NewId();

        await SendMessageAsync(messageId, "ctx-alpha", "shared text");
        await SendMessageAsync(messageId, "ctx-beta", "shared text");

        Assert.Equal(before + 2, invoker.InvocationCount);
    }

    /// <summary>
    /// Routing matches a trailing slash, so replay protection must apply there too.
    /// </summary>
    [Fact]
    public async Task TrailingSlashDoesNotBypassIdempotency()
    {
        var invoker = Invoker;
        var before = invoker.InvocationCount;
        var messageId = NewId();

        await SendMessageAsync(messageId, "ctx-slash", "one action", path: AdapterConstants.RuntimePath + "/");
        await SendMessageAsync(messageId, "ctx-slash", "one action", path: AdapterConstants.RuntimePath + "/");

        Assert.Equal(before + 1, invoker.InvocationCount);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"SendMessage\",\"params\":[]}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"SendMessage\",\"params\":{\"message\":\"text\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"SendMessage\",\"params\":{\"message\":{\"contextId\":123}}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"SendMessage\",\"params\":{\"message\":{\"messageId\":99}}}")]
    [InlineData("not json at all")]
    public async Task MalformedRequestsDoNotProduceServerErrors(string payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("A2A-Version", "1.0");

        using var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A request without a messageId cannot be made idempotent, so it must be refused rather
    /// than silently opting out of replay protection.
    /// </summary>
    [Fact]
    public async Task RequestWithoutMessageIdIsRefused()
    {
        var invoker = Invoker;
        var before = invoker.InvocationCount;

        var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = "no-message-id",
                    method = "SendMessage",
                    @params = new
                    {
                        message = new
                        {
                            role = "ROLE_USER",
                            parts = new[] { new { text = "no message id here" } },
                            contextId = "ctx-no-id"
                        }
                    }
                },
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        request.Headers.Add("A2A-Version", "1.0");

        using var response = await _client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("mock-copilot-studio", content);
        Assert.Equal(before, invoker.InvocationCount);
    }

    private MockCopilotStudioInvoker Invoker =>
        Assert.IsType<MockCopilotStudioInvoker>(
            _factory.Services.GetRequiredService<ICopilotStudioInvoker>());

    private static string NewId() => $"msg-{Guid.NewGuid():N}";

    private Task<HttpResponseMessage> SendMessageAsync(
        string messageId,
        string contextId,
        string text,
        string? path = null,
        string? agentId = null,
        string? chainTargetAgentId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path ?? AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = messageId,
                    method = "SendMessage",
                    @params = new
                    {
                        message = new
                        {
                            role = "ROLE_USER",
                            parts = new[] { new { text } },
                            messageId,
                            contextId
                        }
                    }
                },
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        request.Headers.Add("A2A-Version", "1.0");
        if (agentId is not null)
        {
            request.Headers.Add(AdapterConstants.AgentHeaderName, agentId);
        }
        if (chainTargetAgentId is not null)
        {
            request.Headers.Add(AdapterConstants.ChainTargetHeaderName, chainTargetAgentId);
        }

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendV03MessageAsync(
        string messageId,
        string contextId,
        string text) =>
        _client.PostAsJsonAsync(
            AdapterConstants.RuntimePath,
            new
            {
                jsonrpc = "2.0",
                id = messageId,
                method = "message/send",
                @params = new
                {
                    message = new
                    {
                        kind = "message",
                        role = "user",
                        parts = new[] { new { kind = "text", text } },
                        messageId,
                        contextId
                    }
                }
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

}

public sealed class StartupValidationTests
{
    [Fact]
    public void AdapterRefusesAllowedOriginWithAPath()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Adapter:Backend", "Mock");
                builder.UseSetting("Adapter:PublicBaseUrl", "https://adapter.test");
                builder.UseSetting("Adapter:AllowedOrigins:0", "https://ui.test/path");
                builder.UseSetting("Adapter:AllowAnonymousDevelopmentMode", "true");
                builder.UseSetting("Authentication:Enabled", "false");
            });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("AllowedOrigins", exception.Message);
    }

    /// <summary>
    /// The adapter must refuse to start unauthenticated unless that is explicitly acknowledged,
    /// because both caches partition on a caller identity that does not exist without auth.
    /// </summary>
    [Fact]
    public void AdapterRefusesToStartUnauthenticatedWithoutExplicitOptIn()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Adapter:Backend", "Mock");
                builder.UseSetting("Adapter:PublicBaseUrl", "https://adapter.test");
                builder.UseSetting("Adapter:AllowAnonymousDevelopmentMode", "false");
                builder.UseSetting("Authentication:Enabled", "false");
            });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("AllowAnonymousDevelopmentMode", exception.Message);
    }
}

public sealed class A2AAdapterFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Adapter:Backend", "Mock");
        builder.UseSetting("Adapter:PublicBaseUrl", "https://adapter.test");
        builder.UseSetting("Adapter:AllowAnonymousDevelopmentMode", "true");
        builder.UseSetting("Authentication:Enabled", "false");
    }
}
