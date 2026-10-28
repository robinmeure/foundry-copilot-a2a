using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.BackgroundServices;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Chat;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class AzureContractTests
{
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-credential", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private static IConfiguration Config() => TestConfiguration.Create(values: new Dictionary<string, string?>
    {
        ["OpenAI:Endpoint"] = "https://example.openai.azure.com",
        ["OpenAI:ChatDeployment"] = "gpt-6.1-sol",
        ["OpenAI:EmbeddingDeployment"] = "text-embedding-3-large",
        ["OpenAI:EmbeddingDimensions"] = "3",
        ["DocumentIntelligence:Endpoint"] = "https://example.cognitiveservices.azure.com",
        ["DocumentIntelligence:ModelId"] = "prebuilt-read"
    });
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task UsesResponsesApiWithoutRetainingConversations()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("/openai/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("gpt-6.1-sol", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.Contains("Question:", body.RootElement.GetProperty("input").GetRawText());
            return Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","id":"msg1","content":[{"type":"output_text","text":"The value is 42 [[cite:1]]."}]}]}""");
        }));
        var result = await Coordinator(http).Run(new DefaultHttpContext(), "value?",
            [new Passage("1", "doc", "Test", "42", "Page 1")], [], [], default);
        Assert.Equal("The value is 42 [1].", result.Answer);
    }

    [Fact]
    public async Task RejectsWrongEmbeddingDimensions()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json("""{"data":[{"embedding":[1,2]}]}"""))));
        var azure = new AzureServices(new Factory(http), new Credential(), Config());
        var exception = await Assert.ThrowsAsync<ApiException>(() => azure.Embed("test", default));
        Assert.Equal("embedding_mismatch", exception.Code);
    }

    [Fact]
    public async Task ExtractionUsesReadModelAndPreservesPageNumbers()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (request.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (calls == 1)
            {
                Assert.Contains("prebuilt-read:analyze", request.RequestUri!.AbsoluteUri);
                Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add("Operation-Location", "https://example.cognitiveservices.azure.com/operation/1");
                return Task.FromResult(response);
            }
            return Task.FromResult(Json("""{"status":"succeeded","analyzeResult":{"pages":[{"pageNumber":2,"lines":[{"content":"Budget 42"}]}]}}"""));
        }));
        var azure = new AzureServices(new Factory(http), new Credential(), Config());
        var page = Assert.Single(await azure.Extract("%PDF-test"u8.ToArray(), default));
        Assert.Equal(2, page.Number);
        Assert.Equal("Budget 42", page.Text);
    }

    [Fact]
    public async Task RejectsUntrustedPollingHostBeforeSendingCredential()
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.Add("Operation-Location", "https://evil.test/operation/1");
            return Task.FromResult(response);
        }));
        var azure = new AzureServices(new Factory(http), new Credential(), Config());
        Assert.Equal("invalid_operation", (await Assert.ThrowsAsync<ApiException>(() => azure.Extract([1], default))).Code);
    }

    internal static ResponsesCoordinator Coordinator(HttpClient http, KamervragenSkill? kamervragen = null)
    {
        var factory = new Factory(http);
        var configuration = Config();
        var graph = new GraphService(factory, configuration);
        return new(new AgentModelClient(factory, new Credential(), Config()),
            new(graph, configuration), NullLogger<ResponsesCoordinator>.Instance,
            new A2aKnowledgeService(factory, configuration, new DelegatedTokenProvider(
                Microsoft.Identity.Client.ConfidentialClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111").Build()),
                NullLogger<A2aKnowledgeService>.Instance),
            new GraphActions(graph, configuration),
            kamervragen ?? new(new AzureServices(factory, new Credential(), Config()), configuration));
    }

    [Fact]
    public async Task ToolLoopCarriesReasoningAndCallIdsAndCitesDiscoveredTodoTask()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.False(body.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.False(body.TryGetProperty("previous_response_id", out _));
            Assert.Contains("reasoning.encrypted_content", body.GetProperty("include").EnumerateArray().Select(v => v.GetString()));
            Assert.All(body.GetProperty("tools").EnumerateArray(), tool =>
                Assert.Matches("^graph_(list|propose)_todo_", tool.GetProperty("name").GetString()));
            calls++;
            if (calls == 1)
                return Json("""{"status":"completed","output":[{"type":"reasoning","id":"r1","summary":[],"encrypted_content":"opaque"},{"type":"function_call","call_id":"call1","name":"graph_list_todo_lists","arguments":"{}"}]}""");
            var input = body.GetProperty("input").EnumerateArray().ToArray();
            Assert.Contains(input, i => i.TryGetProperty("encrypted_content", out var encrypted) && encrypted.GetString() == "opaque");
            var output = input.Last(i => i.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            using var data = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            if (calls == 2)
            {
                Assert.Equal("call1", output.GetProperty("call_id").GetString());
                Assert.Equal("[[cite:1]]", data.RootElement.GetProperty("items")[0].GetProperty("citation").GetString());
                return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"call2","name":"graph_list_todo_tasks","arguments":"{\"listId\":\"mock-list\"}"}]}""");
            }
            Assert.Equal("call2", output.GetProperty("call_id").GetString());
            Assert.Equal("[[cite:2]]", data.RootElement.GetProperty("items")[0].GetProperty("citation").GetString());
            return Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","id":"msg1","content":[{"type":"output_text","text":"Prepare launch notes [[cite:2]]."}]}]}""");
        }));
        var result = await Coordinator(http).Run(new DefaultHttpContext(), "What are my To Do tasks?", [], ["todo"], [], default);
        Assert.Equal(3, calls);
        Assert.Equal(2, result.Evidence.Length);
        Assert.Equal("Prepare launch notes [2].", result.Answer);
    }

    [Fact]
    public async Task DisabledModelToolsNeverExecute()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(
            """{"status":"completed","output":[{"type":"function_call","call_id":"call1","name":"graph_search_mail","arguments":"{\"query\":\"secret\"}"}]}"""))));
        var failure = await Assert.ThrowsAsync<ApiException>(() => Coordinator(http).Run(
            new DefaultHttpContext(), "calendar", [], ["calendar"], [], default));
        Assert.Equal("invalid_model_tool", failure.Code);
    }

    [Fact]
    public async Task RepeatedToolRoundsFailAtTheBudget()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(
            $$"""{"status":"completed","output":[{"type":"function_call","call_id":"call{{++calls}}","name":"graph_list_my_planner_tasks","arguments":"{}"}]}"""))));
        var failure = await Assert.ThrowsAsync<ApiException>(() => Coordinator(http).Run(
            new DefaultHttpContext(), "tasks", [], ["planner"], [], default));
        Assert.Equal("graph_tool_budget", failure.Code);
        Assert.Equal(ResponsesCoordinator.MaxRounds, calls);
    }

    [Fact]
    public async Task DuplicateCallIdsAreRejected()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(
            """{"status":"completed","output":[{"type":"function_call","call_id":"same","name":"graph_list_my_planner_tasks","arguments":"{}"}]}"""))));
        var failure = await Assert.ThrowsAsync<ApiException>(() => Coordinator(http).Run(
            new DefaultHttpContext(), "tasks", [], ["planner"], [], default));
        Assert.Equal("invalid_model_tool", failure.Code);
    }

    [Fact]
    public async Task ExcessiveToolCallsAreRejectedBeforeExecution()
    {
        var calls = Enumerable.Range(1, 9).Select(i => new
        {
            type = "function_call",
            call_id = $"call{i}",
            name = "graph_list_my_planner_tasks",
            arguments = "{}"
        });
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(
            JsonSerializer.Serialize(new { status = "completed", output = calls })))));
        var failure = await Assert.ThrowsAsync<ApiException>(() => Coordinator(http).Run(
            new DefaultHttpContext(), "tasks", [], ["planner"], [], default));
        Assert.Equal("graph_tool_budget", failure.Code);
    }

    [Theory]
    [InlineData("{\"query\":\"\"}")]
    [InlineData("{bad-json}")]
    public async Task ToolArgumentErrorsAreVisibleAndCannotProduceAnUngroundedAnswer(string arguments)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (++calls == 1) return Json(JsonSerializer.Serialize(new
            {
                status = "completed",
                output = new[] { new
                {
                    type = "function_call", call_id = "call1", name = "graph_search_mail", arguments
                } }
            }));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var output = json.RootElement.GetProperty("input").EnumerateArray()
                .Last(i => i.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            using var error = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            Assert.True(error.RootElement.GetProperty("isError").GetBoolean());
            return Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","id":"msg1","content":[{"type":"output_text","text":"Invented mailbox fact."}]}]}""");
        }));
        var result = await Coordinator(http).Run(new DefaultHttpContext(), "mail", [], ["mail"], [], default);
        Assert.DoesNotContain("Invented", result.Answer);
        Assert.Empty(result.Evidence);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task SearchIndexAndQueryIncludeUserIdForSecurityTrimming()
    {
        string? capturedUploadBody = null;
        string? capturedSearchBody = null;

        using var http = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/indexes/asrlocalrag", StringComparison.Ordinal))
            {
                return Json("""
                {
                  "fields": [
                    { "name": "id", "filterable": true },
                    { "name": "scope", "filterable": true },
                    { "name": "userId", "filterable": true },
                    { "name": "documentId", "filterable": true },
                    { "name": "title", "filterable": false },
                    { "name": "content", "filterable": false },
                    { "name": "locator", "filterable": false },
                    { "name": "url", "filterable": false },
                    { "name": "expiresAt", "filterable": true },
                    { "name": "contentVector", "filterable": false, "dimensions": 3 }
                  ]
                }
                """);
            }

            if (path.EndsWith("/openai/v1/embeddings", StringComparison.Ordinal))
            {
                return Json("""{"data":[{"embedding":[0.1,0.2,0.3]}]}""");
            }

            if (path.EndsWith("/docs/index", StringComparison.Ordinal))
            {
                capturedUploadBody = await request.Content!.ReadAsStringAsync();
                return Json("""{"value":[{"key":"doc1-0","status":true}]}""");
            }

            if (path.EndsWith("/docs/search", StringComparison.Ordinal))
            {
                capturedSearchBody = await request.Content!.ReadAsStringAsync();
                return Json("""
                {
                  "value": [
                    {
                      "id": "doc1-0",
                      "userId": "user-oid-123",
                      "documentId": "doc1",
                      "title": "brief.txt",
                      "content": "Authorized content",
                      "locator": "Page 1, passage 1",
                      "url": null
                    },
                    {
                      "id": "doc2-0",
                      "userId": "other-user-oid",
                      "documentId": "doc1",
                      "title": "brief.txt",
                      "content": "Leaked content",
                      "locator": "Page 1, passage 2",
                      "url": null
                    }
                  ]
                }
                """);
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:Endpoint"] = "https://example.openai.azure.com",
                ["OpenAI:EmbeddingDeployment"] = "text-embedding-3-large",
                ["OpenAI:EmbeddingDimensions"] = "3",
                ["Search:Endpoint"] = "https://example.search.windows.net",
                ["Search:Index"] = "asrlocalrag"
            })
            .Build();

        config["Knowledge:Backend"] = "Azure";
        var azure = new AzureServices(new Factory(http), new Credential(), config);
        var store = new SearchStore(azure, config, new FakeEnvironment());

        var caller = new Caller("tenant-1", "user-oid-123");
        var scope = caller.Scope(Guid.NewGuid().ToString());
        var document = new DocumentRecord(
            Id: "doc1",
            Scope: scope,
            Name: "brief.txt",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
            Chunks: 1,
            UserId: caller.User);
        var passages = Chunker.Split("doc1", "brief.txt", [new ExtractedPage(1, "Authorized content")], userId: caller.User);

        await store.Index(document, passages, default);

        Assert.NotNull(capturedUploadBody);
        using (var uploadJson = JsonDocument.Parse(capturedUploadBody!))
        {
            var uploadedChunk = uploadJson.RootElement.GetProperty("value")[0];
            Assert.Equal("user-oid-123", uploadedChunk.GetProperty("userId").GetString());
        }

        var results = await store.Search(scope, [document], "Authorized", default, caller.User);

        Assert.NotNull(capturedSearchBody);
        using (var searchJson = JsonDocument.Parse(capturedSearchBody!))
        {
            var filter = searchJson.RootElement.GetProperty("filter").GetString();
            Assert.Contains("userId eq 'user-oid-123'", filter);
        }

        var singlePassage = Assert.Single(results);
        Assert.Equal("Authorized content", singlePassage.Content);
        Assert.Equal("user-oid-123", singlePassage.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericQuestionsDoNotRequireKnowledgeRetrieval(bool enableAllSources)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.EndsWith("/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(
                """{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","id":"msg1","content":[{"type":"output_text","text":"Paris is the capital of France."}]}]}"""));
        }));

        var result = await Coordinator(http).Run(
            new DefaultHttpContext(),
            "What is the capital of France?",
            initial: [],
            sources: enableAllSources ? ["mail", "calendar", "todo", "planner", "teams", "people", "a2a"] : [],
            initialWarnings: [],
            ct: default);

        Assert.Equal("Paris is the capital of France.", result.Answer);
        Assert.Equal(1, calls);
        Assert.Empty(result.Evidence);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ModelCanInvokeSearchDocumentsToolWhenIntentRequiresDocumentLookup()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;

            Assert.Contains(
                body.GetProperty("tools").EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == "search_documents");

            if (calls == 1)
            {
                return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"doc_call_1","name":"search_documents","arguments":"{\"query\":\"lunar fact\"}"}]}""");
            }

            var output = body.GetProperty("input").EnumerateArray().Last(item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            using var evidence = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            var item = evidence.RootElement.GetProperty("items")[0];
            Assert.Equal(1, item.GetProperty("pageNumber").GetInt32());
            Assert.Equal(1, item.GetProperty("passageNumber").GetInt32());
            Assert.Equal("Page 1, passage 1", item.GetProperty("locator").GetString());

            return Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","id":"msg1","content":[{"type":"output_text","text":"The lunar project fact is Lavender-27 [[cite:1]]."}]}]}""");
        }));

        var result = await Coordinator(http).Run(
            new DefaultHttpContext(),
            "What does brief.txt say about the lunar project?",
            initial: [],
            sources: [],
            initialWarnings: [],
            ct: default,
            availableDocumentNames: ["brief.txt"],
            searchDocumentsAsync: (query, _) =>
            {
                Assert.Equal("lunar fact", query);
                return Task.FromResult(new[]
                {
                    new Passage("doc1-0", "doc1", "brief.txt", "Lavender-27", "Page 1, passage 1", UserId: "user-1")
                });
            });

        Assert.Equal(2, calls);
        Assert.Equal("The lunar project fact is Lavender-27 [1].", result.Answer);
        Assert.Single(result.Evidence);
    }

    [Fact]
    public async Task AgentFrameworkDiscoversAndQueriesApimSpecialistAsCitedKnowledge()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.EndsWith("/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;
            Assert.False(body.GetProperty("store").GetBoolean());
            var toolNames = body.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Equal(new[] { "list_a2a_agents", "ask_a2a_agent" }, toolNames);
            if (++calls == 1)
            {
                return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"discover","name":"list_a2a_agents","arguments":"{}"}]}""");
            }
            var output = body.GetProperty("input").EnumerateArray()
                .Last(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            using var toolResult = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            if (calls == 2)
            {
                Assert.Equal("mock-apim-knowledge", toolResult.RootElement[0].GetProperty("id").GetString());
                return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"ask","name":"ask_a2a_agent","arguments":"{\"agentId\":\"mock-apim-knowledge\",\"question\":\"When is the review?\"}"}]}""");
            }
            Assert.Equal("[[cite:1]]", toolResult.RootElement.GetProperty("items")[0].GetProperty("citation").GetString());
            return Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","id":"msg1","content":[{"type":"output_text","text":"The review is Friday [[cite:1]]."}]}]}""");
        }));
        var result = await Coordinator(http).Run(
            new DefaultHttpContext(), "Ask the specialist when the review is.", [], ["a2a"], [], default);
        Assert.Equal(3, calls);
        Assert.Equal("The review is Friday [1].", result.Answer);
        Assert.Equal(["mock-apim-knowledge", "mock-apim-knowledge"], result.Evidence.Select(item => item.OriginatingAgent!.Id));
        Assert.Equal("https://example.org/", result.Evidence[1].Url);
    }

    [Fact]
    public async Task DisabledA2aToolCannotBeInvokedByTheModel()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(
            """{"status":"completed","output":[{"type":"function_call","call_id":"ask","name":"ask_a2a_agent","arguments":"{\"agentId\":\"mock-apim-knowledge\",\"question\":\"facts\"}"}]}"""))));
        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            Coordinator(http).Run(new DefaultHttpContext(), "facts", [], ["mail"], [], default));
        Assert.Equal("invalid_model_tool", exception.Code);
    }

    [Fact]
    public async Task FrameworkDoesNotSwallowDelegatedAuthenticationChallenges()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(Json(
                """{"status":"completed","output":[{"type":"function_call","call_id":"doc","name":"search_documents","arguments":"{\"query\":\"facts\"}"}]}"""));
        }));
        var expected = new ApiException(401, "graph_consent_required", "Additional consent is required.");
        var exception = await Assert.ThrowsAsync<ApiException>(() => Coordinator(http).Run(
            new DefaultHttpContext(), "facts", [], [], [], default, ["brief.txt"],
            (_, _) => throw expected));
        Assert.Same(expected, exception);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"query\":\"facts\",\"userId\":\"another-user\"}")]
    [InlineData("{\"query\":\"\"}")]
    public async Task DocumentToolRejectsArgumentsBeforeSearching(string arguments)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(
            ++calls == 1 ? Json(JsonSerializer.Serialize(new
            {
                status = "completed",
                output = new[] { new { type = "function_call", call_id = "doc", name = "search_documents", arguments } }
            })) : Json("""{"status":"completed","output":[{"type":"message","role":"assistant","status":"completed","id":"msg1","content":[{"type":"output_text","text":"No data."}]}]}"""))));
        var result = await Coordinator(http).Run(
            new DefaultHttpContext(), "facts", [], [], [], default, ["brief.txt"],
            (_, _) => throw new InvalidOperationException("The search callback must not execute."));
        Assert.Empty(result.Evidence);
        Assert.Single(result.Warnings);
        Assert.Contains("Some tools failed", result.Answer);
    }

    [Fact]
    public async Task CachedTokenCredentialCachesUntilNearExpirationAndRefreshes()
    {
        var tokenCalls = 0;
        var currentExpiry = DateTimeOffset.UtcNow.AddHours(1);
        var inner = new DelegatingCredential(() =>
        {
            tokenCalls++;
            return new AccessToken($"token-{tokenCalls}", currentExpiry);
        });

        using var cached = new CachedTokenCredential(inner);
        var context = new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]);

        var first = await cached.GetTokenAsync(context, default);
        var second = await cached.GetTokenAsync(context, default);
        Assert.Equal("token-1", first.Token);
        Assert.Equal("token-1", second.Token);
        Assert.Equal(1, tokenCalls);

        // Simulate a token within the 5-minute refresh buffer
        currentExpiry = DateTimeOffset.UtcNow.AddMinutes(2);
        using var expiringCached = new CachedTokenCredential(new DelegatingCredential(() =>
        {
            tokenCalls++;
            return new AccessToken($"token-{tokenCalls}", currentExpiry);
        }));

        var third = await expiringCached.GetTokenAsync(context, default);
        currentExpiry = DateTimeOffset.UtcNow.AddHours(1);
        var fourth = await expiringCached.GetTokenAsync(context, default);
        Assert.NotEqual(third.Token, fourth.Token);
    }

    private sealed class DelegatingCredential(Func<AccessToken> factory) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            factory();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(factory());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupRetainsFailedDocumentAndRetriesWithoutBlockingOthers(bool timeout)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cleanup-tests-" + Guid.NewGuid().ToString("N"));
        var scope = new Caller("tenant", "user").Scope(Guid.NewGuid().ToString());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Search:Endpoint"] = "https://example.search.windows.net",
            ["Search:Index"] = "documents"
        }).Build();
        config["Knowledge:Backend"] = "Azure";
        config["Knowledge:DataDirectory"] = directory;
        var fail = true;
        using var http = new HttpClient(new Handler(async request =>
        {
            var payload = await request.Content!.ReadAsStringAsync();
            if (fail && payload.Contains("retry-0", StringComparison.Ordinal))
            {
                if (timeout) throw new OperationCanceledException("Transport timeout");
                throw new HttpRequestException("Temporary network failure");
            }
            return Json("""{"value":[{"status":true}]}""");
        }));
        var environment = new FakeEnvironment();
        var documents = new DocumentStore(config, environment);
        var search = new SearchStore(new AzureServices(new Factory(http), new Credential(), config),
            config, environment);
        using var history = new ConversationHistoryStore(config, environment, NullLogger<ConversationHistoryStore>.Instance);
        using var worker = new CleanupWorker(documents, search, NullLogger<CleanupWorker>.Instance, history);
        try
        {
            await documents.Commit(new DocumentRecord("retry", scope, "retry.txt", DateTimeOffset.UtcNow.AddMinutes(-1), 1), default);
            await documents.Commit(new DocumentRecord("other", scope, "other.txt", DateTimeOffset.UtcNow.AddMinutes(-1), 1), default);
            await worker.SweepExpiredDocumentsAsync(default);
            Assert.Equal("retry", Assert.Single(await documents.List(scope, default)).Id);
            fail = false;
            await worker.SweepExpiredDocumentsAsync(default);
            Assert.Empty(await documents.List(scope, default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupShutdownDoesNotFault()
    {
        var configuration = TestConfiguration.Create(
            directory: Path.Combine(Path.GetTempPath(), "cleanup-tests-" + Guid.NewGuid().ToString("N")));
        var environment = new FakeEnvironment();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected")));
        var documents = new DocumentStore(configuration, environment);
        using var history = new ConversationHistoryStore(configuration, environment, NullLogger<ConversationHistoryStore>.Instance);
        using var worker = new CleanupWorker(documents,
            new SearchStore(new AzureServices(new Factory(http), new Credential(), Config()), configuration, environment),
            NullLogger<CleanupWorker>.Instance, history);
        await worker.StartAsync(default);
        await worker.StopAsync(default);
        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.False(worker.ExecuteTask.IsFaulted);
    }

    internal sealed class FakeEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }
}
