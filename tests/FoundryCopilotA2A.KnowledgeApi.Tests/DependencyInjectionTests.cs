using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Chat;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Knowledge;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void PublicConsumersDoNotInjectConcreteImplementationsWithInterfaces()
    {
        Type[] implementations =
        [
            typeof(AgentModelClient), typeof(A2aKnowledgeService), typeof(DocumentStore),
            typeof(SearchStore), typeof(DocumentRetrieval), typeof(GraphService),
            typeof(ConversationHistoryStore), typeof(ResponsesCoordinator), typeof(KnowledgeService)
        ];
        foreach (var consumer in typeof(KnowledgeService).Assembly.GetExportedTypes())
        {
            foreach (var constructor in consumer.GetConstructors())
            {
                Assert.DoesNotContain(constructor.GetParameters(),
                    parameter => implementations.Contains(parameter.ParameterType));
            }
        }
    }

    [Fact]
    public void DefaultInterfacesResolveToTheExistingSingletons()
    {
        using var api = new Api();
        var services = api.Services;
        AssertAlias<IAgentModelClient, AgentModelClient>(services);
        AssertAlias<IA2aKnowledgeService, A2aKnowledgeService>(services);
        AssertAlias<IDocumentStore, DocumentStore>(services);
        AssertAlias<ISearchStore, SearchStore>(services);
        AssertAlias<IDocumentRetrieval, DocumentRetrieval>(services);
        AssertAlias<IGraphService, GraphService>(services);
        AssertAlias<IConversationHistoryStore, ConversationHistoryStore>(services);
        AssertAlias<IResponsesCoordinator, ResponsesCoordinator>(services);
        AssertAlias<IKnowledgeService, KnowledgeService>(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MockChatUsesReplacedSearchAndA2aForNormalAndStreamingResponses(bool streaming)
    {
        var search = new RecordingSearch();
        var a2a = new RecordingA2a();
        using var api = new Api(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ISearchStore>(search));
            services.Replace(ServiceDescriptor.Singleton<IA2aKnowledgeService>(a2a));
            services.Replace(ServiceDescriptor.Singleton<IAgentModelClient>(new ForbiddenModel()));
            Poison<SearchStore>(services);
            Poison<A2aKnowledgeService>(services);
            Poison<AgentModelClient>(services);
        });
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("A replaceable source says Lavender."u8.ToArray()), "file", "brief.txt");
        using var uploaded = await client.PostAsync($"/api/conversations/{conversation}/documents", form);
        uploaded.EnsureSuccessStatusCode();
        var document = await uploaded.Content.ReadFromJsonAsync<DocumentRecord>();
        Assert.NotNull(document);

        using var catalog = await client.GetAsync("/api/agents");
        catalog.EnsureSuccessStatusCode();
        Assert.Contains("replacement-agent", await catalog.Content.ReadAsStringAsync());

        using var reply = streaming
            ? await client.PostAsJsonAsync("/api/chat/stream",
                AgUi.Run(conversation, "What is the fact?", ["a2a"], ["replacement-agent"]))
            : await client.PostAsJsonAsync("/api/chat",
                new ChatRequest(conversation, "What is the fact?", ["a2a"], Agents: ["replacement-agent"]));
        reply.EnsureSuccessStatusCode();
        var body = await reply.Content.ReadAsStringAsync();
        Assert.Contains("Lavender", body);
        Assert.Contains("Replacement specialist", body);
        if (streaming)
        {
            var events = AgUi.ParseEvents(body);
            Assert.Equal("RUN_FINISHED", events[^1].Type());
            Assert.DoesNotContain(events, item => item.Type() == "RUN_ERROR");
        }
        Assert.Equal(1, search.IndexCalls);
        Assert.Equal(1, search.SearchCalls);
        Assert.Equal(1, a2a.SelectionCalls);
        Assert.Equal(1, a2a.ListCalls);
        Assert.Equal(1, a2a.AskCalls);

        using var deleted = await client.DeleteAsync($"/api/conversations/{conversation}/documents/{document.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(1, search.DeleteCalls);
    }

    [Fact]
    public async Task LiveOrchestrationUsesReplacedModelRatherThanConcreteFactory()
    {
        using var model = new RecordingModel("The interface supplied this answer.");
        using var api = new Api(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAgentModelClient>(model));
            Poison<AgentModelClient>(services);
        });
        var coordinator = api.Services.GetRequiredService<IResponsesCoordinator>();
        var result = await coordinator.Run(new DefaultHttpContext(), "Say hello", [], [], [], default);
        Assert.Equal("The interface supplied this answer.", result.Answer);
        Assert.Equal(1, model.CreateCalls);
        Assert.Equal(1, model.Requests);
    }

    [Theory]
    [InlineData("focused")]
    [InlineData("whole_document")]
    public async Task RetrievalUsesReplacedSearchAndModelForBatchAnalysis(string mode)
    {
        var search = new RecordingSearch();
        using var model = new RecordingModel("""{"findings":[]}""");
        using var api = new Api(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ISearchStore>(search));
            services.Replace(ServiceDescriptor.Singleton<IAgentModelClient>(model));
            Poison<SearchStore>(services);
            Poison<AgentModelClient>(services);
        });
        // Exercise the live analysis branch with a local HTTP transport, never Azure.
        api.Services.GetRequiredService<IConfiguration>()["Knowledge:Backend"] = "Azure";
        var retrieval = api.Services.GetRequiredService<IDocumentRetrieval>();
        var document = new DocumentRecord("doc", "scope", "large.txt",
            DateTimeOffset.UtcNow.AddDays(1), 3, UserId: "user");
        var passages = Enumerable.Range(0, 3)
            .Select(index => new Passage($"doc-{index}", "doc", "large.txt",
                new string('x', 20000), $"Page {index + 1}", UserId: "user"))
            .ToArray();
        await search.Index(document, passages, default);

        var read = await retrieval.ReadAsync("scope", "user", [document],
            new DocumentRetrievalRequest(mode, "Analyze all passages", ["doc"]), ChatProgress.None, default);
        Assert.Equal(3, Assert.Single(read.Coverage).ReadChunks);
        var analyzed = await retrieval.AnalyzeAsync(read, "Analyze all passages", ChatProgress.None, default);
        Assert.True(analyzed.Synthesized);
        Assert.Equal(1, model.CreateCalls);
        Assert.True(model.Requests >= 2, "The payload must cross the batch threshold to exercise model-backed analysis.");
        Assert.Equal(mode == "focused" ? 1 : 0, search.SearchCalls);
        Assert.Equal(mode == "whole_document" ? 1 : 0, search.ReadCalls);
    }

    private static void AssertAlias<TInterface, TImplementation>(IServiceProvider services)
        where TInterface : class
        where TImplementation : class, TInterface =>
        Assert.Same(services.GetRequiredService<TImplementation>(), services.GetRequiredService<TInterface>());

    private static void Poison<T>(IServiceCollection services) where T : class =>
        services.Replace(ServiceDescriptor.Singleton<T>(_ =>
            throw new InvalidOperationException($"A consumer bypassed the interface for {typeof(T).Name}.")));

    private sealed class Api(Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "knowledge-di-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
            builder.ConfigureServices(services =>
            {
                configure?.Invoke(services);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class RecordingSearch : ISearchStore
    {
        private readonly ConcurrentDictionary<string, (DocumentRecord Document, Passage[] Passages)> entries = new();
        public int IndexCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task Index(DocumentRecord document, Passage[] passages, CancellationToken ct)
        {
            IndexCalls++;
            entries[document.Id] = (document, passages);
            return Task.CompletedTask;
        }

        public Task<Passage[]> Search(
            string scope, DocumentRecord[] allowed, string query, CancellationToken ct, string? userId = null)
        {
            SearchCalls++;
            return Task.FromResult(allowed.SelectMany(document => entries[document.Id].Passages).ToArray());
        }

        public Task<Passage[]> ReadDocument(string scope, string userId, DocumentRecord document, CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult(entries[document.Id].Passages);
        }

        public Task Delete(DocumentRecord document, CancellationToken ct)
        {
            DeleteCalls++;
            entries.TryRemove(document.Id, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingA2a : IA2aKnowledgeService
    {
        public bool Enabled => true;
        public int SelectionCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int AskCalls { get; private set; }

        public string[]? ResolveSelection(IReadOnlyCollection<string> sources, string[]? agentIds)
        {
            SelectionCalls++;
            Assert.Contains("a2a", sources);
            Assert.NotNull(agentIds);
            Assert.Equal(["replacement-agent"], agentIds);
            return agentIds;
        }

        public Task<KnowledgeAgentDescriptor[]> GetAvailableAgentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<KnowledgeAgentDescriptor[]>([new("replacement-agent", "Replacement specialist")]);

        public Task<KnowledgeAgentDescriptor[]> ListAsync(
            HttpContext context, JsonElement arguments, CancellationToken cancellationToken,
            IReadOnlyCollection<string>? selectedAgentIds = null)
        {
            ListCalls++;
            Assert.Equal(["replacement-agent"], selectedAgentIds);
            return GetAvailableAgentsAsync(cancellationToken);
        }

        public Task<A2aKnowledgeAnswer> AskAsync(
            HttpContext context, JsonElement arguments, CancellationToken cancellationToken,
            IReadOnlyCollection<string>? selectedAgentIds = null,
            Func<string, CancellationToken, ValueTask>? onProgress = null)
        {
            AskCalls++;
            Assert.Equal("replacement-agent", arguments.GetProperty("agentId").GetString());
            Assert.Equal(["replacement-agent"], selectedAgentIds);
            return Task.FromResult(new A2aKnowledgeAnswer(new Passage("agent-1", "replacement-agent", "Replacement specialist",
                "Evidence from the substituted agent.", "Agent answer",
                OriginatingAgent: new OriginatingAgentInfo("replacement-agent", "Replacement specialist")), []));
        }
    }

    private sealed class RecordingModel : IAgentModelClient, IDisposable
    {
        private readonly HttpClient http;
        private readonly AgentModelClient implementation;
        public int CreateCalls { get; private set; }
        public int Requests { get; private set; }

        public RecordingModel(string answer)
        {
            http = new HttpClient(new Handler(() =>
            {
                Requests++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        status = "completed",
                        output = new[]
                        {
                            new
                            {
                                type = "message", role = "assistant", status = "completed", id = "msg1",
                                content = new[] { new { type = "output_text", text = answer } }
                            }
                        }
                    }), Encoding.UTF8, "application/json")
                };
            }));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:Endpoint"] = "https://example.openai.azure.com",
                ["OpenAI:ChatDeployment"] = "test-model"
            }).Build();
            implementation = new AgentModelClient(new ClientFactory(http), new Credential(), configuration);
        }

        public IChatClient Create()
        {
            CreateCalls++;
            return implementation.Create();
        }

        public void Dispose() => http.Dispose();
    }

    private sealed class ForbiddenModel : IAgentModelClient
    {
        public IChatClient Create() =>
            throw new InvalidOperationException("Mock chat must not create a model client.");
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response());
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-only", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
