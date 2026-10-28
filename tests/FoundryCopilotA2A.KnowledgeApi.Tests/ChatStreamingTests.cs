using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class ChatStreamingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "chat-streaming-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void CitationFormatterHoldsBackPartialMarkersUntilTheyComplete()
    {
        var formatter = new CitationStreamFormatter();
        var released = new List<string>
        {
            formatter.Append("The value [[ci"),
            formatter.Append("te:1]"),
            formatter.Append("] is 42 [[cite:x]] and ["),
            formatter.Append("see notes] [[cite:2]].")
        };
        released.Add(formatter.Flush());

        Assert.Equal("The value ", released[0]);
        Assert.Equal("", released[1]);
        Assert.Equal("[1] is 42  and ", released[2]);
        Assert.Equal("[see notes] [2].", released[3]);
        Assert.Equal("", released[4]);
    }

    [Fact]
    public void CitationFormatterFlushReleasesUnfinishedText()
    {
        var formatter = new CitationStreamFormatter();
        Assert.Equal("Done ", formatter.Append("Done [[cite:"));
        Assert.Equal("[[cite:", formatter.Flush());
    }

    [Fact]
    public async Task BoundedStreamingRejectsRepeatedToolCallIds()
    {
        using var client = new BoundedChatClient(new StreamingClient(
            [
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call1", "graph_search_mail")]),
                new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call1", "graph_search_mail")])
            ]),
            new HashSet<string>(["graph_search_mail"]), () => { });

        var failure = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });
        Assert.Equal("invalid_model_tool", failure.Code);
    }

    [Fact]
    public async Task BoundedStreamingRejectsIncompleteResponses()
    {
        using var client = new BoundedChatClient(new StreamingClient(
            [
                new ChatResponseUpdate(ChatRole.Assistant, "partial"),
                new ChatResponseUpdate { FinishReason = ChatFinishReason.Length }
            ]),
            new HashSet<string>(), () => { });

        var failure = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });
        Assert.Equal("incomplete_response", failure.Code);
    }

    [Fact]
    public async Task StreamingToolLoopReportsToolStatusAndStreamsDisplayText()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.False(body.GetProperty("store").GetBoolean());
            calls++;
            if (calls == 1)
            {
                return Sse(
                    Created(0),
                    ("response.output_item.done", """{"type":"response.output_item.done","sequence_number":1,"output_index":0,"item":{"type":"reasoning","id":"r1","summary":[],"encrypted_content":"opaque"}}"""),
                    ("response.output_item.done", """{"type":"response.output_item.done","sequence_number":2,"output_index":1,"item":{"type":"function_call","id":"fc1","call_id":"call1","name":"graph_list_todo_lists","arguments":"{}","status":"completed"}}"""),
                    Completed(3, """[{"type":"reasoning","id":"r1","summary":[],"encrypted_content":"opaque"},{"type":"function_call","id":"fc1","call_id":"call1","name":"graph_list_todo_lists","arguments":"{}","status":"completed"}]"""));
            }

            var input = body.GetProperty("input").EnumerateArray().ToArray();
            Assert.Contains(input, item => item.TryGetProperty("encrypted_content", out var encrypted) && encrypted.GetString() == "opaque");
            Assert.Contains(input, item => item.TryGetProperty("call_id", out var callId) && callId.GetString() == "call1" &&
                item.GetProperty("type").GetString() == "function_call_output");
            return Sse(
                Created(0),
                ("response.output_item.added", """{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"type":"message","id":"msg1","role":"assistant","status":"in_progress","content":[]}}"""),
                Delta(2, "Your list is "),
                Delta(3, "Tasks [[cite:"),
                Delta(4, "1]]."),
                ("response.output_item.done", """{"type":"response.output_item.done","sequence_number":5,"output_index":0,"item":{"type":"message","id":"msg1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Your list is Tasks [[cite:1]].","annotations":[]}]}}"""),
                Completed(6, """[{"type":"message","id":"msg1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Your list is Tasks [[cite:1]].","annotations":[]}]}]"""));
        }));

        var progress = new RecordingProgress();
        var result = await AzureContractTests.Coordinator(http).Run(
            new DefaultHttpContext(), "Which To Do lists do I have?", [], ["todo"], [], default, progress: progress);

        Assert.Equal(2, calls);
        Assert.Equal("Your list is Tasks [1].", result.Answer);
        Assert.Equal(result.Answer, progress.Text.ToString());
        Assert.Collection(progress.Statuses,
            status => Assert.Equal("Working out what you need", status.Message),
            status =>
            {
                Assert.Equal("Listing your To Do lists", status.Message);
                Assert.Equal(ChatStatus.Started, status.State);
                Assert.Equal("graph_list_todo_lists", status.Tool);
            },
            status =>
            {
                Assert.Matches(@"^Found \d+ To Do lists?$", status.Message);
                Assert.Equal(ChatStatus.Completed, status.State);
            });
    }

    [Fact]
    public async Task WholeDocumentToolStreamsAnswerWithCitationsBeyondTheOldLimit()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
            if (++calls == 1)
            {
                const string call = """{"type":"function_call","id":"fc1","call_id":"read","name":"retrieve_documents","arguments":"{\"mode\":\"whole_document\",\"query\":\"summarize all\",\"documentIds\":[\"doc\"]}","status":"completed"}""";
                return Sse(Created(0),
                    ("response.output_item.done", $$"""{"type":"response.output_item.done","sequence_number":1,"output_index":0,"item":{{call}}}"""),
                    Completed(2, $"[{call}]"));
            }
            var output = json.RootElement.GetProperty("input").EnumerateArray().Last(item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            using var result = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            Assert.Equal(100, result.RootElement.GetProperty("items").GetArrayLength());
            const string message = """{"type":"message","id":"msg1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Last section [[cite:100]].","annotations":[]}]}""";
            return Sse(Created(0),
                ("response.output_item.added", """{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"type":"message","id":"msg1","role":"assistant","status":"in_progress","content":[]}}"""),
                Delta(2, "Last section [[cite:"),
                Delta(3, "100]]."),
                ("response.output_item.done", $$"""{"type":"response.output_item.done","sequence_number":4,"output_index":0,"item":{{message}}}"""),
                Completed(5, $"[{message}]"));
        }));
        var passages = Enumerable.Range(0, 100)
            .Select(i => new Passage($"doc-{i}", "doc", "report.txt", $"Section {i}", $"Page {i + 1}")).ToArray();
        var progress = new RecordingProgress();
        var result = await AzureContractTests.Coordinator(http).Run(
            new DefaultHttpContext(), "Summarize the whole document", [], [], [], default,
            availableDocumentNames: ["report.txt (id: doc, chunks: 100)"],
            searchDocumentsAsync: (_, _) => Task.FromResult(passages.Take(8).ToArray()),
            progress: progress,
            retrieveDocumentsAsync: (_, _) => Task.FromResult(new DocumentRetrievalResult("whole_document",
                passages, [new("doc", "report.txt", 100, 100)],
                passages.Select(p => new DocumentFinding(p.Content, [p.Id])).ToArray(), false)));
        Assert.Equal(2, calls);
        Assert.Equal(100, result.Evidence.Length);
        Assert.Equal("Last section [100].", progress.Text.ToString());
        Assert.Equal(result.Answer, progress.Text.ToString());
        Assert.Contains(progress.Statuses, status => status.Tool == DocumentRetrieval.ToolName && status.State == ChatStatus.Completed);
    }

    [Fact]
    public async Task RealResponsesStreamRejectsIncompleteFinalAnswer()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Sse(Created(0),
            ("response.incomplete", """{"type":"response.incomplete","sequence_number":1,"response":{"id":"resp1","object":"response","created_at":1,"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"model":"gpt-6.1-sol","output":[],"parallel_tool_calls":false,"tool_choice":"auto","tools":[]}}""")))));
        var error = await Assert.ThrowsAsync<ApiException>(() => AzureContractTests.Coordinator(http).Run(
            new DefaultHttpContext(), "Summarize all", [], [], [], default, progress: new RecordingProgress()));
        Assert.Equal("incomplete_response", error.Code);
    }

    [Fact]
    public async Task MockStreamEndpointSendsAgUiStepsTextAndValidatedResult()
    {
        using var api = CreateMockApi();
        using var client = api.CreateClient();
        var conversationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = JsonContent.Create(AgUi.Run(conversationId, "When is the specialist project review?", ["a2a"]))
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var events = AgUi.ParseEvents(await response.Content.ReadAsStringAsync());
        Assert.Equal("RUN_STARTED", events[0].Type());
        Assert.Equal(conversationId, events[0].GetProperty("threadId").GetString());

        var statuses = events.Select(item => item.Status()).OfType<JsonElement>().ToArray();
        Assert.Equal("Checking your profile and time zone", statuses[0].GetProperty("message").GetString());
        Assert.False(statuses[0].TryGetProperty("tool", out _));
        Assert.Contains(statuses, status =>
            status.TryGetProperty("tool", out var tool) && tool.GetString() == A2aKnowledgeService.AskToolName &&
            status.GetProperty("state").GetString() == ChatStatus.Completed);
        Assert.Contains(statuses, status =>
            status.GetProperty("state").GetString() == ChatStatus.Progress &&
            status.GetProperty("message").GetString() == "Searching through knowledge sources (mock)");

        var deltas = events.Where(item => item.Type() == "TEXT_MESSAGE_CONTENT").ToArray();
        Assert.True(deltas.Length > 1, "The mock answer should stream in chunks.");
        var done = events[^1];
        Assert.Equal("RUN_FINISHED", done.Type());
        var result = done.GetProperty("result");
        var answer = result.GetProperty("answer").GetString();
        Assert.Contains("Friday", answer);
        Assert.Equal(answer, string.Concat(deltas.Select(item => item.GetProperty("delta").GetString())));
        Assert.Equal(2, result.GetProperty("citations").GetProperty("sources").GetArrayLength());
        Assert.DoesNotContain(events, item => item.Type() == "RUN_ERROR");
        AssertValidAgUiSequence(events);
    }

    [Fact]
    public async Task AgUiStreamPairsUnmatchedAndParallelStepsAndClosesThemBeforeFinishing()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var stream = new AgUiEventStream(context, "thread", "run",
            new JsonSerializerOptions(JsonSerializerOptions.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        await stream.ReportAsync(new ChatStatus("Working out what you need"), default);
        await stream.ReportAsync(new ChatStatus("Asking A", ChatStatus.Started, "ask"), default);
        await stream.ReportAsync(new ChatStatus("Asking B", ChatStatus.Started, "ask"), default);
        await stream.WriteTextAsync("Hello", default);
        await stream.ReportAsync(new ChatStatus("A answered", ChatStatus.Completed, "ask"), default);
        await stream.ReportAsync(new ChatStatus("Search failed", ChatStatus.Failed, "search"), default);
        await stream.FinishAsync(new Contracts.Responses.ChatResponse("Hello", [],
            new Contracts.Citations.CitationPayload("v1", [], [])), default);

        var events = AgUi.ParseEvents(Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.Equal(["ask", "ask #2"], events
            .Where(item => item.Type() == "STEP_STARTED" && item.Status() is { } status &&
                status.TryGetProperty("tool", out _))
            .Select(item => item.GetProperty("stepName").GetString()));
        Assert.Equal("ask", events.Single(item => item.Status()?.GetProperty("message").GetString() == "A answered")
            .GetProperty("stepName").GetString());
        Assert.Equal("RUN_FINISHED", events[^1].Type());
        AssertValidAgUiSequence(events);
    }

    [Fact]
    public async Task StreamEndpointReturnsProblemJsonForInvalidRequestsBeforeStreaming()
    {
        using var api = CreateMockApi();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/chat/stream",
            AgUi.Run(Guid.NewGuid().ToString(), " ", []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_question", problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("""{"threadId":"t","messages":[]}""", "invalid_question")]
    [InlineData("""{"threadId":"t","messages":[{"id":"1","role":"assistant","content":"Hi"}]}""", "invalid_question")]
    [InlineData("""{"threadId":"t","messages":[{"id":"1","role":"user","content":[{"type":"binary","mimeType":"image/png","data":"AA=="}]}]}""", "invalid_request")]
    [InlineData("""{"threadId":"t","messages":[{"id":"1","role":"user","content":[{"type":"image","source":{"type":"url","value":"https://example.test/a.png"}}]}]}""", "unsupported_content")]
    [InlineData("""{"threadId":"t","messages":[{"id":"1","role":"user","content":"Hi"}],"forwardedProps":[1]}""", "invalid_request")]
    [InlineData("""{"threadId":"t","messages":[{"id":"1","role":"user","content":"Hi"}],"forwardedProps":{"sources":"a2a"}}""", "invalid_request")]
    public async Task StreamEndpointRejectsRunInputItCannotMapToAQuestion(string body, string code)
    {
        using var api = CreateMockApi();
        using var client = api.CreateClient();

        using var response = await client.PostAsync("/api/chat/stream",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task StreamEndpointAcceptsTextPartsAsTheQuestion()
    {
        using var api = CreateMockApi();
        using var client = api.CreateClient();
        const string body = """{"threadId":"8a4c1c1e-4c2f-4f7e-9d0a-1d6f1b2a3c4d","messages":[{"id":"1","role":"user","content":[{"type":"text","text":"When is the specialist "},{"type":"text","text":"project review?"}]}],"forwardedProps":{"sources":["a2a"]}}""";

        using var response = await client.PostAsync("/api/chat/stream",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();
        var events = AgUi.ParseEvents(await response.Content.ReadAsStringAsync());
        Assert.Contains("Friday", events[^1].GetProperty("result").GetProperty("answer").GetString());
    }

    /// <summary>Applies the ordering rules AG-UI clients verify, so the stream never trips them.</summary>
    private static void AssertValidAgUiSequence(List<JsonElement> events)
    {
        Assert.Equal("RUN_STARTED", events[0].Type());
        var steps = new HashSet<string>();
        string? message = null;
        foreach (var item in events.Skip(1))
        {
            switch (item.Type())
            {
                case "STEP_STARTED":
                    Assert.True(steps.Add(item.GetProperty("stepName").GetString()!), "Duplicate active step.");
                    break;
                case "STEP_FINISHED":
                    Assert.True(steps.Remove(item.GetProperty("stepName").GetString()!), "Finished a step that was not started.");
                    break;
                case "TEXT_MESSAGE_START":
                    Assert.Null(message);
                    message = item.GetProperty("messageId").GetString();
                    break;
                case "TEXT_MESSAGE_CONTENT":
                    Assert.Equal(message, item.GetProperty("messageId").GetString());
                    break;
                case "TEXT_MESSAGE_END":
                    Assert.Equal(message, item.GetProperty("messageId").GetString());
                    message = null;
                    break;
                case "RUN_FINISHED":
                    Assert.Empty(steps);
                    Assert.Null(message);
                    break;
            }
        }
    }

    private WebApplicationFactory<Program> CreateMockApi() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
        });

    private static (string, string) Created(int sequence) => ("response.created",
        $$$"""{"type":"response.created","sequence_number":{{{sequence}}},"response":{"id":"resp1","object":"response","created_at":1,"status":"in_progress","model":"gpt-6.1-sol","output":[],"parallel_tool_calls":false,"tool_choice":"auto","tools":[]}}""");

    private static (string, string) Completed(int sequence, string output) => ("response.completed",
        $$$"""{"type":"response.completed","sequence_number":{{{sequence}}},"response":{"id":"resp1","object":"response","created_at":1,"status":"completed","model":"gpt-6.1-sol","output":{{{output}}},"parallel_tool_calls":false,"tool_choice":"auto","tools":[]}}""");

    private static (string, string) Delta(int sequence, string text) => ("response.output_text.delta",
        $$"""{"type":"response.output_text.delta","sequence_number":{{sequence}},"item_id":"msg1","output_index":0,"content_index":0,"delta":{{JsonSerializer.Serialize(text)}}}""");

    private static HttpResponseMessage Sse(params (string Name, string Data)[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            string.Concat(events.Select(item => $"event: {item.Name}\ndata: {item.Data}\n\n")),
            Encoding.UTF8,
            "text/event-stream")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class RecordingProgress : ChatProgress
    {
        public List<ChatStatus> Statuses { get; } = [];
        public StringBuilder Text { get; } = new();
        public override bool StreamsText => true;

        public override ValueTask ReportAsync(ChatStatus status, CancellationToken cancellationToken)
        {
            Statuses.Add(status);
            return ValueTask.CompletedTask;
        }

        public override ValueTask WriteTextAsync(string text, CancellationToken cancellationToken)
        {
            Text.Append(text);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StreamingClient(ChatResponseUpdate[] updates) : IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
