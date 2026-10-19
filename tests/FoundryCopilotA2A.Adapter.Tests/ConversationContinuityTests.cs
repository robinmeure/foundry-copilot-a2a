using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using BotActivity = Microsoft.Agents.Core.Models.Activity;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class ConversationContinuityTests(A2AAdapterFactory factory) : IClassFixture<A2AAdapterFactory>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ReplayKeepsOnlyTheMostRecentTurns()
    {
        var history = Enumerable.Range(1, 12)
            .Select(index => new A2AConversationTurn(
                index % 2 == 1 ? A2AConversationTurn.UserRole : A2AConversationTurn.AssistantRole,
                $"turn {index}"))
            .ToArray();

        var prompt = ConversationTranscript.Prepend("next", history, maxTurns: 4, maxCharacters: 10_000);

        Assert.StartsWith("Conversation so far (last 4 of 12 turns):", prompt);
        Assert.DoesNotContain("turn 8\n", prompt);
        Assert.Contains("User: turn 9", prompt);
        Assert.Contains("Assistant: turn 12", prompt);
        Assert.EndsWith("\n\n---\nnext", prompt);
    }

    [Fact]
    public void ReplayStopsAtTheCharacterBudgetSoContextStaysContiguous()
    {
        A2AConversationTurn[] history =
        [
            new(A2AConversationTurn.UserRole, "short"),
            new(A2AConversationTurn.AssistantRole, new string('x', 50)),
            new(A2AConversationTurn.UserRole, "recent")
        ];

        var prompt = ConversationTranscript.Prepend("next", history, maxTurns: 10, maxCharacters: 20);

        Assert.Contains("(last 1 of 3 turns)", prompt);
        Assert.Contains("User: recent", prompt);
        Assert.DoesNotContain("short", prompt);
    }

    [Fact]
    public void ReplayWithinBudgetKeepsTheOriginalHeader()
    {
        A2AConversationTurn[] history = [new(A2AConversationTurn.UserRole, "hello")];

        Assert.StartsWith(
            "Conversation so far:\nUser: hello",
            ConversationTranscript.Prepend("next", history, 6, 6_000));
        Assert.Equal("next", ConversationTranscript.Prepend("next", history, 0, 6_000));
    }

    [Fact]
    public void HandleResolvesOnlyForTheSameCallerAgentAndContext()
    {
        var handles = CreateHandles();
        var metadata = Metadata("user-a", "agent-a", "ctx-a");
        var handle = handles.Issue(metadata, "conversation-1")!;

        Assert.DoesNotContain("conversation-1", handle);
        Assert.Equal("conversation-1", handles.Resolve(metadata with { ConversationHandles = [handle] }));
        Assert.Null(handles.Resolve(Metadata("user-b", "agent-a", "ctx-a") with { ConversationHandles = [handle] }));
        Assert.Null(handles.Resolve(Metadata("user-a", "agent-b", "ctx-a") with { ConversationHandles = [handle] }));
        Assert.Null(handles.Resolve(Metadata("user-a", "agent-a", "ctx-b") with { ConversationHandles = [handle] }));
    }

    [Fact]
    public void TamperedOrForeignHandlesAreIgnored()
    {
        var handles = CreateHandles();
        var metadata = Metadata("user-a", "agent-a", "ctx-a");
        var valid = handles.Issue(metadata, "conversation-1")!;
        var tampered = valid[..^4] + (valid[^4] == 'A' ? 'B' : 'A') + valid[^3..];
        var foreign = CreateHandles().Issue(metadata, "conversation-2")!;

        Assert.Null(handles.Resolve(metadata with { ConversationHandles = [tampered, "not-a-handle", foreign] }));
        Assert.Equal(
            "conversation-1",
            handles.Resolve(metadata with { ConversationHandles = [tampered, foreign, valid] }));
    }

    [Fact]
    public void ProgressReportsWorkingOnceThenPlanStepsUntilTheAnswerStarts()
    {
        var progress = new CopilotStudioProgress();
        var updates = new List<string?>
        {
            progress.Next(new BotActivity { Type = "typing" }, "c")?.Text,
            progress.Next(new BotActivity { Type = "typing" }, "c")?.Text,
            progress.Next(PlanEvent("DynamicPlanReceived", new
            {
                steps = new[] { "contoso.topic.InventoryApi", "contoso.topic.Unlisted" },
                toolDefinitions = new[]
                {
                    new { schemaName = "contoso.topic.InventoryApi", displayName = "Inventory API" }
                }
            }), "c")?.Text,
            progress.Next(PlanEvent("DynamicPlanStepTriggered", new
            {
                taskDialogId = "contoso.topic.InventoryApi"
            }), "c")?.Text,
            progress.Next(PlanEvent("DynamicPlanStepTriggered", new
            {
                taskDialogId = "contoso.topic.InventoryApi"
            }), "c")?.Text,
            progress.Next(PlanEvent("DynamicPlanStepFinished", new
            {
                taskDialogId = "contoso.topic.InventoryApi",
                error = new { message = "failed" }
            }), "c")?.Text,
            progress.Next(PlanEvent("DynamicPlanStepFinished", new
            {
                taskDialogId = "contoso.topic.InventoryApi"
            }), "c")?.Text
        };

        Assert.Equal(
            [
                CopilotStudioProgress.WorkingText,
                null,
                "Planning: Inventory API, Unlisted",
                "Running Inventory API...",
                null,
                null,
                "Finished Inventory API"
            ],
            updates);

        progress.Observe(new CopilotInvocationUpdate("answer", "c", "r"));
        Assert.Null(progress.Next(PlanEvent("DynamicPlanStepTriggered", new
        {
            taskDialogId = "contoso.topic.Other"
        }), "c"));
    }

    [Fact]
    public void ProgressIsInformativeAndSkipsWorkingAfterOtherProgress()
    {
        var progress = new CopilotStudioProgress();
        progress.Observe(new CopilotInvocationUpdate("Generating plan...", "c", "r", IsInformative: true));

        Assert.Null(progress.Next(new BotActivity { Type = "typing" }, "c"));
        var update = progress.Next(PlanEvent("DynamicPlanReceived", new { steps = Array.Empty<string>() }), "c");
        Assert.NotNull(update);
        Assert.True(update.IsInformative);
        Assert.Equal("Planning...", update.Text);
    }

    [Fact]
    public async Task NonStreamingIdempotencySpanEndsAfterTheLookupBesideTheInvocation()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AdapterTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) spans.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        using var store = new IdempotencyStore(Options.Create(new AdapterOptions()), new StubLifetime());

        using (var parent = AdapterTelemetry.StartActivity("test.request"))
        {
            Assert.NotNull(parent);
            var result = await store.GetOrAddAsync(
                $"key-{Guid.NewGuid():N}", "hash", SlowProduceAsync, CancellationToken.None);
            Assert.Equal("answer", result.Text);
            Assert.Same(parent, Activity.Current);
        }

        Activity[] snapshot;
        lock (spans) snapshot = [.. spans];
        var request = Assert.Single(snapshot, span => span.OperationName == "test.request");
        snapshot = [.. snapshot.Where(span => span.TraceId == request.TraceId)];
        var idempotency = Assert.Single(snapshot, span => span.OperationName == "a2a.idempotency.get_or_add");
        var invoke = Assert.Single(snapshot, span => span.OperationName == "test.slow_invoke");
        Assert.Equal(request.SpanId, idempotency.ParentSpanId);
        Assert.Equal(request.SpanId, invoke.ParentSpanId);
        Assert.True(idempotency.Duration < invoke.Duration);

        static async IAsyncEnumerable<CopilotInvocationUpdate> SlowProduceAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var invoke = AdapterTelemetry.StartActivity("test.slow_invoke");
            await Task.Delay(100, cancellationToken);
            yield return new CopilotInvocationUpdate("answer", "c", "r")
            {
                Responder = new AgentResponder("agent", "Agent")
            };
        }
    }

    [Fact]
    public async Task SharedInvocationRunsBesideTheIdempotencySpanNotInsideIt()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AdapterTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) spans.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        using var store = new IdempotencyStore(Options.Create(new AdapterOptions()), new StubLifetime());

        using (var parent = AdapterTelemetry.StartActivity("test.request"))
        {
            Assert.NotNull(parent);
            await foreach (var _ in store.GetOrAddStreaming(
                $"key-{Guid.NewGuid():N}", "hash", ProduceAsync, CancellationToken.None))
            {
            }

            Assert.Same(parent, Activity.Current);
        }

        Activity[] snapshot;
        lock (spans) snapshot = [.. spans];
        var request = Assert.Single(snapshot, span => span.OperationName == "test.request");
        snapshot = [.. snapshot.Where(span => span.TraceId == request.TraceId)];
        var idempotency = Assert.Single(snapshot, span => span.OperationName == "a2a.idempotency.get_or_add_stream");
        var invoke = Assert.Single(snapshot, span => span.OperationName == "test.invoke");
        Assert.Equal(request.SpanId, idempotency.ParentSpanId);
        Assert.Equal(request.SpanId, invoke.ParentSpanId);

        static async IAsyncEnumerable<CopilotInvocationUpdate> ProduceAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var invoke = AdapterTelemetry.StartActivity("test.invoke");
            await Task.Yield();
            yield return new CopilotInvocationUpdate("answer", "c", "r")
            {
                Responder = new AgentResponder("agent", "Agent")
            };
        }
    }

    [Fact]
    public async Task ReturnedHandleResumesTheConversationWithoutReplayingHistory()
    {
        var client = factory.CreateClient();
        var contextId = $"ctx-{Guid.NewGuid():N}";

        var first = await SendAsync(client, contextId, "first", handles: null);
        var handle = FindString(first, CopilotConversationHandles.PartMetadataKey);
        Assert.False(string.IsNullOrWhiteSpace(handle));

        var resumed = await SendAsync(client, contextId, "second", [handle!]);
        Assert.Contains("resumed conversation", resumed.GetRawText());
        Assert.DoesNotContain("earlier turns", resumed.GetRawText());

        var otherChat = await SendAsync(client, $"ctx-{Guid.NewGuid():N}", "third", [handle!]);
        Assert.DoesNotContain("resumed conversation", otherChat.GetRawText());
        Assert.Contains("context: 2 earlier turns", otherChat.GetRawText());
    }

    private static async Task<JsonElement> SendAsync(
        HttpClient client, string contextId, string text, string[]? handles)
    {
        var metadata = new Dictionary<string, object>
        {
            ["history"] = new[]
            {
                new { role = "user", text = "earlier question" },
                new { role = "assistant", text = "earlier answer" }
            }
        };
        if (handles is not null)
        {
            metadata[CopilotConversationHandles.RequestMetadataKey] = handles;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = Guid.NewGuid().ToString("N"),
                method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER",
                        parts = new[] { new { text } },
                        messageId = $"msg-{Guid.NewGuid():N}",
                        contextId,
                        metadata
                    }
                }
            }, options: WebJson)
        };
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static string? FindString(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }

                    if (FindString(property.Value, propertyName) is { } nested)
                    {
                        return nested;
                    }
                }
                return null;
            case JsonValueKind.Array:
                return element.EnumerateArray()
                    .Select(item => FindString(item, propertyName))
                    .FirstOrDefault(value => value is not null);
            default:
                return null;
        }
    }

    private static CopilotConversationHandles CreateHandles() =>
        new(new EphemeralDataProtectionProvider(), Options.Create(new AdapterOptions()));

    private static A2ARequestMetadata Metadata(string userId, string agentId, string contextId) => new()
    {
        ContextId = contextId,
        MessageId = "message",
        AgentId = agentId,
        UserId = userId,
        PayloadHash = null,
        BearerToken = null
    };

    private static BotActivity PlanEvent(string type, object value) => new()
    {
        Type = "event",
        Name = type,
        ValueType = type,
        Value = JsonSerializer.SerializeToElement(value)
    };

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}
