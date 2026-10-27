using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class ConversationHistoryTests
{
    [Fact]
    public async Task FrameworkReplaysClarificationAndRestoresHistoryAfterStoreRestart()
    {
        using var fixture = new Fixture();
        var call = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var input = json.RootElement.GetProperty("input").GetRawText();
            if (call++ == 0) return Reply("Which date and timezone should I use?");
            Assert.Contains("Find my appointments", input);
            Assert.Contains("Which date and timezone should I use?", input);
            Assert.Contains("Tomorrow, UTC", input);
            Assert.Single(Regex.Matches(input, "Context availability:"));
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            Assert.False(json.RootElement.TryGetProperty("previous_response_id", out _));
            Assert.DoesNotContain("graph_list_calendar", json.RootElement.GetRawText());
            return Reply("Enable Calendar to check tomorrow in UTC.");
        }));
        var coordinator = AzureContractTests.Coordinator(http);
        await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
            coordinator.Run(new DefaultHttpContext(), "Find my appointments", [], ["calendar"], [], ct, sessionState: state), default);

        using var restarted = fixture.CreateStore();
        var answer = await restarted.RunAsync(fixture.Scope, (state, ct) =>
            coordinator.Run(new DefaultHttpContext(), "Tomorrow, UTC", [], [], [], ct, sessionState: state), default);
        Assert.Contains("tomorrow in UTC", answer.Answer);
        Assert.Equal(2, call);
    }

    [Fact]
    public async Task PrivateEvidenceToolResultsAndDocumentNamesNeverReplayAfterSourceRemoval()
    {
        using var fixture = new Fixture();
        var call = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (call++ == 0)
            {
                return Json("""{"status":"completed","output":[{"type":"function_call","call_id":"c1","name":"search_documents","arguments":"{\"query\":\"project\"}"}]}""");
            }
            if (call == 2) return Reply("The private-excerpt is Lavender-27 [[cite:1]].");
            var body = json.RootElement.GetRawText();
            Assert.DoesNotContain("Lavender-27", body);
            Assert.DoesNotContain("private-excerpt", body);
            Assert.DoesNotContain("secret-title.txt", body);
            Assert.DoesNotContain("search_documents", json.RootElement.GetProperty("input").GetRawText());
            Assert.DoesNotContain("function_call", body);
            Assert.Contains(ConversationHistory.PrivateReply, json.RootElement.GetProperty("input").GetRawText());
            return Reply("Please upload an authorized document again.");
        }));
        var coordinator = AzureContractTests.Coordinator(http);
        var first = await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
            coordinator.Run(new DefaultHttpContext(), "Summarize my upload", [], [], [], ct,
                availableDocumentNames: ["secret-title.txt"],
                searchDocumentsAsync: (_, _) => Task.FromResult(new[] {
                    new Passage("1", "doc", "secret-title.txt", "private-excerpt Lavender-27", "Page 1") }),
                sessionState: state), default);
        Assert.Contains("Lavender-27", first.Answer);
        var saved = await File.ReadAllTextAsync(fixture.Path);
        Assert.DoesNotContain("Lavender-27", saved);
        Assert.DoesNotContain("secret-title.txt", saved);
        Assert.DoesNotContain("function_call", saved);
        var second = await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
            coordinator.Run(new DefaultHttpContext(), "What about that project?", [], [], [], ct,
                availableDocumentNames: [], sessionState: state), default);
        Assert.Empty(second.Evidence);
        Assert.Equal(3, call);
    }

    [Fact]
    public async Task HistoryIsIsolatedByTenantUserAndConversation()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply("Hello"))));
        var coordinator = AzureContractTests.Coordinator(http);
        await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
            coordinator.RunMock(new DefaultHttpContext(), "private-user-prompt", [], [], [], ct, state), default);
        var scopes = new[]
        {
            new Caller("other-tenant", "user").Scope(fixture.Conversation),
            new Caller("tenant", "other-user").Scope(fixture.Conversation),
            new Caller("tenant", "user").Scope(Guid.NewGuid().ToString())
        };
        foreach (var scope in scopes)
        {
            await fixture.Store.RunAsync(scope, (state, ct) =>
            {
                Assert.Null(state);
                return coordinator.RunMock(new DefaultHttpContext(), "new conversation", [], [], [], ct, state);
            }, default);
        }
    }

    [Fact]
    public async Task HistoryBoundsWholeDialogueTurnsAndTotalCharacters()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply(new string('a', 2000)))));
        var coordinator = AzureContractTests.Coordinator(http);
        GroundedAnswer? latest = null;
        for (var index = 0; index < 16; index++)
        {
            var question = $"history-question-{index:D2} " + new string('q', 2000);
            latest = await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
                coordinator.Run(new DefaultHttpContext(), question, [], [], [], ct, sessionState: state), default);
        }
        var serialized = latest!.SessionState!.Value.GetRawText();
        Assert.DoesNotContain("history-question-00", serialized);
        Assert.Contains("history-question-15", serialized);
        Assert.InRange(Regex.Matches(serialized, "history-question-").Count, 1, ConversationHistory.MaxTurns);
        Assert.True(serialized.Length < ConversationHistory.MaxCharacters + 3000);
    }

    [Fact]
    public async Task ConcurrentTurnsSerializeAndObserveTheLastCommittedTurn()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply("Hello"))));
        var coordinator = AzureContractTests.Coordinator(http);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Store.RunAsync(fixture.Scope, async (state, ct) =>
        {
            entered.SetResult();
            await release.Task;
            return await coordinator.RunMock(new DefaultHttpContext(), "first-turn", [], [], [], ct, state);
        }, default);
        await entered.Task;
        var secondEntered = false;
        var second = fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
        {
            secondEntered = true;
            Assert.Contains("first-turn", state!.Value.GetRawText());
            return coordinator.RunMock(new DefaultHttpContext(), "second-turn", [], [], [], ct, state);
        }, default);
        Assert.False(secondEntered);
        release.SetResult();
        await Task.WhenAll(first, second);
        var saved = await File.ReadAllTextAsync(fixture.Path);
        Assert.Contains("first-turn", saved);
        Assert.Contains("second-turn", saved);
    }

    [Fact]
    public async Task FailedAndCanceledTurnsLeaveCommittedHistoryUnchanged()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply("Hello"))));
        var coordinator = AzureContractTests.Coordinator(http);
        var committed = await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
            coordinator.RunMock(new DefaultHttpContext(), "committed-turn", [], [], [], ct, state), default);
        var original = await File.ReadAllTextAsync(fixture.Path);
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Store.RunAsync(fixture.Scope,
            (_, _) => throw new HttpRequestException("Failed model request"), default));
        using var invalidModel = new HttpClient(new Handler(_ => Task.FromResult(Reply("Invented [[cite:999]]."))));
        var invalidCoordinator = AzureContractTests.Coordinator(invalidModel);
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.Store.RunAsync(fixture.Scope,
            (state, ct) => invalidCoordinator.Run(new DefaultHttpContext(), "failed-turn", [], [], [], ct, sessionState: state), default));
        Assert.Equal("invalid_citation", error.Code);
        using var canceled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.RunAsync(fixture.Scope, (_, _) =>
        {
            canceled.Cancel();
            return Task.FromResult(committed);
        }, canceled.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!));
    }

    [Fact]
    public async Task ExpiredHistoryIsNotReplayedAndCleanupRemovesIt()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply("Hello"))));
        var coordinator = AzureContractTests.Coordinator(http);
        var first = await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
            coordinator.RunMock(new DefaultHttpContext(), "expired-turn", [], [], [], ct, state), default);
        var expired = JsonSerializer.Serialize(new
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            Session = first.SessionState
        });
        await File.WriteAllTextAsync(fixture.Path, expired);
        await fixture.Store.RunAsync(fixture.Scope, (state, ct) =>
        {
            Assert.Null(state);
            return coordinator.RunMock(new DefaultHttpContext(), "fresh-turn", [], [], [], ct, state);
        }, default);
        Assert.DoesNotContain("expired-turn", await File.ReadAllTextAsync(fixture.Path));
        await File.WriteAllTextAsync(fixture.Path, expired);
        await fixture.Store.SweepExpiredAsync(default);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task CorruptHistoryFailsExplicitlyInsteadOfSilentlyResetting()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        await File.WriteAllTextAsync(fixture.Path, "{broken");
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.Store.RunAsync(fixture.Scope,
            (_, _) => throw new InvalidOperationException("The model must not run"), default));
        Assert.Equal("history_unavailable", error.Code);
        Assert.Equal("{broken", await File.ReadAllTextAsync(fixture.Path));
    }

    private static HttpResponseMessage Reply(string text) => Json(JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new[] { new { type = "message", role = "assistant", status = "completed", id = "msg1",
            content = new[] { new { type = "output_text", text } } } }
    }));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "history-tests-" + Guid.NewGuid().ToString("N"));
        public string Conversation { get; } = Guid.NewGuid().ToString();
        public string Scope => new Caller("tenant", "user").Scope(Conversation);
        public string Path => System.IO.Path.Combine(directory, Scope, "conversation.agent.json");
        public ConversationHistoryStore Store { get; }
        public Fixture() => Store = CreateStore();
        public ConversationHistoryStore CreateStore() => new(
            TestConfiguration.Create(directory: directory),
            new AzureContractTests.FakeEnvironment(), NullLogger<ConversationHistoryStore>.Instance);
        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
