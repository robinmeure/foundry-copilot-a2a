using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

public sealed class MockCopilotStudioInvoker(CopilotConversationHandles conversationHandles) : ICopilotStudioInvoker
{
    public const string FailureReason =
        "The mock Copilot Studio service is unavailable. Try the request again.";

    /// <summary>Prompt that makes the mock reproduce a streamed Copilot Studio answer.</summary>
    public const string StreamProgressPrompt = "stream progress response";
    public const string CitationPrompt = "show citation example";
    public const string CardPrompt = "show card example";
    public const string PlanPrompt = "show plan example";
    /// <summary>Prompt that streams raw citation markers, then a canonical final that replaces them.</summary>
    public const string ReplacementPrompt = "show streamed replacement example";

    /// <summary>The answer the mock streams token by token for <see cref="StreamProgressPrompt"/>.</summary>
    public static readonly string[] StreamedAnswerDeltas =
        ["mock-copilot-studio", ": streamed", " answer", "."];

    /// <summary>The canonical answer that replaces the stream for <see cref="ReplacementPrompt"/>.</summary>
    public const string ReplacementAnswer = "This streamed mock answer cites a source [1].";

    private int _invocationCount;

    public int InvocationCount => _invocationCount;

    private static AdaptiveCardBundle MockCard(AgentResponder responder)
    {
        var content = JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard",
            version = "1.5",
            body = new object[]
            {
                new { type = "TextBlock", text = "Mock order status", weight = "Bolder" },
                new { type = "FactSet", facts = new[] { new { title = "Order", value = "A-1001" } } }
            }
        });
        return new AdaptiveCardBundle(
        [
            new AdaptiveCardItem(
                "mock-card-1", 1, content, "Mock order status: Order A-1001", responder)
        ]);
    }

    public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.mock.invoke");
        traceActivity?.SetTag("copilot_studio.backend", "mock");
        traceActivity?.SetTag("a2a.history.turns", metadata.History.Count);
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = Interlocked.Increment(ref _invocationCount);

        if (string.Equals(
            metadata.AgentId,
            AgentCatalog.MockFailureAgentId,
            StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(250, cancellationToken);
            var exception = new InvalidOperationException(FailureReason);
            AdapterTelemetry.RecordFailure(traceActivity, exception);
            AdapterTelemetry.RecordFailureReason(traceActivity, FailureReason);
            throw exception;
        }

        var resumedConversationId = conversationHandles.Resolve(metadata);
        if (resumedConversationId is not null)
        {
            traceActivity?.SetTag("copilot_studio.conversation.source", "client_handle");
        }

        var conversationId = resumedConversationId ?? metadata.ContextId ?? $"mock-{Guid.NewGuid():N}";
        var handle = conversationHandles.Issue(metadata, conversationId);
        var historySuffix = resumedConversationId is not null
            ? " | resumed conversation"
            : metadata.History.Count == 0
                ? string.Empty
                : $" | context: {metadata.History.Count} earlier turns";
        var fileSuffix = metadata.Files.Count == 0 ? string.Empty :
            $" | mock received {metadata.Files[0].Name} ({metadata.Files[0].Bytes.Length} bytes); " +
            "mock mode does not analyze file contents";

        await Task.Yield();
        if (string.Equals(prompt, PlanPrompt, StringComparison.OrdinalIgnoreCase))
        {
            var responder = new AgentResponder("mock", "Mock Copilot Studio");
            var names = new[]
            {
                "DynamicPlanReceived", "DynamicPlanStepTriggered", "DynamicPlanStepBindUpdate",
                "DynamicPlanStepFinished", "DynamicPlanReceived"
            };
            for (var index = 0; index < names.Length; index++)
            {
                if (index == 4)
                {
                    yield return new CopilotInvocationUpdate(
                        "Here is the mock research answer.", conversationId, "mock-plan-answer")
                    { ConversationHandle = handle };
                }
                var plan = AgentPlanUpdates.ReadActivity(new Activity
                {
                    Type = "event",
                    Name = names[index],
                    Id = $"mock-plan-event-{index}",
                    Value = JsonSerializer.SerializeToElement(new
                    {
                        planIdentifier = $"mock-plan-{invocation}",
                        stepId = "search-1",
                        taskDialogId = "Knowledge search",
                        state = index >= 3 ? "completed" : "inProgress",
                        steps = index == 4 ? Array.Empty<string>() : ["Knowledge search"],
                        isFinalPlan = index == 4,
                        arguments = new { search_query = "Example research question", apiKey = "mock-secret" },
                        observation = new { search_result = new { title = "Example record", text = "A synthetic result." } }
                    })
                }, responder, index + 1)!;
                yield return new CopilotInvocationUpdate(
                    AgentPlanUpdates.Summary(plan), conversationId, plan.EventId, IsInformative: true)
                { PlanUpdate = plan };
                await Task.Delay(150, cancellationToken);
            }
            yield break;
        }
        if (string.Equals(prompt, ReplacementPrompt, StringComparison.OrdinalIgnoreCase))
        {
            var responder = new AgentResponder("mock", "Mock Copilot Studio");
            foreach (var delta in new[]
                     { "This streamed mock answer", " cites a source ", "\uE200cite\uE202turn0search0\uE201", "." })
            {
                yield return new CopilotInvocationUpdate(delta, conversationId, "mock-replacement-answer")
                { ConversationHandle = handle };
                await Task.Delay(100, cancellationToken);
            }
            yield return new CopilotInvocationUpdate(
                ReplacementAnswer, conversationId, "mock-replacement-final")
            {
                ReplacesAnswer = true,
                Citations = AgentCitations.FromProviderSource(
                    responder, "mock-replacement-answer", "Example source (mock)",
                    "https://example.org/", "[1]", "A synthetic excerpt from the cited source.")
            };
            yield break;
        }
        if (string.Equals(prompt, CitationPrompt, StringComparison.OrdinalIgnoreCase))
        {
            yield return new CopilotInvocationUpdate(
                "This is a mock answer with a source [1].", conversationId, "mock-citation-answer");
            yield return new CopilotInvocationUpdate(
                string.Empty, conversationId, "mock-citation-sources")
            {
                Citations = AgentCitations.FromProviderSource(
                    new AgentResponder("mock", "Mock Copilot Studio"),
                    "mock-citation-answer",
                    "Example source (mock)",
                    "https://example.org/",
                    "[1]")
            };
            yield break;
        }
        if (string.Equals(prompt, CardPrompt, StringComparison.OrdinalIgnoreCase))
        {
            yield return new CopilotInvocationUpdate(
                "Here is a mock card.", conversationId, "mock-card-answer")
            {
                ConversationHandle = handle
            };
            yield return new CopilotInvocationUpdate(
                string.Empty, conversationId, "mock-card-payload")
            {
                Cards = MockCard(new AgentResponder("mock", "Mock Copilot Studio"))
            };
            yield break;
        }
        if (string.Equals(prompt, StreamProgressPrompt, StringComparison.Ordinal))
        {
            yield return new CopilotInvocationUpdate(
                "Generating plan...",
                conversationId,
                $"mock-progress-{invocation}",
                IsInformative: true);

            // Mirrors a real streamed answer: the caller receives token-sized chunks and no
            // repeated aggregate at the end.
            var index = 0;
            foreach (var delta in StreamedAnswerDeltas)
            {
                yield return new CopilotInvocationUpdate(
                    delta,
                    conversationId,
                    $"mock-delta-{invocation}-{index++}");
            }

            yield break;
        }

        yield return new CopilotInvocationUpdate(
            $"mock-copilot-studio[{invocation}]: {prompt}{historySuffix}{fileSuffix}",
            conversationId,
            $"mock-response-{invocation}")
        {
            ConversationHandle = handle
        };
    }
}

public sealed class FailureMockRoutingInvoker(
    SdkCopilotStudioInvoker sdkInvoker,
    MockCopilotStudioInvoker mockInvoker) : ICopilotStudioInvoker
{
    public IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken) =>
        string.Equals(
            metadata.AgentId,
            AgentCatalog.MockFailureAgentId,
            StringComparison.OrdinalIgnoreCase)
            ? mockInvoker.StreamAsync(prompt, metadata, cancellationToken)
            : sdkInvoker.StreamAsync(prompt, metadata, cancellationToken);
}
