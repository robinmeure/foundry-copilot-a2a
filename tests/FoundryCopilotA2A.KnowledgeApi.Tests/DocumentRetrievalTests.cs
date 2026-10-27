using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using FoundryCopilotA2A.KnowledgeApi.Services.Knowledge;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class DocumentRetrievalTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Page 7, passage 12", 7, 12)]
    [InlineData("Page 7", 7, null)]
    [InlineData("Live Graph / calendar", null, null)]
    [InlineData("Section 4", null, null)]
    [InlineData("Page 0, passage 1", null, null)]
    [InlineData("Page 99999999999999999999", null, null)]
    public void PageMetadataComesOnlyFromRecognizedExtractionLocators(string locator, int? page, int? passage)
    {
        var chunk = new Passage("doc-0", "doc", "report.txt", "Text", locator);
        Assert.Equal(page, chunk.PageNumber);
        Assert.Equal(passage, chunk.PassageNumber);
    }

    [Fact]
    public void ChunkMetadataKeepsPageNumbersDistinctFromChunkSequence()
    {
        var passages = Chunker.Split("doc", "report.txt", [new(7, new string('x', 5000)), new(12, "Last page")]);
        Assert.Equal(new int?[] { 7, 7, 7, 12 }, passages.Select(p => p.PageNumber));
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, passages.Select(p => p.PassageNumber));
        var legacy = JsonSerializer.Deserialize<Passage>(
            """{"id":"doc-3","documentId":"doc","title":"report.txt","content":"Last page","locator":"Page 12, passage 4"}""", WebJson)!;
        var json = JsonSerializer.SerializeToElement(legacy, WebJson);
        Assert.Equal(12, json.GetProperty("pageNumber").GetInt32());
        Assert.Equal(4, json.GetProperty("passageNumber").GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":[null]}""")]
    [InlineData("""{"value":[{"id":"first-0"}]}""")]
    public async Task LiveReadRejectsMalformedChunkResponses(string json)
    {
        using var fixture = new Fixture(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Schema()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }), live: true);
        Assert.Equal("invalid_document_response", (await Assert.ThrowsAsync<ApiException>(
            () => fixture.Store.ReadDocument("scope", "user", Document("first", 1), default))).Code);
    }

    [Theory]
    [InlineData("whole_document")]
    [InlineData("exhaustive")]
    public async Task ReadsAllChunksInOrderWithoutTheEightResultCap(string mode)
    {
        using var fixture = new Fixture();
        var (document, passages) = await fixture.IndexAsync("first", 23);
        var request = new DocumentRetrievalRequest(mode, "Read everything", [document.Id]);
        var result = await fixture.Retrieval.ReadAsync("scope", "user", [document], request, ChatProgress.None, default);
        result = await fixture.Retrieval.AnalyzeAsync(result, request.Query, ChatProgress.None, default);

        Assert.Equal(passages.Select(p => p.Id), result.Passages.Select(p => p.Id));
        Assert.Equal(23, Assert.Single(result.Coverage).ReadChunks);
        Assert.Equal(23, result.Findings.Length);
        Assert.Contains("Fact 22", result.Findings[^1].Text);
        Assert.False(result.Synthesized);
    }

    [Fact]
    public async Task FocusedKeepsEightResultsAndContextualAddsOrderedDeduplicatedNeighbors()
    {
        using var fixture = new Fixture();
        var (document, _) = await fixture.IndexAsync("first", 20);
        var focused = await fixture.ReadAsync("focused", [document], "Fact");
        Assert.Equal(8, focused.Passages.Length);
        var contextual = await fixture.ReadAsync("contextual", [document], "Fact");
        Assert.Equal(9, contextual.Passages.Length);
        Assert.Equal(Enumerable.Range(0, 9).Select(i => $"first-{i}"), contextual.Passages.Select(p => p.Id));
        Assert.Equal(20, Assert.Single(contextual.Coverage).ExpectedChunks);
    }

    [Fact]
    public async Task ComparisonIncludesEveryChunkFromEachDocument()
    {
        using var fixture = new Fixture();
        var (first, _) = await fixture.IndexAsync("first", 17);
        var (second, _) = await fixture.IndexAsync("second", 13);
        var result = await fixture.ReadAsync("comparison", [first, second]);
        Assert.Equal(30, result.Passages.Length);
        Assert.Equal(new[] { 17, 13 }, result.Coverage.Select(c => c.ReadChunks));
        Assert.Equal("second-12", result.Passages[^1].Id);
    }

    [Theory]
    [InlineData("""{"mode":"everything","query":"facts","documentIds":["first"]}""")]
    [InlineData("""{"mode":"whole_document","query":"facts","documentIds":[]}""")]
    [InlineData("""{"mode":"comparison","query":"facts","documentIds":["first"]}""")]
    [InlineData("""{"mode":"whole_document","query":"facts","documentIds":["first","first"]}""")]
    [InlineData("""{"mode":"whole_document","query":"facts","documentIds":["first"],"userId":"someone"}""")]
    [InlineData("""{"mode":"whole_document","query":"facts","documentIds":[1]}""")]
    [InlineData("""{"mode":"whole_document","query":"","documentIds":["first"]}""")]
    public void RejectsMalformedOrScopeOverridingArguments(string json)
    {
        Assert.Equal("invalid_tool_arguments",
            Assert.Throws<ApiException>(() => DocumentRetrievalRequest.Parse(JsonSerializer.Deserialize<JsonElement>(json))).Code);
    }

    [Fact]
    public void RejectsUnavailableDocumentsAndOversizedFullReads()
    {
        var document = Document("first", 1500);
        Assert.Equal("document_unavailable", Assert.Throws<ApiException>(() => DocumentRetrieval.SelectDocuments(
            new("whole_document", "facts", ["other"]), [document])).Code);
        Assert.Equal("document_retrieval_budget", Assert.Throws<ApiException>(() => DocumentRetrieval.SelectDocuments(
            new("comparison", "facts", ["first", "second"]), [document, Document("second", 1500)])).Code);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("user")]
    [InlineData("expired")]
    [InlineData("pending")]
    public async Task OrderedReadRejectsUnavailableMetadataBeforeReading(string invalid)
    {
        using var fixture = new Fixture();
        var document = Document("first", 1);
        document = invalid switch
        {
            "scope" => document with { Scope = "other" },
            "user" => document with { UserId = "other" },
            "expired" => document with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            _ => document with { Status = "Pending" }
        };
        Assert.Equal("document_unavailable", (await Assert.ThrowsAsync<ApiException>(
            () => fixture.Store.ReadDocument("scope", "user", document, default))).Code);
    }

    [Fact]
    public async Task MissingChunkNeverProducesACompleteRead()
    {
        using var fixture = new Fixture();
        var (document, _) = await fixture.IndexAsync("first", 12);
        File.Delete(Path.Combine(fixture.Directory, "scope", "mock-passages", "first-9.json"));
        Assert.Equal("document_incomplete", (await Assert.ThrowsAsync<ApiException>(
            () => fixture.ReadAsync("whole_document", [document]))).Code);
    }

    [Fact]
    public async Task LiveReadPagesWithScopeFiltersAndRestoresNumericOrderWithoutEmbeddings()
    {
        var pages = 0;
        var document = Document("first", 1103);
        using var fixture = new Fixture(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Schema();
            }
            Assert.EndsWith("/docs/search", request.RequestUri!.AbsolutePath);
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal("*", body.GetProperty("search").GetString());
            Assert.False(body.TryGetProperty("vectorQueries", out _));
            var filter = body.GetProperty("filter").GetString()!;
            Assert.Contains("scope eq 'scope'", filter);
            Assert.Contains("userId eq 'user'", filter);
            Assert.Contains("documentId eq 'first'", filter);
            Assert.Contains("expiresAt gt ", filter);
            var skip = body.GetProperty("skip").GetInt32();
            Assert.Equal(pages++ * 500, skip);
            return Json(new
            {
                value = Enumerable.Range(skip, body.GetProperty("top").GetInt32()).Reverse()
                    .Select(i => Passage(document, i))
            });
        }, live: true);
        var passages = await fixture.Store.ReadDocument("scope", "user", document, default);
        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(0, 1103).Select(i => $"first-{i}"), passages.Select(p => p.Id));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("document")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task LiveReadFailsClosedOnIncompleteOrUnauthorizedResults(string problem)
    {
        var document = Document("first", 2);
        var first = Passage(document, 0);
        var second = problem switch
        {
            "user" => Passage(document, 1) with { UserId = "other" },
            "document" => Passage(document, 1) with { DocumentId = "other" },
            "missing" => Passage(document, 2),
            _ => first
        };
        using var fixture = new Fixture(request => Task.FromResult(
            request.Method == HttpMethod.Get ? Schema() : Json(new { value = new[] { first, second } })), live: true);
        Assert.Equal("document_incomplete", (await Assert.ThrowsAsync<ApiException>(
            () => fixture.Store.ReadDocument("scope", "user", document, default))).Code);
    }

    [Theory]
    [InlineData("whole_document")]
    [InlineData("exhaustive")]
    [InlineData("comparison")]
    public async Task LargeReadsAnalyzeEveryBatchAndKeepOriginalSourceIds(string mode)
    {
        var seen = new HashSet<string>();
        var modelCalls = 0;
        using var fixture = new Fixture(async request =>
        {
            modelCalls++;
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.Equal("json_schema", body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            var userInput = body.GetProperty("input").EnumerateArray().Last(item => item.GetProperty("role").GetString() == "user");
            Assert.InRange(Encoding.UTF8.GetByteCount(userInput.GetProperty("content")[0].GetProperty("text").GetString()!),
                1, DocumentRetrieval.BatchInputBytes);
            var input = JsonSerializer.Deserialize<JsonElement>(
                userInput.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal(mode, input.GetProperty("mode").GetString());
            Assert.Contains("every", input.GetProperty("question").GetString());
            var batch = input.GetProperty("passages").EnumerateArray().ToArray();
            foreach (var passage in batch)
            {
                Assert.True(seen.Add(passage.GetProperty("id").GetString()!));
                Assert.Equal(
                    $"Page {passage.GetProperty("pageNumber").GetInt32()}, passage {passage.GetProperty("passageNumber").GetInt32()}",
                    passage.GetProperty("locator").GetString());
            }
            return ModelResponse(JsonSerializer.Serialize(new
            {
                findings = batch.Select(p => new
                {
                    text = "Extracted " + p.GetProperty("id").GetString(),
                    passageIds = new[] { p.GetProperty("id").GetString() }
                })
            }));
        }, live: true);
        var passages = Enumerable.Range(0, 100)
            .Select(i => Passage(Document(i < 60 ? "first" : "second", 100), i) with { Content = new string('x', 2200) }).ToArray();
        var result = await fixture.Retrieval.AnalyzeAsync(
            new(mode, passages, [new("first", "first.txt", 60, 60), new("second", "second.txt", 40, 40)], [], false),
            "Find every obligation", ChatProgress.None, default);

        Assert.True(modelCalls > 1);
        Assert.Equal(100, seen.Count);
        Assert.Equal(100, result.Findings.Length);
        Assert.True(result.Synthesized);
        Assert.Equal(passages, result.Passages);
        Assert.All(result.Findings, finding => Assert.Contains(Assert.Single(finding.PassageIds), seen));
    }

    [Theory]
    [InlineData("""{"findings":[{"text":"invented","passageIds":["other-0"]}]}""", "completed", "invalid_document_analysis")]
    [InlineData("""{"findings":[{"text":"invented [[cite:99]]","passageIds":["first-0"]}]}""", "completed", "invalid_document_analysis")]
    [InlineData("""{"findings":[{"text":"unattributed","passageIds":[]}]}""", "completed", "invalid_document_analysis")]
    [InlineData("not json", "completed", "invalid_document_analysis")]
    [InlineData("""{"findings":[]}""", "incomplete", "document_analysis_incomplete")]
    public async Task BatchAnalysisRejectsInvalidOrTruncatedFindings(string text, string status, string code)
    {
        using var fixture = new Fixture(_ => Task.FromResult(ModelResponse(text, status)), live: true);
        var passages = Enumerable.Range(0, 30).Select(i => Passage(Document("first", 30), i) with { Content = new string('x', 2200) }).ToArray();
        var exception = await Assert.ThrowsAsync<ApiException>(() => fixture.Retrieval.AnalyzeAsync(
            new("exhaustive", passages, [new("first", "first.txt", 30, 30)], [], false),
            "Every obligation", ChatProgress.None, default));
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task OversizedFindingsFailExplicitlyInsteadOfDroppingBatches()
    {
        using var fixture = new Fixture();
        var (document, _) = await fixture.IndexAsync("first", 100, 2200);
        var read = await fixture.ReadAsync("exhaustive", [document]);
        Assert.Equal("document_analysis_budget", (await Assert.ThrowsAsync<ApiException>(
            () => fixture.Retrieval.AnalyzeAsync(read, "Every fact", ChatProgress.None, default))).Code);
    }

    [Fact]
    public async Task CancelledBatchAnalysisDoesNotCallTheModel()
    {
        using var fixture = new Fixture(live: true);
        var passages = Enumerable.Range(0, 30).Select(i => Passage(Document("first", 30), i) with { Content = new string('x', 2200) }).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Retrieval.AnalyzeAsync(
            new("whole_document", passages, [], [], false), "Summary", ChatProgress.None, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameworkReturnsMoreThanEightyCitationsAndCanStillUseGraph(bool longQuestion)
    {
        var calls = 0;
        var question = longQuestion ? new string('\u6F22', 4000) : "read all";
        using var fixture = new Fixture(async request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            if (++calls == 1)
            {
                Assert.Contains(body.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == DocumentRetrieval.ToolName);
                return ToolCall(DocumentRetrieval.ToolName, JsonSerializer.Serialize(new
                {
                    mode = "whole_document",
                    query = question,
                    documentIds = new[] { "first" }
                }), "read");
            }
            var outputs = body.GetProperty("input").EnumerateArray()
                .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output").ToArray();
            if (calls == 2)
            {
                var result = JsonSerializer.Deserialize<JsonElement>(outputs[^1].GetProperty("output").GetString()!);
                Assert.True(result.GetProperty("complete").GetBoolean());
                Assert.Equal(95, result.GetProperty("items").GetArrayLength());
                Assert.Equal("[[cite:95]]", result.GetProperty("items")[94].GetProperty("sources")[0].GetProperty("citation").GetString());
                Assert.Equal(95, result.GetProperty("items")[94].GetProperty("sources")[0].GetProperty("pageNumber").GetInt32());
                Assert.Equal(95, result.GetProperty("items")[94].GetProperty("sources")[0].GetProperty("passageNumber").GetInt32());
                return ToolCall("graph_list_todo_lists", "{}", "todo");
            }
            return ModelResponse("The last fact is 94 [[cite:95]].");
        });
        var (document, passages) = await fixture.IndexAsync("first", 95);
        var progress = new Progress();
        var result = await AzureContractTests.Coordinator(fixture.Http).Run(
            new DefaultHttpContext(), "Read the whole document", [], ["todo"], [], default,
            availableDocumentNames: ["first.txt (id: first, chunks: 95)"],
            searchDocumentsAsync: (_, _) => Task.FromResult(passages.Take(8).ToArray()),
            progress: progress,
            retrieveDocumentsAsync: async (request, ct) =>
            {
                Assert.Equal(question, request.Query);
                var read = await fixture.Retrieval.ReadAsync("scope", "user", [document], request, progress, ct);
                return await fixture.Retrieval.AnalyzeAsync(read, request.Query, progress, ct);
            });
        Assert.Equal(3, calls);
        Assert.True(result.Evidence.Length > 95);
        Assert.Contains("[95]", result.Answer);
        Assert.Contains(progress.Statuses, status => status.Tool == DocumentRetrieval.ToolName && status.State == ChatStatus.Completed);
    }

    [Fact]
    public async Task UnavailableRetrievalToolIsRejectedBeforeInvocation()
    {
        using var fixture = new Fixture(_ => Task.FromResult(ToolCall(
            DocumentRetrieval.ToolName, """{"mode":"whole_document","query":"all","documentIds":["first"]}""", "read")));
        Assert.Equal("invalid_model_tool", (await Assert.ThrowsAsync<ApiException>(() =>
            AzureContractTests.Coordinator(fixture.Http).Run(new DefaultHttpContext(), "read", [], [], [], default))).Code);
    }

    [Fact]
    public async Task TruncatedFinalAnswerIsNotReportedAsComplete()
    {
        using var fixture = new Fixture(_ => Task.FromResult(ModelResponse("Partial findings", "incomplete")));
        Assert.Equal("incomplete_response", (await Assert.ThrowsAsync<ApiException>(() =>
            AzureContractTests.Coordinator(fixture.Http).Run(new DefaultHttpContext(), "read", [], [], [], default))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnowledgeServiceUsesTheCurrentCatalogAndRechecksDeletionAfterAnalysis(bool deleteDuringAnalysis)
    {
        DocumentRecord? document = null;
        var calls = 0;
        using var fixture = new Fixture(async request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            if (++calls == 1)
            {
                Assert.Contains(document!.Id, body.GetProperty("input").GetRawText());
                return ToolCall(DocumentRetrieval.ToolName, JsonSerializer.Serialize(new
                {
                    mode = "whole_document",
                    query = "Summarize every section",
                    documentIds = new[] { document.Id }
                }), "read");
            }
            var output = body.GetProperty("input").EnumerateArray().Last(item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            var result = JsonSerializer.Deserialize<JsonElement>(output.GetProperty("output").GetString()!);
            if (deleteDuringAnalysis)
            {
                Assert.Equal("document_unavailable", result.GetProperty("error").GetString());
                return ModelResponse("The document was removed.");
            }
            Assert.True(result.GetProperty("complete").GetBoolean());
            Assert.Equal(document!.Chunks, result.GetProperty("documents")[0].GetProperty("readChunks").GetInt32());
            Assert.Equal(document.Chunks, result.GetProperty("items").GetArrayLength());
            return ModelResponse($"All sections read [[cite:{document.Chunks}]].");
        });
        using var api = new Api(fixture);
        using var client = api.CreateClient();
        var service = ActivatorUtilities.CreateInstance<KnowledgeService>(api.Services,
            TestConfiguration.Create("Azure", fixture.Directory));
        var caller = new Caller("local", "mock-user");
        var conversation = Guid.NewGuid().ToString();
        var scope = caller.Scope(conversation);
        var text = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"Section {i}: " + new string('x', 2200)));
        document = await service.Import(scope, "report.txt", Encoding.UTF8.GetBytes(text), null, null, null, default, caller.User);
        Assert.True(document.Chunks > 8);
        var progress = new DeleteDuringAnalysis(api.Services.GetRequiredService<DocumentStore>(), document, deleteDuringAnalysis);
        var result = await service.Chat(new DefaultHttpContext(), caller, new(conversation, "Summarize every section"), default, progress);
        Assert.Equal(2, calls);
        if (deleteDuringAnalysis)
        {
            Assert.True(progress.Deleted);
            Assert.Empty(result.Evidence);
            Assert.Contains(result.Warnings, warning => warning.Contains("removed", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Equal(document.Chunks, result.Evidence.Length);
            Assert.Contains($"[{document.Chunks}]", result.Answer);
        }
    }

    private static DocumentRecord Document(string id, int chunks) =>
        new(id, "scope", id + ".txt", DateTimeOffset.UtcNow.AddDays(1), chunks, UserId: "user");

    private static Passage Passage(DocumentRecord document, int index) =>
        new($"{document.Id}-{index}", document.Id, document.Name, $"Fact {index}",
            $"Page {index + 1}, passage {index + 1}", UserId: document.UserId);

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, WebJson), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Schema() => Json(new
    {
        fields = new[] { "id", "scope", "userId", "documentId", "title", "content", "locator", "url", "expiresAt", "contentVector" }
            .Select(name => new { name, filterable = true, dimensions = 3 })
    });

    private static HttpResponseMessage ModelResponse(string text, string status = "completed") => Json(new
    {
        status,
        incomplete_details = status == "incomplete" ? new { reason = "max_output_tokens" } : null,
        output = new[] { new { type = "message", role = "assistant", status, id = "msg1", content = new[] { new { type = "output_text", text } } } }
    });

    private static HttpResponseMessage ToolCall(string name, string arguments, string callId) => Json(new
    {
        status = "completed",
        output = new[] { new { type = "function_call", call_id = callId, name, arguments } }
    });

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "retrieval-tests-" + Guid.NewGuid().ToString("N"));
        public HttpClient Http { get; }
        public SearchStore Store { get; }
        public DocumentRetrieval Retrieval { get; }
        public AgentModelClient Model { get; }

        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>>? handler = null, bool live = false)
        {
            Http = new HttpClient(new Handler(handler ?? (_ => throw new InvalidOperationException("No network expected"))));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:Endpoint"] = "https://example.openai.azure.com",
                ["OpenAI:ChatDeployment"] = "test-model",
                ["OpenAI:EmbeddingDimensions"] = "3",
                ["Search:Endpoint"] = "https://example.search.windows.net",
                ["Search:Index"] = "documents"
            }).Build();
            configuration["Knowledge:Backend"] = live ? "Azure" : "Mock";
            configuration["Knowledge:DataDirectory"] = Directory;
            var factory = new Factory(Http);
            Model = new AgentModelClient(factory, new Credential(), configuration);
            Store = new(new AzureServices(factory, new Credential(), configuration), configuration, new AzureContractTests.FakeEnvironment());
            Retrieval = new(Store, Model, configuration);
        }

        public async Task<(DocumentRecord, Passage[])> IndexAsync(string id, int count, int? size = null)
        {
            var document = Document(id, count);
            var passages = Enumerable.Range(0, count).Select(i => Passage(document, i)).ToArray();
            if (size is { } length)
            {
                passages = passages.Select(p => p with { Content = new string('x', length) }).ToArray();
            }
            await Store.Index(document, passages, default);
            return (document, passages);
        }

        public Task<DocumentRetrievalResult> ReadAsync(string mode, DocumentRecord[] documents, string query = "all") =>
            Retrieval.ReadAsync("scope", "user", documents, new(mode, query, documents.Select(d => d.Id).ToArray()), ChatProgress.None, default);

        public void Dispose()
        {
            Http.Dispose();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, true);
            }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class Api(Fixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, fixture.Directory);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AgentModelClient>();
                services.AddSingleton(fixture.Model);
            });
        }
    }

    private sealed class DeleteDuringAnalysis(DocumentStore store, DocumentRecord document, bool enabled) : ChatProgress
    {
        public bool Deleted { get; private set; }
        public override ValueTask ReportAsync(ChatStatus status, CancellationToken cancellationToken)
        {
            if (enabled && !Deleted && status.Message.StartsWith("Analyzing document batch", StringComparison.Ordinal))
            {
                store.Remove(document);
                Deleted = true;
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Progress : ChatProgress
    {
        public List<ChatStatus> Statuses { get; } = [];
        public override ValueTask ReportAsync(ChatStatus status, CancellationToken cancellationToken)
        {
            Statuses.Add(status);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
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
