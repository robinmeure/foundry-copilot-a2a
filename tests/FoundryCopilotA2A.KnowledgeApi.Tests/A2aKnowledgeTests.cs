using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class A2aKnowledgeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "a2a-knowledge-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task MockChatExposesA2aCapabilityAndPreservesSpecialistAttribution()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
        });
        using var client = api.CreateClient();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/capabilities");
        Assert.True(capabilities.GetProperty("a2a").GetBoolean());
        var response = await client.PostAsJsonAsync("/api/chat", new
        {
            conversationId = Guid.NewGuid().ToString(),
            text = "When is the specialist project review?",
            sources = new[] { "a2a" }
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Friday", result.GetProperty("answer").GetString());
        var source = Assert.Single(result.GetProperty("citations").GetProperty("sources").EnumerateArray());
        Assert.Equal("mock-apim-knowledge", source.GetProperty("originatingAgent").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FrameworkA2aClientUsesApprovedAuthenticationAndRouting(bool taskResponse, bool sameAudience)
    {
        const string tenant = "22222222-2222-2222-2222-222222222222";
        const string scope = "api://adapter/access_as_user";
        var tokenCalls = 0;
        using var tokenHttp = new HttpClient(new AsyncHandler(async (request, ct) =>
        {
            Assert.False(sameAudience, "Shared-audience calls must never perform a token exchange.");
            Assert.Equal("login.microsoftonline.com", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get)
            {
                return Json($$"""
                    {
                      "authorization_endpoint":"https://login.microsoftonline.com/{{tenant}}/oauth2/v2.0/authorize",
                      "token_endpoint":"https://login.microsoftonline.com/{{tenant}}/oauth2/v2.0/token",
                      "issuer":"https://login.microsoftonline.com/{{tenant}}/v2.0",
                      "jwks_uri":"https://login.microsoftonline.com/{{tenant}}/discovery/v2.0/keys"
                    }
                    """);
            }
            tokenCalls++;
            var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                await request.Content!.ReadAsStringAsync(ct));
            Assert.Contains(scope, form["scope"].ToString());
            Assert.DoesNotContain("graph.microsoft.com", form["scope"].ToString());
            Assert.Equal("user-assertion", form["assertion"].ToString());
            var clientInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { uid = "caller", utid = tenant })))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Json(JsonSerializer.Serialize(new
            {
                token_type = "Bearer",
                expires_in = 3600,
                access_token = "adapter-obo-token",
                scope,
                client_info = clientInfo
            }));
        }));
        var tokens = new DelegatedTokenProvider(ConfidentialClientApplicationBuilder
            .Create("11111111-1111-1111-1111-111111111111")
            .WithClientSecret("unit-test-only")
            .WithAuthority($"https://login.microsoftonline.com/{tenant}")
            .WithInstanceDiscovery(false)
            .WithHttpClientFactory(new MsalFactory(tokenHttp))
            .Build());

        var runtimeCalls = 0;
        var messageIds = new HashSet<string>();
        using var handler = new AsyncHandler(async (request, ct) =>
        {
            Assert.Equal("adapter.example", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath == "/api/agents")
            {
                Assert.Null(request.Headers.Authorization);
                return Json("""{"agents":[{"id":"allowed","displayName":"Specialist","provider":"apiManagement","supported":true}]}""");
            }
            runtimeCalls++;
            Assert.Equal("/a2a/copilot-studio", request.RequestUri.AbsolutePath);
            Assert.Equal(sameAudience ? "validated-api-token" : "adapter-obo-token", request.Headers.Authorization!.Parameter);
            Assert.Equal("allowed", Assert.Single(request.Headers.GetValues("X-Copilot-Agent")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert.Equal("SendMessage", body.GetProperty("method").GetString());
            var message = body.GetProperty("params").GetProperty("message");
            Assert.True(messageIds.Add(message.GetProperty("messageId").GetString()!));
            Assert.Contains("When is the review?", message.GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.False(message.TryGetProperty("contextId", out var contextId) && contextId.ValueKind == JsonValueKind.String);
            if (taskResponse)
            {
                return Json(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = body.GetProperty("id").Clone(),
                    result = new
                    {
                        task = new
                        {
                            id = "task1",
                            contextId = "remote-context",
                            status = new { state = "TASK_STATE_COMPLETED" },
                            artifacts = new[]
                            {
                                new { artifactId = "answer", parts = new[] { new { text = "The review is Friday." } } }
                            }
                        }
                    }
                }));
            }
            return Json(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = body.GetProperty("id").Clone(),
                result = new
                {
                    message = new
                    {
                        messageId = "reply",
                        role = "ROLE_AGENT",
                        parts = new[] { new { text = "The review is Friday." } }
                    }
                }
            }));
        });
        var service = new A2aKnowledgeService(new HandlerFactory(handler),
            TestConfiguration.Create("Azure", values: new Dictionary<string, string?>
            {
                ["A2A:Enabled"] = "true",
                ["A2A:AdapterBaseUrl"] = "https://adapter.example",
                ["A2A:AuthenticationMode"] = sameAudience ? "SameAudience" : "OnBehalfOf",
                ["A2A:DelegatedScope"] = sameAudience ? "" : scope,
                ["A2A:AllowedAgentIds:0"] = "allowed"
            }), tokens, NullLogger<A2aKnowledgeService>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer user-assertion";
        var authentication = AuthenticatedApi();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new SavedAuthentication(authentication))
            .BuildServiceProvider();
        context.RequestServices = services;
        context.User = authentication.Principal!;
        for (var index = 0; index < 2; index++)
        {
            var passage = await service.AskAsync(context,
                Args(new { agentId = "allowed", question = "When is the review?" }), default);
            Assert.Equal("The review is Friday.", passage.Content);
            Assert.Equal("allowed", passage.OriginatingAgent!.Id);
            Assert.Null(passage.Url);
        }
        Assert.Equal(2, runtimeCalls);
        Assert.Equal(sameAudience ? 0 : 1, tokenCalls);
    }

    [Theory]
    [InlineData("wrong-audience", "access_as_user", true, true, true)]
    [InlineData("11111111-1111-1111-1111-111111111111", "other_scope", true, true, true)]
    [InlineData("11111111-1111-1111-1111-111111111111", "access_as_user", false, true, true)]
    [InlineData("11111111-1111-1111-1111-111111111111", "access_as_user", true, false, true)]
    [InlineData("11111111-1111-1111-1111-111111111111", "access_as_user", true, true, false)]
    public async Task SharedAudienceRejectsUnvalidatedWrongAudienceOrNondelegatedTokens(
        string audience, string scope, bool savedToken, bool authenticated, bool matchingCaller)
    {
        var authentication = authenticated ? AuthenticatedApi(audience, scope, savedToken) : AuthenticateResult.NoResult();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new SavedAuthentication(authentication))
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = matchingCaller && authentication.Principal is not null
                ? authentication.Principal
                : new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "different-user")], "test"))
        };
        context.Request.Headers.Authorization = "Bearer unvalidated-header-token";
        var tokens = new DelegatedTokenProvider(ConfidentialClientApplicationBuilder
            .Create("11111111-1111-1111-1111-111111111111").Build());
        var error = await Assert.ThrowsAsync<ApiException>(() => tokens.GetSameAudienceTokenAsync(context, default));
        Assert.Equal("invalid_shared_api_token", error.Code);
    }

    private static AuthenticateResult AuthenticatedApi(
        string audience = "11111111-1111-1111-1111-111111111111",
        string scope = "access_as_user",
        bool savedToken = true)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("aud", audience), new Claim("scp", scope), new Claim("tid", "tenant"), new Claim("oid", "caller")],
            JwtBearerDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties();
        if (savedToken)
        {
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "validated-api-token" }]);
        }
        return AuthenticateResult.Success(new AuthenticationTicket(principal, properties, JwtBearerDefaults.AuthenticationScheme));
    }

    private sealed class SavedAuthentication(AuthenticateResult result) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Assert.Equal(JwtBearerDefaults.AuthenticationScheme, scheme);
            return Task.FromResult(result);
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CatalogRestrictsKnowledgeToApprovedSupportedApimAgents()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/api/agents", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return Json("""
                {"agents":[
                    {"id":"allowed","displayName":"Knowledge specialist","provider":"apiManagement","supported":true},
                    {"id":"unapproved","displayName":"Other","provider":"apiManagement","supported":true},
                    {"id":"online","displayName":"Online","provider":"copilotStudio","supported":true},
                    {"id":"unsupported","displayName":"Unsupported","provider":"apiManagement","supported":false}
                ]}
                """);
        }));
        var service = Service(http, ["allowed", "online", "unsupported"]);
        var agents = await service.ListAsync(new DefaultHttpContext(), Args(new { }), default);
        Assert.Equal("allowed", Assert.Single(agents).Id);
        var error = await Assert.ThrowsAsync<ApiException>(() => service.AskAsync(
            new DefaultHttpContext(), Args(new { agentId = "unapproved", question = "Read facts" }), default));
        Assert.Equal("a2a_agent_not_allowed", error.Code);
    }

    [Fact]
    public async Task MockAgentsEndpointAndChatSelectionAreValidated()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
        });
        using var client = api.CreateClient();
        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/agents");
        var agent = Assert.Single(catalog.GetProperty("agents").EnumerateArray());
        Assert.Equal("mock-apim-knowledge", agent.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(agent.GetProperty("displayName").GetString()));

        var selected = await client.PostAsJsonAsync("/api/chat", new
        {
            conversationId = Guid.NewGuid().ToString(),
            text = "When is the specialist project review?",
            sources = new[] { "a2a" },
            agents = new[] { "MOCK-APIM-KNOWLEDGE" }
        });
        selected.EnsureSuccessStatusCode();
        var result = await selected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(result.GetProperty("citations").GetProperty("sources").EnumerateArray());

        foreach (var agents in new[] { new[] { "unapproved" }, Array.Empty<string>() })
        {
            var rejected = await client.PostAsJsonAsync("/api/chat", new
            {
                conversationId = Guid.NewGuid().ToString(),
                text = "question",
                sources = new[] { "a2a" },
                agents
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("invalid_agent", await rejected.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task SelectionRestrictsListedAndInvokedAgents()
    {
        var catalogCalls = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal("/api/agents", request.RequestUri!.AbsolutePath);
            catalogCalls++;
            return Json("""
                {"agents":[
                    {"id":"first","displayName":"First specialist","provider":"apiManagement","supported":true},
                    {"id":"second","displayName":"Second specialist","provider":"apiManagement","supported":true}
                ]}
                """);
        });
        var service = Service(new PerCallFactory(handler), ["first", "second"]);

        Assert.Equal(["first", "second"], (await service.GetAvailableAgentsAsync(default)).Select(agent => agent.Id));
        var selection = service.ResolveSelection(["a2a"], ["first", "FIRST"]);
        Assert.Equal(["first"], selection!);
        var listed = await service.ListAsync(new DefaultHttpContext(), Args(new { }), default, selection);
        Assert.Equal("first", Assert.Single(listed).Id);

        catalogCalls = 0;
        var error = await Assert.ThrowsAsync<ApiException>(() => service.AskAsync(
            new DefaultHttpContext(), Args(new { agentId = "second", question = "Read facts" }), default, selection));
        Assert.Equal("a2a_agent_not_selected", error.Code);
        Assert.Equal(0, catalogCalls);

        Assert.Null(service.ResolveSelection(["a2a"], null));
        Assert.Null(service.ResolveSelection(["mail"], ["unapproved"]));
        Assert.Equal("invalid_agent", Assert.Throws<ApiException>(() => service.ResolveSelection(["a2a"], ["unapproved"])).Code);
        Assert.Equal("invalid_agent", Assert.Throws<ApiException>(() => service.ResolveSelection(["a2a"], [])).Code);
    }

    [Fact]
    public async Task CatalogFailureIsNotAnEmptySuccess()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            Service(http, ["allowed"]).ListAsync(new DefaultHttpContext(), Args(new { }), default));
        Assert.Equal("a2a_catalog_error", exception.Code);
    }

    [Theory]
    [InlineData("{\"url\":\"https://untrusted.example\"}")]
    [InlineData("{\"agentId\":\"allowed\",\"question\":\"\"}")]
    [InlineData("{\"agentId\":\"allowed\",\"question\":\"facts\",\"url\":\"https://untrusted.example\"}")]
    public async Task InvocationRejectsInvalidOrModelSuppliedEndpointArguments(string arguments)
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No outbound call expected")));
        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            Service(http, ["allowed"]).AskAsync(new DefaultHttpContext(), JsonDocument.Parse(arguments).RootElement, default));
        Assert.Equal("invalid_tool_arguments", exception.Code);
    }

    private static A2aKnowledgeService Service(HttpClient http, string[] allowed) => Service(new Factory(http), allowed);

    private static A2aKnowledgeService Service(IHttpClientFactory clients, string[] allowed) => new(
        clients,
        TestConfiguration.Create("Azure", values: new Dictionary<string, string?>
        {
            ["A2A:Enabled"] = "true",
            ["A2A:AdapterBaseUrl"] = "https://adapter.example",
            ["A2A:DelegatedScope"] = "api://adapter/access_as_user"
        }.Concat(allowed.Select((id, index) => new KeyValuePair<string, string?>($"A2A:AllowedAgentIds:{index}", id)))
            .ToDictionary()),
        new DelegatedTokenProvider(ConfidentialClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111").Build()),
        NullLogger<A2aKnowledgeService>.Instance);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class PerCallFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class MsalFactory(HttpClient client) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => client;
    }
}
