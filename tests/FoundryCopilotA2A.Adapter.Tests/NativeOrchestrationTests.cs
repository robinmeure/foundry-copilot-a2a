using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class NativeOrchestrationTests
{
    [Theory]
    [InlineData("foundry")]
    [InlineData("studio")]
    public void EitherProviderCanAdvertiseNativeTargets(string orchestratorId)
    {
        var catalog = CreateCatalog([" SPECIALIST ", "specialist", "second"]);
        var orchestrator = catalog.ResolveAgent(orchestratorId);

        Assert.True(orchestrator.CanOrchestrate);
        Assert.Equal(["specialist", "second"], orchestrator.ChainTargets);
        Assert.Equal("specialist", catalog.ResolveChainTarget(orchestratorId, "SPECIALIST").Id);
        Assert.False(catalog.ResolveAgent("specialist").CanOrchestrate);
        Assert.False(catalog.Agents.Single(agent => agent.Id == "unsupported").CanOrchestrate);
    }

    [Theory]
    [InlineData("foundry")]
    [InlineData("studio")]
    public void NativeOrchestrationIsOptIn(string orchestratorId)
    {
        var catalog = CreateCatalog([]);

        Assert.False(catalog.ResolveAgent(orchestratorId).CanOrchestrate);
        Assert.Throws<AdapterRequestException>(
            () => catalog.ResolveChainTarget(orchestratorId, "specialist"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    [InlineData("studio")]
    public void InvalidNativeTargetsFailConfiguration(string target) =>
        Assert.Throws<InvalidOperationException>(() => CreateCatalog([target]));

    [Theory]
    [InlineData("foundry")]
    [InlineData("studio")]
    public void RequestsCannotSelectAnUnlistedTarget(string orchestratorId)
    {
        var catalog = CreateCatalog(["specialist"]);

        Assert.Throws<AdapterRequestException>(
            () => catalog.ResolveChainTarget(orchestratorId, "second"));
    }

    [Theory]
    [InlineData("foundry", true)]
    [InlineData("studio", true)]
    [InlineData("foundry", false)]
    [InlineData("studio", false)]
    public async Task RoutingCallsOnlyTheNativeOrchestratorAndPreservesMetadata(
        string orchestratorId, bool chain)
    {
        var catalog = CreateCatalog(["specialist"]);
        var copilot = new CapturingCopilotInvoker();
        using var handler = new FoundryHandler();
        using var client = new HttpClient(handler);
        var router = CreateRouter(catalog, copilot, client);
        var metadata = Metadata(orchestratorId) with
        {
            ChainTargetAgentId = chain ? "specialist" : null,
            History = [new("user", "Earlier question"), new("assistant", "Earlier answer")]
        };

        var updates = await CollectAsync(router.StreamAsync(
            "Current request", metadata, CancellationToken.None));

        Assert.Equal("Native answer", updates.Last().Text);
        var nativePrompt = copilot.Prompt ?? handler.Prompt!;
        Assert.Contains("Current request", nativePrompt);
        Assert.Equal(chain, nativePrompt.Contains("\"Specialist\"", StringComparison.Ordinal));
        Assert.Equal(orchestratorId == "studio" ? 1 : 0, copilot.Calls);
        Assert.Equal(orchestratorId == "foundry" ? 1 : 0, handler.Calls);
        if (orchestratorId == "studio")
        {
            Assert.Same(metadata, copilot.Metadata);
            Assert.True(updates.First().IsInformative);
        }
        else
        {
            Assert.Contains("Earlier question", nativePrompt);
            Assert.Contains("Earlier answer", nativePrompt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolTelemetryRequiresAnActualTargetSpecificRequest(bool isAgentRoute)
    {
        using var parent = new Activity("native-orchestration-test").Start();
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GenAiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == parent.TraceId)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var catalog = CreateCatalog(["specialist"]);
        using var client = new HttpClient(new FoundryHandler());
        var router = CreateRouter(catalog, new CapturingCopilotInvoker(), client);
        var metadata = Metadata(isAgentRoute ? "specialist" : "studio") with
        {
            IsAgentRoute = isAgentRoute,
            ChainTargetAgentId = isAgentRoute ? null : "specialist"
        };

        await CollectAsync(router.StreamAsync("Run request", metadata, CancellationToken.None));

        Assert.Equal(isAgentRoute ? 1 : 0, activities.Count(activity =>
            activity.GetTagItem(GenAiTelemetry.Attributes.OperationName) as string ==
            GenAiTelemetry.Operations.ExecuteTool));
        Assert.All(activities, activity => Assert.Equal(ActivityStatusCode.Ok, activity.Status));
    }

    [Fact]
    public async Task ReplayCannotChangeTheRequestedNativeTarget()
    {
        var invoker = new CapturingCopilotInvoker();
        using var factory = CreateCopilotFactory(invoker);
        using var client = factory.CreateAuthenticatedClient(Caller.Victim);
        var messageId = Guid.NewGuid().ToString("N");

        using var first = await SendAsync(client, messageId, "specialist");
        Assert.Contains("Native answer", await first.Content.ReadAsStringAsync());
        using var replay = await SendAsync(client, messageId, "second");
        var body = await replay.Content.ReadAsStringAsync();

        Assert.Contains("different content", body);
        Assert.Equal(1, invoker.Calls);
    }

    [Fact]
    public async Task CopilotCatalogAndRouteUseConfiguredCapabilities()
    {
        var invoker = new CapturingCopilotInvoker();
        using var factory = CreateCopilotFactory(invoker);
        using var client = factory.CreateAuthenticatedClient(Caller.Victim);
        using var catalog = JsonDocument.Parse(await client.GetStringAsync(AdapterConstants.AgentsPath));
        var orchestrator = catalog.RootElement.GetProperty("agents")
            .EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == "studio");
        Assert.True(orchestrator.GetProperty("canOrchestrate").GetBoolean());
        Assert.Equal("copilotStudio", orchestrator.GetProperty("provider").GetString());

        using var request = MessageRequest(Guid.NewGuid().ToString("N"));
        request.RequestUri = new Uri(AdapterConstants.ChainAgentRuntimePath("specialist"), UriKind.Relative);
        request.Headers.Add(AdapterConstants.AgentHeaderName, "studio");
        using var response = await client.SendAsync(request);
        Assert.Contains("Native answer", await response.Content.ReadAsStringAsync());
        Assert.Equal("specialist", invoker.Metadata?.AgentId);
        Assert.True(invoker.Metadata?.IsAgentRoute);
        Assert.Equal("Current request", invoker.Prompt);
    }

    [Fact]
    public async Task FoundryAgentHasProviderSpecificCardAndRoute()
    {
        using var factory = CreateCopilotFactory(new CapturingCopilotInvoker()).WithWebHostBuilder(
            builder =>
            {
                builder.UseSetting("Foundry:Agents:foundry:DisplayName", "Foundry");
                builder.UseSetting(
                    "Foundry:Agents:foundry:Endpoint",
                    "https://foundry.example/agents/agent/endpoint/protocols/a2a");
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"{AdapterConstants.ChainAgentBasePath("foundry")}/.well-known/agent-card.json");
        using var card = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Foundry", card.RootElement.GetProperty("name").GetString());
        Assert.Equal(
            "foundry-foundry",
            card.RootElement.GetProperty("skills")[0].GetProperty("id").GetString());
        Assert.Equal(
            "foundry",
            card.RootElement.GetProperty("skills")[0].GetProperty("tags")[0].GetString());
        Assert.EndsWith(
            "/a2a-agents/foundry/a2a",
            card.RootElement.GetProperty("url").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecuredFoundryRejectsMissingOrAppOnlyIdentity(bool appOnly)
    {
        var token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: [new Claim("roles", "application-role")]));
        using var handler = new FoundryHandler();
        using var client = new HttpClient(handler);
        var copilot = new CapturingCopilotInvoker();
        var router = CreateRouter(CreateCatalog(["specialist"]), copilot, client);
        var metadata = Metadata("foundry") with
        {
            BearerToken = appOnly ? token : null
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CollectAsync(router.StreamAsync("Request", metadata, CancellationToken.None)));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, copilot.Calls);
    }

    private static AgentCatalog CreateCatalog(string[] targets) => new(
        Options.Create(new AdapterOptions { Backend = "CopilotStudio" }),
        Options.Create(new CopilotStudioOptions
        {
            DefaultAgent = "studio",
            Agents = new()
            {
                ["studio"] = new() { DisplayName = "Studio", ChainTargets = targets },
                ["specialist"] = new() { DisplayName = "Specialist" },
                ["second"] = new() { DisplayName = "Second" },
                ["unsupported"] = new()
                {
                    DisplayName = "Unsupported", Harness = CopilotStudioHarness.GitHubCopilot
                }
            }
        }),
        Options.Create(new FoundryOptions
        {
            Agents = new()
            {
                ["foundry"] = new()
                {
                    DisplayName = "Foundry",
                    Endpoint = "https://foundry.example/agents/agent/endpoint/protocols/a2a",
                    ChainTargets = targets
                }
            }
        }));

    private static RoutingAgentInvoker CreateRouter(
        AgentCatalog catalog, ICopilotStudioInvoker copilot, HttpClient client)
    {
        var clientFactory = new ClientFactory(client);
        var apiManagementAgents = new ApiManagementAgentRegistry(
            new StubCredential(),
            clientFactory,
            Options.Create(new ApiManagementDiscoveryOptions()),
            TimeProvider.System,
            NullLogger<ApiManagementAgentRegistry>.Instance);
        return new RoutingAgentInvoker(
            catalog,
            apiManagementAgents,
            copilot,
            new FoundryA2AInvoker(
                catalog,
                new StubCredential(),
                clientFactory,
                new DelegatedTokenBroker(),
                Options.Create(new AuthenticationOptions { Enabled = true })),
            new ApiManagementA2AInvoker(apiManagementAgents, clientFactory));
    }

    private static A2ARequestMetadata Metadata(string agentId) => new()
    {
        AgentId = agentId,
        ContextId = "context",
        MessageId = Guid.NewGuid().ToString("N"),
        UserId = "tenant|caller",
        PayloadHash = "payload",
        BearerToken = "caller-token"
    };

    private static async Task<List<CopilotInvocationUpdate>> CollectAsync(
        IAsyncEnumerable<CopilotInvocationUpdate> updates)
    {
        var result = new List<CopilotInvocationUpdate>();
        await foreach (var update in updates)
        {
            result.Add(update);
        }
        return result;
    }

    private static AuthenticatedAdapterFactory CreateCopilotFactory(CapturingCopilotInvoker invoker) =>
        new(builder =>
        {
            builder.UseSetting("Adapter:Backend", "CopilotStudio");
            builder.UseSetting("CopilotStudio:TenantId", "tenant-1");
            builder.UseSetting("CopilotStudio:ClientId", "adapter-test");
            builder.UseSetting("CopilotStudio:ClientSecret", "test-only-placeholder");
            builder.UseSetting("CopilotStudio:DefaultAgent", "studio");
            foreach (var id in new[] { "studio", "specialist", "second" })
            {
                builder.UseSetting($"CopilotStudio:Agents:{id}:DisplayName", id);
                builder.UseSetting($"CopilotStudio:Agents:{id}:DirectConnectUrl", $"https://copilot.example/{id}");
            }
            builder.UseSetting("CopilotStudio:Agents:studio:ChainTargets:0", "specialist");
            builder.UseSetting("CopilotStudio:Agents:studio:ChainTargets:1", "second");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICopilotStudioInvoker>();
                services.AddSingleton<ICopilotStudioInvoker>(invoker);
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string messageId, string target)
    {
        using var request = MessageRequest(messageId);
        request.Headers.Add(AdapterConstants.AgentHeaderName, "studio");
        request.Headers.Add(AdapterConstants.ChainTargetHeaderName, target);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage MessageRequest(string messageId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = messageId,
                method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER",
                        parts = new[] { new { text = "Current request" } },
                        messageId,
                        contextId = "native-context"
                    }
                }
            })
        };
        request.Headers.Add("A2A-Version", "1.0");
        return request;
    }

    private sealed class CapturingCopilotInvoker : ICopilotStudioInvoker
    {
        public int Calls { get; private set; }
        public string? Prompt { get; private set; }
        public A2ARequestMetadata? Metadata { get; private set; }

        public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
            string prompt,
            A2ARequestMetadata metadata,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Prompt = prompt;
            Metadata = metadata;
            await Task.Yield();
            yield return new("Working", metadata.ContextId, "response", IsInformative: true);
            yield return new("Native answer", metadata.ContextId, "response");
        }
    }

    private sealed class FoundryHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Prompt { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("caller-foundry-token", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Prompt = body.RootElement.GetProperty("params").GetProperty("message")
                .GetProperty("parts")[0].GetProperty("text").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","result":{"message":{"parts":[{"text":"Native answer"}]}}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Secured Foundry calls must not use a shared identity.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class DelegatedTokenBroker : IOboTokenBroker
    {
        public Task<string> AcquireAsync(
            string scope, A2ARequestMetadata metadata, CancellationToken cancellationToken)
        {
            Assert.Equal("https://ai.azure.com/.default", scope);
            Assert.Equal("caller-token", metadata.BearerToken);
            return Task.FromResult("caller-foundry-token");
        }
    }
}
