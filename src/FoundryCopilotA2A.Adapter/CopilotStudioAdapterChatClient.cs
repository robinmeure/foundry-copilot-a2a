using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter;

public sealed class CopilotStudioAdapterChatClient(
    IAgentInvoker invoker,
    A2ARequestMetadataAccessor metadataAccessor,
    IdempotencyStore idempotencyStore,
    IConfiguration configuration,
    ILogger<CopilotStudioAdapterChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = AdapterTelemetry.StartActivity("a2a.adapter.get_response");
        var (prompt, metadata, cacheKey, payloadHash) = PrepareRequest(messages);
        activity?.SetTag("copilot_studio.agent.id", metadata.AgentId);
        activity?.SetTag("a2a.context.present", metadata.ContextId is not null);
        activity?.SetTag("a2a.message_id.present", metadata.MessageId is not null);

        try
        {
            var result = await idempotencyStore.GetOrAddAsync(
                cacheKey,
                payloadHash,
                token => StreamNormalizedAsync(prompt, metadata, activity, token),
                cancellationToken);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return ToChatResponse(result, metadata);
        }
        catch (Exception exception) when (exception is not A2AException
                                          && exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            AdapterTelemetry.RecordFailureReason(activity, exception.Message);
            // Translate backend failures into a defined adapter error instead of letting an
            // arbitrary exception collapse into a generic "no response events" A2A error.
            logger.LogError(
                exception,
                "Delegated agent invocation failed for context {ContextId}.",
                metadata.ContextId);
            throw new AdapterRequestException(
                $"The delegated agent invocation failed: {exception.Message}", exception);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = AdapterTelemetry.StartActivity("a2a.adapter.get_streaming_response");
        var (prompt, metadata, cacheKey, payloadHash) = PrepareRequest(messages);
        activity?.SetTag("copilot_studio.agent.id", metadata.AgentId);
        activity?.SetTag("a2a.context.present", metadata.ContextId is not null);
        activity?.SetTag("a2a.message_id.present", metadata.MessageId is not null);

        var updates = idempotencyStore.GetOrAddStreaming(
            cacheKey,
            payloadHash,
            token => StreamNormalizedAsync(prompt, metadata, activity, token),
            cancellationToken);
        var answerStarted = false;
        await using var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (Exception exception) when (exception is not A2AException
                                              && exception is not OperationCanceledException)
            {
                AdapterTelemetry.RecordFailure(activity, exception);
                AdapterTelemetry.RecordFailureReason(activity, exception.Message);
                logger.LogError(
                    exception,
                    "Delegated agent streaming invocation failed for context {ContextId}.",
                    metadata.ContextId);
                throw new AdapterRequestException(
                    $"The delegated agent invocation failed: {exception.Message}", exception);
            }

            if (!hasNext)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                yield break;
            }

            var update = enumerator.Current;
            var responder = RequireResponder(update.Responder);
            var hasText = !string.IsNullOrEmpty(update.Text);
            var text = hasText && (update.IsInformative || !answerStarted || update.ReplacesAnswer)
                ? WithResponderHeader(update.Text, responder)
                : update.Text;
            var responseUpdate = new ChatResponseUpdate(
                ChatRole.Assistant,
                CreateResponseContents(
                    text, responder, update.Citations, update.IsInformative, update.ConversationHandle,
                    update.Cards, update.PlanUpdate, update.ReplacesAnswer))
            {
                AuthorName = responder.Name,
                ConversationId = metadata.ContextId ?? update.ConversationId,
                ResponseId = update.ResponseId,
                MessageId = $"response-{metadata.MessageId}",
                ModelId = metadata.AgentId
            };
            if (!update.IsInformative && hasText)
            {
                answerStarted = true;
            }

            yield return responseUpdate;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private (string Prompt, A2ARequestMetadata Metadata, string CacheKey, string PayloadHash)
        PrepareRequest(IEnumerable<ChatMessage> messages)
    {
        var metadata = metadataAccessor.Current;
        var prompt = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(prompt) && metadata.Files.Count > 0)
        {
            prompt = "Please summarize the attached file.";
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new AdapterRequestException("An A2A request must contain a non-empty user message.");
        }

        if (metadata.MessageId is null)
        {
            throw new AdapterRequestException(
                "An A2A request must carry a messageId so the delegated call can be made idempotent.");
        }

        var cacheKey = IdempotencyStore.BuildKey(
            metadata.UserId,
            metadata.AgentId,
            metadata.ContextId,
            metadata.MessageId);
        var payloadHash = metadata.PayloadHash ?? PayloadHash.ComputeFromPrompt(prompt);
        return (prompt, metadata, cacheKey, payloadHash);
    }

    private async IAsyncEnumerable<CopilotInvocationUpdate> StreamNormalizedAsync(
        string prompt,
        A2ARequestMetadata metadata,
        System.Diagnostics.Activity? activity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var planCount = 0;
        await foreach (var rawUpdate in invoker.StreamAsync(prompt, metadata, cancellationToken))
        {
            var update = !configuration.GetValue<bool>("Adapter:AdaptiveCardsEnabled") && rawUpdate.Cards is not null
                ? rawUpdate with { Cards = null }
                : rawUpdate;
            RequireResponder(update.Responder);
            if (update.PlanUpdate is not null)
            {
                if (++planCount > AgentPlanUpdates.MaxEvents)
                {
                    throw new AdapterRequestException("The plan stream exceeds the event limit.");
                }
                AgentPlanUpdates.Validate(update.PlanUpdate);
                update = update with
                {
                    IsInformative = true,
                    Text = AgentPlanUpdates.Summary(update.PlanUpdate)
                };
            }
            if (update.Citations is not null)
            {
                AgentCitations.Validate(update.Citations);
            }
            if (update.Cards is not null)
            {
                AgentAdaptiveCards.Validate(update.Cards);
            }
            if (CopilotStudioResponseClassifier.IsUnsupportedHarnessResponse(update.Text))
            {
                activity?.SetTag("copilot_studio.client.compatible", false);
                logger.LogWarning(
                    "Agent {AgentId} returned the retired enhanced task completion response.",
                    metadata.AgentId);
                yield return update with
                {
                    Text = CopilotStudioResponseClassifier.Guidance,
                    Citations = null,
                    Cards = null
                };
                continue;
            }

            yield return update;
        }
    }

    private static ChatResponse ToChatResponse(
        CopilotInvocationResult result,
        A2ARequestMetadata metadata)
    {
        var responder = RequireResponder(result.Responder);
        return new(new ChatMessage(
            ChatRole.Assistant,
            CreateResponseContents(
                WithResponderHeader(result.Text, responder),
                responder,
                result.Citations,
                conversationHandle: result.ConversationHandle,
                cards: result.Cards))
        {
            AuthorName = responder.Name
        })
        {
            ConversationId = metadata.ContextId ?? result.ConversationId,
            ResponseId = result.ResponseId,
            ModelId = metadata.AgentId
        };
    }

    private static AgentResponder RequireResponder(AgentResponder? responder) =>
        responder ?? throw new InvalidOperationException(
            "The delegated agent response is missing its responder identity.");

    private static string WithResponderHeader(string text, AgentResponder responder) =>
        $"Responding agent: {responder.Name}\n\n{text}";

    /// <summary>Text part metadata marking a part that replaces the whole answer streamed so far.</summary>
    internal const string ReplacesAnswerKey = "replacesAnswer";

    private static TextContent CreateResponseContent(
        string text, AgentResponder responder, bool isInformative = false, string? conversationHandle = null,
        bool replacesAnswer = false)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            ["agentId"] = responder.Id,
            ["agentName"] = responder.Name
        };
        if (isInformative)
        {
            properties["isInformative"] = true;
        }
        if (replacesAnswer)
        {
            properties[ReplacesAnswerKey] = true;
        }
        if (conversationHandle is not null)
        {
            properties[CopilotConversationHandles.PartMetadataKey] = conversationHandle;
        }

        return new TextContent(text) { AdditionalProperties = properties };
    }

    private static List<AIContent> CreateResponseContents(
        string text,
        AgentResponder responder,
        CitationBundle? citations,
        bool isInformative = false,
        string? conversationHandle = null,
        AdaptiveCardBundle? cards = null,
        AgentPlanUpdate? planUpdate = null,
        bool replacesAnswer = false)
    {
        List<AIContent> contents = [];
        if (planUpdate is not null)
        {
            contents.Add(AgentPlanUpdates.ToContent(planUpdate));
        }
        if (!string.IsNullOrEmpty(text))
        {
            contents.Add(CreateResponseContent(
                text, responder, isInformative, isInformative ? null : conversationHandle,
                replacesAnswer && !isInformative));
        }
        if (!isInformative && citations is { Sources.Count: > 0 })
        {
            contents.Add(AgentCitations.ToContent(citations));
        }
        if (!isInformative && cards is { Cards.Count: > 0 })
        {
            contents.Add(AgentAdaptiveCards.ToContent(cards));
        }
        return contents;
    }
}

internal static class CopilotStudioResponseClassifier
{
    private const string UnsupportedHarnessResponse =
        "Enhanced task completion preview has ended.";

    public const string Guidance =
        "This Copilot Studio agent uses a harness that the Microsoft 365 Agents SDK client " +
        "does not support. Create and publish a standard-harness agent in Copilot Studio, " +
        "then replace this agent's connection string.";

    public static bool IsUnsupportedHarnessResponse(string response) =>
        response.Contains(UnsupportedHarnessResponse, StringComparison.OrdinalIgnoreCase);
}
