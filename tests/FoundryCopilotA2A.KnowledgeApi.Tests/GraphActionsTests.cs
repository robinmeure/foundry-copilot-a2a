using Microsoft.Extensions.Configuration;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ApiException = FoundryCopilotA2A.KnowledgeApi.Middleware.ApiException;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class GraphActionsTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Api : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "action-tests-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static readonly IConfiguration Live = TestConfiguration.Create("Azure");
    private static readonly IConfiguration Mock = TestConfiguration.Create();

    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static GraphActions Actions(HttpMessageHandler handler, IConfiguration configuration) =>
        new(new GraphService(new Factory(handler), configuration, (_, _) => Task.FromResult("obo-token")), configuration);

    private static GraphActions MockActions() =>
        Actions(new Handler(_ => throw new InvalidOperationException("Mock proposals must not call Graph.")), Mock);

    private static DefaultHttpContext Context() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "tenant"), new Claim("oid", "caller")], "test"))
    };

    private static GraphActionProposal Proposal(string tool, object arguments) =>
        new(tool, "test", "Title", "Summary", [], JsonSerializer.SerializeToElement(arguments));

    private static Task<GraphActionProposal> Propose(GraphActions actions, string tool, string arguments, params string[] sources)
    {
        using var json = JsonDocument.Parse(arguments);
        return actions.ProposeAsync(Context(), tool, json.RootElement.Clone(), sources, default);
    }

    [Fact]
    public void CatalogIsStrictAndGatedBySelectedSources()
    {
        Assert.Empty(GraphActions.Enabled([]));
        Assert.Equal(3, GraphActions.Enabled(["mail"]).Length);
        Assert.Single(GraphActions.Enabled(["planner"]));
        Assert.Empty(GraphActions.Enabled(["teams", "people"]));
        Assert.All(GraphActions.Catalog, tool =>
        {
            Assert.StartsWith("graph_propose_", tool.Name);
            Assert.True(GraphActions.IsActionTool(tool.Name));
            Assert.DoesNotContain(GraphSkill.Catalog, read => read.Name == tool.Name);
            var definition = JsonSerializer.SerializeToElement(tool.Definition);
            Assert.True(definition.GetProperty("strict").GetBoolean());
            Assert.False(tool.Parameters.GetProperty("additionalProperties").GetBoolean());
        });
    }

    [Fact]
    public void StoreIssuesOpaqueSingleUseActionsBoundToScope()
    {
        var store = new GraphActionStore(new ManualClock());
        var view = Assert.Single(store.Add("tenant:alice:c1", [Proposal("graph_propose_todo_task", new { })]));
        Assert.Matches("^[0-9a-f]{32}$", view.Id);

        Assert.Null(store.Take(view.Id, "tenant:bob:c1"));
        Assert.Null(store.Take(view.Id, "tenant:alice:c2"));
        Assert.NotNull(store.Take(view.Id, "tenant:alice:c1"));
        Assert.Null(store.Take(view.Id, "tenant:alice:c1"));
        Assert.Null(store.Take("../other", "tenant:alice:c1"));
    }

    [Fact]
    public void StoreExpiresActionsAndCapsPendingPerScope()
    {
        var clock = new ManualClock();
        var store = new GraphActionStore(clock);
        var expiring = Assert.Single(store.Add("scope", [Proposal("graph_propose_todo_task", new { })]));
        Assert.Equal(clock.Now + GraphActionStore.Lifetime, expiring.ExpiresAt);
        clock.Now += GraphActionStore.Lifetime;
        Assert.Null(store.Take(expiring.Id, "scope"));

        var views = Enumerable.Range(0, 11)
            .SelectMany(_ => store.Add("scope", [Proposal("graph_propose_todo_task", new { })]))
            .ToArray();
        Assert.Null(store.Take(views[0].Id, "scope"));
        Assert.All(views.Skip(1), view => Assert.NotNull(store.Take(view.Id, "scope")));
    }

    [Theory]
    [InlineData("graph_propose_mail_draft", """{"to":["Alex <alex@example.test>"],"cc":[],"subject":"Hi","body":"Body"}""")]
    [InlineData("graph_propose_mail_draft", """{"to":["alex@example.test","ALEX@example.test"],"cc":[],"subject":"Hi","body":"Body"}""")]
    [InlineData("graph_propose_mail_draft", """{"to":[],"cc":[],"subject":"Hi","body":"Body"}""")]
    [InlineData("graph_propose_mail_draft", """{"to":["alex@example.test"],"cc":[],"subject":"Hi\nBcc: evil@example.test","body":"Body"}""")]
    [InlineData("graph_propose_mail_draft", """{"to":["alex@example.test"],"cc":[],"subject":"Hi","body":"Body","bcc":["evil@example.test"]}""")]
    [InlineData("graph_propose_calendar_event", """{"subject":"Sync","start":"2026-10-27T10:00:00","end":"2026-10-27T11:00:00Z","attendees":[],"location":"","body":""}""")]
    [InlineData("graph_propose_calendar_event", """{"subject":"Sync","start":"2026-10-27T11:00:00Z","end":"2026-10-27T10:00:00Z","attendees":[],"location":"","body":""}""")]
    [InlineData("graph_propose_calendar_event", """{"subject":"Sync","start":"2026-10-01T10:00:00Z","end":"2026-10-27T10:00:00Z","attendees":[],"location":"","body":""}""")]
    [InlineData("graph_propose_todo_task", """{"listId":"list","title":"Task","dueDate":"27/10/2026","body":""}""")]
    [InlineData("graph_propose_todo_task", """{"listId":"../users/other","title":"Task","dueDate":"","body":""}""")]
    [InlineData("graph_propose_todo_task_status", """{"listId":"list","taskId":"task","status":"deleted"}""")]
    [InlineData("graph_propose_planner_task_progress", """{"taskId":"task","percentComplete":75}""")]
    [InlineData("graph_propose_mail_read_state", """{"messageId":"m1","isRead":"true"}""")]
    public async Task UnsafeOrInvalidProposalsAreRejected(string tool, string arguments)
    {
        var source = GraphActions.Catalog.Single(candidate => candidate.Name == tool).Source;
        var failure = await Assert.ThrowsAsync<ApiException>(() => Propose(MockActions(), tool, arguments, source));
        Assert.Equal(StatusCodes.Status400BadRequest, failure.Status);
    }

    [Fact]
    public async Task ProposalsRequireTheSelectedSource()
    {
        var failure = await Assert.ThrowsAsync<ApiException>(() => Propose(MockActions(),
            "graph_propose_todo_task", """{"listId":"list","title":"Task","dueDate":"","body":""}""", "mail"));
        Assert.Equal(StatusCodes.Status403Forbidden, failure.Status);
        Assert.Equal("graph_tool_disabled", failure.Code);
    }

    [Fact]
    public async Task CalendarProposalNormalizesToUtcAndDescribesInvitations()
    {
        var proposal = await Propose(MockActions(), "graph_propose_calendar_event",
            """{"subject":" Launch sync ","start":"2026-10-27T10:00:00+01:00","end":"2026-10-27T11:00:00+01:00","attendees":["alex@example.test"],"location":"","body":""}""",
            "calendar");
        Assert.Equal("calendar.create", proposal.Kind);
        Assert.Equal("Launch sync", proposal.Arguments.GetProperty("subject").GetString());
        Assert.Equal("2026-10-27T09:00:00", proposal.Arguments.GetProperty("start").GetString());
        Assert.Contains("send invitations to 1 attendee", proposal.Summary);
        Assert.DoesNotContain(proposal.Details, detail => detail.Label == "Location");
    }

    [Fact]
    public async Task LiveReplyProposalReadsTrustedMessageDetailsWithoutWriting()
    {
        var requests = new List<HttpRequestMessage>();
        var actions = Actions(new Handler(request =>
        {
            requests.Add(request);
            return Task.FromResult(Json("""{"id":"m1","subject":"Budget","from":{"emailAddress":{"name":"Morgan","address":"morgan@example.test"}}}"""));
        }), Live);

        var proposal = await Propose(actions, "graph_propose_mail_reply_draft", """{"messageId":"m1","comment":"Thanks!"}""", "mail");

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1.0/me/messages/m1", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Contains("Morgan <morgan@example.test>", proposal.Summary);
        Assert.Contains("\"Budget\"", proposal.Summary);
    }

    [Fact]
    public async Task MailDraftIsSavedButNeverSent()
    {
        var requests = new List<(HttpMethod Method, string Path, JsonElement Body)>();
        var actions = Actions(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, body.RootElement.Clone()));
            return Json("""{"id":"draft","webLink":"https://outlook.office.com/mail/drafts/draft"}""", HttpStatusCode.Created);
        }), Live);
        var proposal = await Propose(actions, "graph_propose_mail_draft",
            """{"to":["alex@example.test"],"cc":["sam@example.test"],"subject":"Launch","body":"Line 1\nLine 2"}""", "mail");

        var result = await actions.ExecuteAsync(Context(), "id", proposal, default);

        var (method, path, sent) = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/v1.0/me/messages", path);
        Assert.Equal("Launch", sent.GetProperty("subject").GetString());
        Assert.Equal("text", sent.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("alex@example.test", sent.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("sam@example.test", sent.GetProperty("ccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("completed", result.Status);
        Assert.Contains("not been sent", result.Message);
        Assert.Equal("https://outlook.office.com/mail/drafts/draft", result.WebLink);
    }

    [Fact]
    public async Task CalendarAndTodoWritesUseExpectedGraphShapes()
    {
        var requests = new List<(HttpMethod Method, string Path, JsonElement Body)>();
        var actions = Actions(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, body.RootElement.Clone()));
            return Json("""{"id":"created","webLink":"https://evil.test/?token=secret"}""", HttpStatusCode.Created);
        }), Live);

        var calendar = await actions.ExecuteAsync(Context(), "a", Proposal("graph_propose_calendar_event", new
        {
            subject = "Sync",
            start = "2026-10-27T09:00:00",
            end = "2026-10-27T10:00:00",
            attendees = new[] { "alex@example.test" },
            location = "Room 1",
            body = ""
        }), default);
        await actions.ExecuteAsync(Context(), "b", Proposal("graph_propose_todo_task", new
        {
            listId = "AQ==",
            title = "Prepare notes",
            dueDate = "2026-10-30",
            body = ""
        }), default);
        await actions.ExecuteAsync(Context(), "c", Proposal("graph_propose_todo_task_status", new
        {
            listId = "AQ==",
            taskId = "task",
            status = "completed"
        }), default);

        Assert.Null(calendar.WebLink);
        Assert.Equal((HttpMethod.Post, "/v1.0/me/events"), (requests[0].Method, requests[0].Path));
        Assert.Equal("UTC", requests[0].Body.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal("required", requests[0].Body.GetProperty("attendees")[0].GetProperty("type").GetString());
        Assert.Equal("Room 1", requests[0].Body.GetProperty("location").GetProperty("displayName").GetString());
        Assert.False(requests[0].Body.TryGetProperty("body", out _));
        Assert.Equal((HttpMethod.Post, "/v1.0/me/todo/lists/AQ==/tasks"), (requests[1].Method, Uri.UnescapeDataString(requests[1].Path)));
        Assert.Equal("2026-10-30T00:00:00", requests[1].Body.GetProperty("dueDateTime").GetProperty("dateTime").GetString());
        Assert.False(requests[1].Body.TryGetProperty("body", out _));
        Assert.Equal((HttpMethod.Patch, "/v1.0/me/todo/lists/AQ==/tasks/task"), (requests[2].Method, Uri.UnescapeDataString(requests[2].Path)));
        Assert.Equal("completed", requests[2].Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PlannerProgressUsesTheCurrentETag()
    {
        var requests = new List<HttpRequestMessage>();
        var actions = Actions(new Handler(async request =>
        {
            requests.Add(request);
            if (request.Method == HttpMethod.Get)
            {
                return Json("""{"id":"task","title":"Review","@odata.etag":"W/\"JzEtVGFzayc=\""}""");
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(100, body.RootElement.GetProperty("percentComplete").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }), Live);

        var result = await actions.ExecuteAsync(Context(), "id", Proposal("graph_propose_planner_task_progress", new
        {
            taskId = "task",
            percentComplete = 100
        }), default);

        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Patch, requests[1].Method);
        Assert.Equal("/v1.0/planner/tasks/task", requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("W/\"JzEtVGFzayc=\"", requests[1].Headers.GetValues("If-Match").Single());
        Assert.Contains("completed", result.Message);
    }

    [Fact]
    public async Task PlannerProgressFailsClosedWithoutETag()
    {
        var actions = Actions(new Handler(_ => Task.FromResult(Json("""{"id":"task","title":"Review"}"""))), Live);
        var failure = await Assert.ThrowsAsync<ApiException>(() => actions.ExecuteAsync(Context(), "id",
            Proposal("graph_propose_planner_task_progress", new { taskId = "task", percentComplete = 50 }), default));
        Assert.Equal("planner_etag_missing", failure.Code);
    }

    [Fact]
    public async Task GraphPermissionFailuresPropagate()
    {
        var actions = Actions(new Handler(_ => Task.FromResult(Json("""{"error":{"code":"ErrorAccessDenied","message":"sensitive"}}""", HttpStatusCode.Forbidden))), Live);
        var failure = await Assert.ThrowsAsync<ApiException>(() => actions.ExecuteAsync(Context(), "id",
            Proposal("graph_propose_mail_read_state", new { messageId = "m1", isRead = true }), default));
        Assert.Equal(StatusCodes.Status403Forbidden, failure.Status);
        Assert.Equal("graph_error", failure.Code);
        Assert.DoesNotContain("sensitive", failure.Message);
    }

    [Fact]
    public async Task ChatStagesActionThatCanBeConfirmedOnceInTheSameConversation()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();

        var chat = await client.PostAsJsonAsync("/api/chat", new
        {
            conversationId = conversation,
            text = "Create a task to prepare launch notes",
            sources = new[] { "todo" }
        });
        chat.EnsureSuccessStatusCode();
        var body = await chat.Content.ReadFromJsonAsync<JsonElement>();
        var action = Assert.Single(body.GetProperty("actions").EnumerateArray());
        Assert.Equal("todo.create", action.GetProperty("kind").GetString());
        Assert.Contains("Launch tasks", action.GetProperty("summary").GetString());
        var id = action.GetProperty("id").GetString();

        var otherConversation = await client.PostAsJsonAsync($"/api/actions/{id}/confirm", new { conversationId = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.NotFound, otherConversation.StatusCode);

        var confirmed = await client.PostAsJsonAsync($"/api/actions/{id}/confirm", new { conversationId = conversation });
        confirmed.EnsureSuccessStatusCode();
        var result = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, result.GetProperty("id").GetString());
        Assert.Equal("completed", result.GetProperty("status").GetString());

        var replay = await client.PostAsJsonAsync($"/api/actions/{id}/confirm", new { conversationId = conversation });
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal("action_not_found", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task CancelledActionsCannotBeConfirmedAndReadOnlyChatsStageNothing()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();

        var readOnly = await (await client.PostAsJsonAsync("/api/chat", new { conversationId = conversation, text = "tasks", sources = new[] { "todo" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(readOnly.GetProperty("actions").EnumerateArray());

        var staged = await (await client.PostAsJsonAsync("/api/chat", new { conversationId = conversation, text = "add a task for review", sources = new[] { "todo" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = staged.GetProperty("actions")[0].GetProperty("id").GetString();

        var cancelled = await client.PostAsJsonAsync($"/api/actions/{id}/cancel", new { conversationId = conversation });
        cancelled.EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/actions/{id}/confirm", new { conversationId = conversation })).StatusCode);
    }
}
