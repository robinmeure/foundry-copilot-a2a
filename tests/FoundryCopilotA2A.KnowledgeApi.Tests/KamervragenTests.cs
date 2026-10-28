using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using FoundryCopilotA2A.Skills;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class KamervragenTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "kamervragen-tests-" + Guid.NewGuid().ToString("N"));
    private const string SearchResult = """
        {"value":[{"id":"chunk-1","parentId":"doc-1","level":"chunk","onderwerp":"AI in de overheid",
        "content":"The minister supports responsible AI.","thema":"Digitalisering","ministerie":"BZK",
        "partijen":["Partij A"],"indieners":["Example member"],"keyPoints":["Accountability"],
        "datum":"2026-10-20T00:00:00Z","documentNummer":"2026Z001",
        "blobPath":"private/path","metadata_storage_path":"https://storage.example/document?sig=secret"}]}
        """;

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task HybridSearchUsesIndexVectorizerAndSafeExactFiltersWithoutTouchingUploads()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.search.windows.net/indexes/kamervragen-openai/docs/search?api-version=2024-07-01",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;
            Assert.Equal("AI overheid", body.GetProperty("search").GetString());
            Assert.Equal("semantic", body.GetProperty("queryType").GetString());
            Assert.Equal("semantic", body.GetProperty("semanticConfiguration").GetString());
            Assert.Equal("freshness", body.GetProperty("scoringProfile").GetString());
            Assert.False(body.TryGetProperty("queryLanguage", out _),
                "The stable 2024-07-01 Search API rejects queryLanguage; language analysis belongs to the index.");
            Assert.Equal(8, body.GetProperty("top").GetInt32());
            var vector = Assert.Single(body.GetProperty("vectorQueries").EnumerateArray());
            Assert.Equal("text", vector.GetProperty("kind").GetString());
            Assert.Equal("AI overheid", vector.GetProperty("text").GetString());
            Assert.Equal("contentVector", vector.GetProperty("fields").GetString());
            Assert.Equal(50, vector.GetProperty("k").GetInt32());
            Assert.False(vector.TryGetProperty("vector", out _), "Do not use the uploads' 3,072-dimensional embeddings.");
            Assert.Equal("thema eq 'L''economie' and ministerie eq 'BZK' and documentNummer eq '2026Z001' and " +
                "partijen/any(p: p eq 'O''Brien') and datum ge 2026-10-01T00:00:00Z and datum lt 2026-11-01T00:00:00Z",
                body.GetProperty("filter").GetString());
            Assert.Equal("preFilter", body.GetProperty("vectorFilterMode").GetString());
            Assert.DoesNotContain("userId", body.GetProperty("filter").GetString());
            var select = body.GetProperty("select").GetString()!;
            Assert.DoesNotContain("blobPath", select);
            Assert.DoesNotContain("metadata_storage_path", select);
            Assert.DoesNotContain("contentVector", select);
            return Json(SearchResult);
        }));
        var skill = CreateSkill(http);
        var passage = Assert.Single(await skill.SearchAsync(Kamervragen, Args(new
        {
            query = "AI overheid",
            thema = "L'economie",
            ministerie = "BZK",
            partij = "O'Brien",
            documentNummer = "2026Z001",
            fromDate = "2026-10-01",
            toDate = "2026-10-31"
        }), ["kamervragen"], default));
        Assert.Equal(1, calls);
        Assert.Equal("kamervragen:chunk-1", passage.Id);
        Assert.Equal("kamervragen:doc-1", passage.DocumentId);
        Assert.Equal("AI in de overheid", passage.Title);
        Assert.Contains("2026Z001", passage.Locator);
        Assert.Contains("BZK", passage.Content);
        Assert.Contains("responsible AI", passage.Content);
        Assert.Null(passage.Url);
        Assert.Null(passage.UserId);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(passage));
    }

    [Fact]
    public async Task NoMatchesReturnEmptyEvidenceWithoutInventingFilters()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(json.RootElement.TryGetProperty("filter", out _));
            return Json("""{"value":[]}""");
        }));
        Assert.Empty(await CreateSkill(http).SearchAsync(Kamervragen, Args(new { query = "no matches" }), ["kamervragen"], default));
    }

    [Fact]
    public async Task SearchUsesTheConfiguredEndpointAndIndexWithoutCustomFormatChecks()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("http://search.internal/custom/indexes/shared.reference/docs/search?api-version=2024-07-01",
                request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("""{"value":[]}"""));
        }));
        var configuration = TestConfiguration.Create("Azure", values: new Dictionary<string, string?>
        {
            ["Kamervragen:Enabled"] = "true",
            ["Kamervragen:Endpoint"] = "http://search.internal/custom",
            ["Kamervragen:Index"] = "shared.reference"
        });
        var skill = new AzureAiSearchConnector(
            new AzureServices(new Factory(http), new SearchCredential(), configuration), configuration);

        Assert.Empty(await skill.SearchAsync(Kamervragen, Args(new { query = "AI" }), ["kamervragen"], default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":4}""")]
    [InlineData("""{"query":"x","filter":"userId eq 'another user'"}""")]
    [InlineData("""{"query":"x","endpoint":"https://evil.example"}""")]
    [InlineData("""{"query":"x","partij":[]}""")]
    [InlineData("""{"query":"x","fromDate":"tomorrow"}""")]
    [InlineData("""{"query":"x","fromDate":"2026-10-30","toDate":"2026-10-01"}""")]
    [InlineData("""{"query":"x","toDate":"9999-12-31"}""")]
    public async Task InvalidArgumentsNeverReachAzure(string arguments)
    {
        using var http = NoNetwork();
        var error = await Assert.ThrowsAsync<ApiException>(() => CreateSkill(http)
            .SearchAsync(Kamervragen, JsonDocument.Parse(arguments).RootElement, ["kamervragen"], default));
        Assert.Equal("invalid_tool_arguments", error.Code);
    }

    [Fact]
    public async Task OverlongQueriesAndUnselectedOrDisabledSourcesNeverReachAzure()
    {
        using var http = NoNetwork();
        var args = Args(new { query = "AI" });
        Assert.Equal("kamervragen_not_selected", (await Assert.ThrowsAsync<ApiException>(() =>
            CreateSkill(http).SearchAsync(Kamervragen, args, [], default))).Code);
        Assert.Equal("kamervragen_disabled", (await Assert.ThrowsAsync<ApiException>(() =>
            CreateSkill(http, enabled: false).SearchAsync(Kamervragen, args, ["kamervragen"], default))).Code);
        Assert.Equal("invalid_tool_arguments", (await Assert.ThrowsAsync<ApiException>(() =>
            CreateSkill(http).SearchAsync(Kamervragen, Args(new { query = new string('x', 2001) }), ["kamervragen"], default))).Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"value":{}}""")]
    [InlineData("""{"value":[{"content":"missing id"}]}""")]
    [InlineData("""{"value":[{"id":"1","content":42}]}""")]
    [InlineData("""{"value":[{"id":"1","content":"text","partijen":"not an array"}]}""")]
    public async Task MalformedSearchResultsFailExplicitly(string response)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(response))));
        var error = await Assert.ThrowsAsync<ApiException>(() => CreateSkill(http)
            .SearchAsync(Kamervragen, Args(new { query = "AI" }), ["kamervragen"], default));
        Assert.Equal("invalid_kamervragen_response", error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 502)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    public async Task LiveFailuresNeverSubstituteMockResults(HttpStatusCode upstream, int expected)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(upstream))));
        var error = await Assert.ThrowsAsync<ApiException>(() => CreateSkill(http)
            .SearchAsync(Kamervragen, Args(new { query = "AI" }), ["kamervragen"], default));
        Assert.Equal(expected, error.Status);
        Assert.Equal("azure_service_error", error.Code);
    }

    [Fact]
    public async Task LongResultsAreBoundedAndTooManyResultsFail()
    {
        using var longHttp = new HttpClient(new Handler(_ => Task.FromResult(Json(JsonSerializer.Serialize(new
        {
            value = new[] { new { id = "1", content = new string('x', 9000), onderwerp = new string('t', 500) } }
        })))));
        var passage = Assert.Single(await CreateSkill(longHttp).SearchAsync(Kamervragen, Args(new { query = "AI" }), ["kamervragen"], default));
        Assert.InRange(passage.Content.Length, 1, 3003);
        Assert.InRange(passage.Title.Length, 1, 243);
        using var tooManyHttp = new HttpClient(new Handler(_ => Task.FromResult(Json(JsonSerializer.Serialize(new
        {
            value = Enumerable.Range(0, 9).Select(id => new { id = id.ToString(), content = "text" })
        })))));
        Assert.Equal("invalid_kamervragen_response", (await Assert.ThrowsAsync<ApiException>(() =>
            CreateSkill(tooManyHttp).SearchAsync(Kamervragen, Args(new { query = "AI" }), ["kamervragen"], default))).Code);
    }

    [Fact]
    public async Task FatalSearchFailureReportsProgressAndEscapesTheModelLoop()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(
            request.RequestUri!.Host == "example.search.windows.net"
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : Json("""{"status":"completed","output":[{"type":"function_call","call_id":"c1","name":"search_kamervragen","arguments":"{\"query\":\"AI\"}"}]}"""))));
        var progress = new RecordingProgress();
        var error = await Assert.ThrowsAsync<ApiException>(() => AzureContractTests.Coordinator(http, CreateSkill(http))
            .Run(new DefaultHttpContext(), "AI", [], ["kamervragen"], [], default, progress: progress));
        Assert.Equal("azure_service_error", error.Code);
        Assert.Contains(progress.Statuses, status => status.Tool == ToolName && status.State == ChatStatus.Failed);
    }

    [Fact]
    public void StrictToolSchemaMatchesTheIndexContract()
    {
        using var http = NoNetwork();
        var tool = Assert.Single(CreateSkill(http).GetTools(Kamervragen, new SkillScope(["kamervragen"], null)));
        Assert.Equal("search_kamervragen", tool.Name);
        var parameters = tool.Parameters;
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(parameters.GetProperty("properties").EnumerateObject().Select(property => property.Name),
            parameters.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task AgentFrameworkInvokesLiveSearchAndAssignsCitationsAndProgress()
    {
        var modelCalls = 0;
        var searchCalls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.Host == "example.search.windows.net")
            {
                searchCalls++;
                return Json(SearchResult);
            }
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(new[] { "load_skill", "read_skill_resource" }.Concat(modelCalls == 0 ? [] : [ToolName]),
                json.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
            if (++modelCalls == 1)
            {
                return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"c0","name":"load_skill","arguments":"{\"names\":[\"kamervragen\"]}"}]}""");
            }
            var input = json.RootElement.GetProperty("input").EnumerateArray();
            if (modelCalls == 2)
            {
                var loaded = input.Last(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
                using var skill = JsonDocument.Parse(loaded.GetProperty("output").GetString()!);
                Assert.Equal(["references/fields.md"], skill.RootElement.GetProperty("skills")[0].GetProperty("resources").EnumerateArray().Select(value => value.GetString()));
                return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"c1","name":"search_kamervragen","arguments":"{\"query\":\"AI overheid\"}"}]}""");
            }
            var output = input.Last(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            using var evidence = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            Assert.Equal("[[cite:1]]", evidence.RootElement.GetProperty("items")[0].GetProperty("citation").GetString());
            Assert.Contains("not exhaustive", evidence.RootElement.GetProperty("coverage").GetString());
            return Json("""{"status":"completed","output":[{"type":"message","id":"msg1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Responsible AI [[cite:1]]."}]}]}""");
        }));
        var progress = new RecordingProgress();
        var answer = await AzureContractTests.Coordinator(http, CreateSkill(http)).Run(new DefaultHttpContext(),
            "Find parliamentary questions about AI", [], ["kamervragen"], [], default, progress: progress);
        Assert.Equal(3, modelCalls);
        Assert.Equal(1, searchCalls);
        Assert.Equal("Responsible AI [1].", answer.Answer);
        Assert.Single(answer.Evidence);
        Assert.Contains(progress.Statuses, status => status.Tool == ToolName && status.State == ChatStatus.Started);
        Assert.Contains(progress.Statuses, status => status.Message == "Found 1 Kamervragen passage" && status.State == ChatStatus.Completed);
    }

    [Fact]
    public async Task ModelCannotInvokeSearchWhenTheUserRemovedTheSource()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("example.openai.azure.com", request.RequestUri!.Host);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("tools", out var tools))
            {
                Assert.DoesNotContain(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == ToolName);
            }
            return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"c1","name":"search_kamervragen","arguments":"{\"query\":\"AI\"}"}]}""");
        }));
        var error = await Assert.ThrowsAsync<ApiException>(() => AzureContractTests.Coordinator(http, CreateSkill(http))
            .Run(new DefaultHttpContext(), "AI", [], [], [], default));
        Assert.Equal("invalid_model_tool", error.Code);
    }

    [Fact]
    public async Task MockApiAdvertisesSearchAndStreamsCitedEvidenceWithoutAzure()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new Factory(NoNetwork()));
            });
        });
        using var client = api.CreateClient();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/capabilities");
        Assert.True(capabilities.GetProperty("kamervragen").GetBoolean());
        using var response = await client.PostAsJsonAsync("/api/chat/stream",
            AgUi.Run(Guid.NewGuid().ToString(), "Kamervragen about AI", ["kamervragen"]));
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStringAsync();
        Assert.Contains("search_kamervragen", stream);
        Assert.Contains("Searching Kamervragen", stream);
        Assert.Contains("\"type\":\"TEXT_MESSAGE_CONTENT\"", stream);
        Assert.Contains("\"type\":\"RUN_FINISHED\"", stream);
        Assert.Contains("Mock Kamervragen", stream);
        Assert.Contains("[1]", stream);
        Assert.DoesNotContain("\"type\":\"RUN_ERROR\"", stream);
    }

    private const string ToolName = "search_kamervragen";
    private static SkillDefinition Kamervragen => TestSkills.Get("kamervragen");

    private static AzureAiSearchConnector CreateSkill(HttpClient http, bool enabled = true) => new(
        new AzureServices(new Factory(http), new SearchCredential(), new ConfigurationBuilder().Build()),
        TestConfiguration.Create("Azure", values: new Dictionary<string, string?>
        {
            ["Kamervragen:Enabled"] = enabled.ToString(),
            ["Kamervragen:Endpoint"] = "https://example.search.windows.net"
        }));

    private static HttpClient NoNetwork() => new(new Handler(_ => throw new InvalidOperationException("Unexpected network request.")));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class SearchCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal(["https://search.azure.com/.default"], requestContext.Scopes);
            return new("test-only", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class RecordingProgress : ChatProgress
    {
        public List<ChatStatus> Statuses { get; } = [];
        public override ValueTask ReportAsync(ChatStatus status, CancellationToken ct)
        {
            Statuses.Add(status);
            return ValueTask.CompletedTask;
        }
    }
}
