using System.Runtime.CompilerServices;
using System.Text.Json;
using ActivityStatusCode = System.Diagnostics.ActivityStatusCode;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Options;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace FoundryCopilotA2A.Adapter;

public sealed class SdkCopilotStudioInvoker : ICopilotStudioInvoker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SdkCopilotStudioInvoker> _logger;
    private readonly OboTokenBroker _tokenBroker;
    private readonly CopilotConversationStore _conversationStore;
    private readonly CopilotStudioResponseMetadataAccessor _responseMetadataAccessor;
    private readonly CopilotConversationHandles _conversationHandles;
    private readonly int _maxReplayTurns;
    private readonly int _maxReplayCharacters;
    private readonly IReadOnlyDictionary<string, CopilotStudioAgentRuntime> _agents;

    public SdkCopilotStudioInvoker(
        IOptions<CopilotStudioOptions> options,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<SdkCopilotStudioInvoker> logger,
        OboTokenBroker tokenBroker,
        CopilotConversationStore conversationStore,
        CopilotStudioResponseMetadataAccessor responseMetadataAccessor,
        CopilotConversationHandles conversationHandles,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _tokenBroker = tokenBroker;
        _conversationStore = conversationStore;
        _responseMetadataAccessor = responseMetadataAccessor;
        _conversationHandles = conversationHandles;
        _maxReplayTurns = configuration.GetValue("Adapter:MaxReplayTurns", 6);
        _maxReplayCharacters = configuration.GetValue("Adapter:MaxReplayCharacters", 6_000);

        var value = options.Value;
        _agents = value.ResolveAgents().ToDictionary(
            agent => agent.Id,
            agent =>
            {
                var connectionSettings = new ConnectionSettings
                {
                    DirectConnectUrl = agent.DirectConnectUrl,
                    EnvironmentId = agent.EnvironmentId,
                    SchemaName = agent.SchemaName,
                    // Without an explicit cloud the SDK leaves this as PowerPlatformCloud.Unknown and
                    // ScopeFromSettings throws "Invalid cluster category value: Unknown" at startup.
                    Cloud = value.Cloud,
                    CustomPowerPlatformCloud = string.IsNullOrWhiteSpace(value.CustomPowerPlatformCloud)
                        ? null
                        : value.CustomPowerPlatformCloud,
                    CopilotAgentType = value.AgentType
                };

                // The SDK token callback receives the request URI, not an OAuth scope. Derive the
                // correct scope once from each configured connection instead of passing that URI to MSAL.
                return new CopilotStudioAgentRuntime(
                    connectionSettings,
                    CopilotClient.ScopeFromSettings(connectionSettings),
                    agent.DisplayName,
                    agent.ChainTargets.Count > 0);
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.invoke");
        traceActivity?.SetTag("copilot_studio.backend", "sdk");
        traceActivity?.SetTag("copilot_studio.agent.id", metadata.AgentId);
        var invocationStarted = Stopwatch.GetTimestamp();
        var invocationCompleted = false;

        if (!_agents.TryGetValue(metadata.AgentId, out var agent))
        {
            throw new AdapterRequestException(
                $"Copilot Studio agent '{metadata.AgentId}' is not configured.");
        }

        using var genAiActivity = GenAiTelemetry.StartInvokeAgent(
            GenAiTelemetry.Providers.CopilotStudio,
            agent.DisplayName,
            metadata.AgentId,
            metadata.ContextId);
        try
        {
            await using var enumerator = StreamCoreAsync(
                    agent, prompt, metadata, traceActivity, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (Exception exception)
                {
                    AdapterTelemetry.RecordFailure(traceActivity, exception);
                    GenAiTelemetry.RecordFailure(genAiActivity, exception);
                    throw;
                }

                if (!hasNext)
                {
                    invocationCompleted = true;
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            AdapterTelemetry.RecordCopilotStudioInvocation(
                traceActivity,
                Stopwatch.GetElapsedTime(invocationStarted),
                metadata.AgentId,
                invocationCompleted);
        }
    }

    private async IAsyncEnumerable<CopilotInvocationUpdate> StreamCoreAsync(
        CopilotStudioAgentRuntime agent,
        string prompt,
        A2ARequestMetadata metadata,
        System.Diagnostics.Activity? traceActivity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var isAppOnlyCaller = TokenInspector.IsAppOnly(metadata.BearerToken ?? string.Empty);
        var accessToken = await _tokenBroker.AcquireAsync(agent.Scope, metadata, cancellationToken);
        var client = CreateClient(agent, accessToken);

        var cachedConversationId = ResolveConversation(metadata, traceActivity);

        // A reused conversation already has its transcript; replay only when starting a new one.
        prompt = A2AInputFiles.FramePrompt(prompt, metadata.Files);
        var promptWithHistory = ConversationTranscript.Prepend(
            prompt, metadata.History, _maxReplayTurns, _maxReplayCharacters);

        var effectivePrompt = cachedConversationId is null ? promptWithHistory : prompt;
        var restarted = false;
        while (true)
        {
            var emitted = false;
            await using var enumerator = ExecuteTurnStreamAsync(
                    client,
                    effectivePrompt,
                    metadata,
                    cachedConversationId,
                    cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                // An app-only caller needs the CopilotStudio.Copilots.Invoke application role.
                catch (HttpRequestException exception) when (isAppOnlyCaller &&
                    (exception.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                     exception.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new AdapterRequestException(
                        "Copilot Studio rejected an application-only call with 403. Grant the " +
                        "'CopilotStudio.Copilots.Invoke' application permission to the adapter app " +
                        "registration and have an administrator consent to it, or call the adapter with " +
                        "a delegated user token instead.");
                }
                // Never restart an orchestrator: its tools may already have run before any output.
                catch (Exception exception) when (!emitted &&
                                                  cachedConversationId is not null &&
                                                  !agent.IsOrchestrator &&
                                                  !restarted &&
                                                  exception is not CitationFormatException &&
                                                  exception is not OperationCanceledException)
                {
                    traceActivity?.AddEvent(new System.Diagnostics.ActivityEvent(
                        "copilot_studio.conversation.restart"));
                    traceActivity?.SetTag("copilot_studio.conversation.restarted", true);
                    _logger.LogWarning(
                        exception,
                        "Cached Copilot Studio conversation was rejected; restarting the conversation once.");
                    _conversationStore.Remove(metadata.UserId, metadata.AgentId, metadata.ContextId);
                    cachedConversationId = null;
                    effectivePrompt = promptWithHistory;
                    restarted = true;
                    break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                emitted = true;
                yield return enumerator.Current;
            }
        }
    }

    private string? ResolveConversation(
        A2ARequestMetadata metadata,
        System.Diagnostics.Activity? traceActivity)
    {
        var cachedConversationId = _conversationStore.Get(
            metadata.UserId,
            metadata.AgentId,
            metadata.ContextId);
        var conversationSource = cachedConversationId is null ? null : "adapter_cache";
        if (cachedConversationId is null &&
            _conversationHandles.Resolve(metadata) is { } handleConversationId)
        {
            cachedConversationId = handleConversationId;
            conversationSource = "client_handle";
        }
        traceActivity?.SetTag("copilot_studio.conversation.reused", cachedConversationId is not null);
        if (conversationSource is not null)
        {
            traceActivity?.SetTag("copilot_studio.conversation.source", conversationSource);
        }

        return cachedConversationId;
    }

    private async IAsyncEnumerable<CopilotInvocationUpdate> ExecuteTurnStreamAsync(
        CopilotClient client,
        string prompt,
        A2ARequestMetadata metadata,
        string? conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.execute_turn");
        traceActivity?.SetTag("copilot_studio.conversation.reused", conversationId is not null);
        OAuthCardInfo? oauthCard = null;

        if (conversationId is null)
        {
            (conversationId, oauthCard) = await StartConversationAsync(
                client, metadata.AgentId, traceActivity, cancellationToken);
        }

        if (conversationId is null)
        {
            var exception = new CopilotStudioResponseException(
                "Copilot Studio did not return a conversation id in the activity stream or response header. " +
                "Confirm that the configured agent is published and uses the standard harness.");
            AdapterTelemetry.RecordFailure(traceActivity, exception);
            AdapterTelemetry.RecordFailureReason(traceActivity, exception.Message);
            throw exception;
        }

        // The agent may demand a signed-in user before it will answer anything.
        if (oauthCard is not null)
        {
            traceActivity?.SetTag("copilot_studio.token_exchange.required", true);
            await PerformTokenExchangeAsync(
                client, conversationId, oauthCard.Value, metadata, cancellationToken);
        }

        var activitySummary = new CopilotStudioActivitySummary();
        var answer = ReadAnswerStreamAsync(
            metadata.Files.Count == 0
                ? client.AskQuestionAsync(prompt, conversationId, cancellationToken)
                : client.ExecuteAsync(
                    conversationId, A2AInputFiles.CreateMessage(prompt, metadata.Files), cancellationToken),
            conversationId,
            new AgentResponder(metadata.AgentId, _agents[metadata.AgentId].DisplayName),
            activitySummary,
            allowOAuthChallenge: true,
            cancellationToken);
        var emittedFinalText = false;
        string? conversationHandle = null;
        CopilotInvocationUpdate AttachConversationHandle(CopilotInvocationUpdate update)
        {
            if (conversationHandle is not null ||
                update.IsInformative ||
                string.IsNullOrWhiteSpace(update.Text))
            {
                return update;
            }

            conversationHandle = _conversationHandles.Issue(metadata, conversationId!);
            return conversationHandle is null ? update : update with { ConversationHandle = conversationHandle };
        }

        OAuthCardInfo? cardDuringTurn = null;
        await foreach (var item in answer)
        {
            cardDuringTurn ??= item.Card;
            if (item.Update is not null)
            {
                // A stream of whitespace fragments is not a usable answer, so it must not
                // satisfy the response check or suppress the sign-in retry below.
                emittedFinalText |= HasUsableAnswer(item.Update);
                yield return AttachConversationHandle(item.Update);
            }
        }

        // The card can also arrive in response to the user's message rather than at startup.
        if (!emittedFinalText && cardDuringTurn is not null)
        {
            traceActivity?.SetTag("copilot_studio.token_exchange.required", true);
            await PerformTokenExchangeAsync(
                client, conversationId, cardDuringTurn.Value, metadata, cancellationToken);

            await foreach (var item in ReadAnswerStreamAsync(
                client.ExecuteAsync(
                    conversationId,
                    A2AInputFiles.CreateMessage(prompt, metadata.Files),
                    cancellationToken),
                conversationId,
                new AgentResponder(metadata.AgentId, _agents[metadata.AgentId].DisplayName),
                activitySummary,
                allowOAuthChallenge: false,
                cancellationToken))
            {
                if (item.Update is not null)
                {
                    emittedFinalText |= HasUsableAnswer(item.Update);
                    yield return AttachConversationHandle(item.Update);
                }
            }
        }

        if (!emittedFinalText)
        {
            var exception = new CopilotStudioResponseException(
                activitySummary.CreateEmptyResponseMessage());
            AdapterTelemetry.RecordFailure(traceActivity, exception);
            AdapterTelemetry.RecordFailureReason(traceActivity, exception.Message);
            _logger.LogWarning(
                "Copilot Studio agent {AgentId} returned no usable text response. " +
                "Activities: {ActivityCount}; types: {ActivityTypes}; messages: {MessageCount}; " +
                "text messages: {TextMessageCount}; attachments: {AttachmentCount}; OAuth card: {OAuthCardPresent}.",
                metadata.AgentId,
                activitySummary.ActivityCount,
                activitySummary.ActivityTypes,
                activitySummary.MessageCount,
                activitySummary.TextMessageCount,
                activitySummary.AttachmentCount,
                activitySummary.OAuthCardPresent);
            throw exception;
        }

        _conversationStore.Set(
            metadata.UserId,
            metadata.AgentId,
            metadata.ContextId,
            conversationId);
        traceActivity?.SetTag("copilot_studio.response.present", true);
        traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
    }

    private async Task<(string? ConversationId, OAuthCardInfo? OAuthCard)> StartConversationAsync(
        CopilotClient client,
        string agentId,
        System.Diagnostics.Activity? turnActivity,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("copilot_studio.start_conversation");
        activity?.SetTag("copilot_studio.agent.id", agentId);
        var started = Stopwatch.GetTimestamp();
        var created = false;
        try
        {
            string? conversationId = null;
            string? source = null;
            OAuthCardInfo? oauthCard = null;
            using var responseMetadata = _responseMetadataAccessor.BeginCapture();
            await foreach (var message in client.StartConversationAsync(
                emitStartConversationEvent: true,
                cancellationToken: cancellationToken))
            {
                if (message is null)
                {
                    continue;
                }

                if (conversationId is null && message.Conversation?.Id is { } messageConversationId)
                {
                    conversationId = messageConversationId;
                    source = "activity_stream";
                }

                oauthCard ??= ExtractOAuthCard(message);
            }

            if (conversationId is null && responseMetadata.ConversationId is { } headerConversationId)
            {
                conversationId = headerConversationId;
                source = "response_header";
            }

            created = conversationId is not null;
            if (source is not null)
            {
                turnActivity?.SetTag("copilot_studio.conversation.id.source", source);
                activity?.SetTag("copilot_studio.conversation.id.source", source);
            }

            activity?.SetStatus(created ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            return (conversationId, oauthCard);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            AdapterTelemetry.RecordCopilotStudioConversationStart(
                activity, Stopwatch.GetElapsedTime(started), agentId, created);
        }
    }

    private static bool HasUsableAnswer(CopilotInvocationUpdate update) =>
        !update.IsInformative && !string.IsNullOrWhiteSpace(update.Text);

    private static string? ReadMessageText(
        IActivity activity,
        string? connectionManagerText,
        AgentResponder responder)
    {
        if (!string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(activity.Text))
        {
            return activity.Text;
        }

        return connectionManagerText ??
            CopilotStudioAdaptiveCards.Read(activity, responder)?.Cards[0].FallbackText;
    }

    private static async IAsyncEnumerable<AnswerStreamItem> ReadAnswerStreamAsync(
        IAsyncEnumerable<IActivity> activities,
        string conversationId,
        AgentResponder responder,
        CopilotStudioActivitySummary turnSummary,
        bool allowOAuthChallenge,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.collect_answer");
        traceActivity?.SetTag("copilot_studio.agent.id", responder.Id);
        var collectionSummary = new CopilotStudioActivitySummary();
        var answerStream = new CopilotStudioAnswerStream();
        var progress = new CopilotStudioProgress();
        var planSequence = 0;
        var streamStarted = Stopwatch.GetTimestamp();
        var firstActivityRecorded = false;
        var firstProgressRecorded = false;
        var firstAnswerRecorded = false;
        var streamCompleted = false;
        try
        {
            await foreach (var activity in activities.WithCancellation(cancellationToken))
            {
                if (activity is null)
                {
                    continue;
                }

                if (!firstActivityRecorded)
                {
                    firstActivityRecorded = true;
                    AdapterTelemetry.RecordCopilotStudioStreamMilestone(
                        traceActivity,
                        AdapterTelemetry.StreamMilestone.FirstActivity,
                        Stopwatch.GetElapsedTime(streamStarted),
                        responder.Id);
                }

                var card = ExtractOAuthCard(activity);
                var connectionManagerText =
                    CopilotStudioAttachmentText.ExtractConnectionManagerCardText(activity);
                var messageText = ReadMessageText(activity, connectionManagerText, responder);
                var planUpdate = AgentPlanUpdates.ReadActivity(activity, responder, planSequence + 1);
                if (planUpdate is not null)
                {
                    planSequence++;
                }

                // A suppressed final message is still observed, so a fully streamed turn is never
                // misreported as an empty response.
                collectionSummary.Observe(
                    activity,
                    card is not null,
                    messageText,
                    connectionManagerText is not null);
                if (messageText is not null)
                {
                    messageText = collectionSummary.EnrichFailureResponse(messageText);
                }
                if (card is not null)
                {
                    yield return new AnswerStreamItem(null, card);
                }

                var update = planUpdate is not null
                    ? new CopilotInvocationUpdate(
                        AgentPlanUpdates.Summary(planUpdate), conversationId, activity.Id, IsInformative: true)
                    { PlanUpdate = planUpdate }
                    : answerStream.NextUpdate(
                        activity,
                        messageText,
                        conversationId,
                        responder);
                if (update is null)
                {
                    update = progress.Next(activity, conversationId);
                    if (update is null)
                    {
                        continue;
                    }
                }
                else
                {
                    progress.Observe(update);
                }

                if (!firstProgressRecorded && !string.IsNullOrWhiteSpace(update.Text))
                {
                    firstProgressRecorded = true;
                    AdapterTelemetry.RecordCopilotStudioStreamMilestone(
                        traceActivity,
                        AdapterTelemetry.StreamMilestone.FirstProgress,
                        Stopwatch.GetElapsedTime(streamStarted),
                        responder.Id);
                }

                if (!firstAnswerRecorded && HasUsableAnswer(update))
                {
                    firstAnswerRecorded = true;
                    AdapterTelemetry.RecordCopilotStudioStreamMilestone(
                        traceActivity,
                        AdapterTelemetry.StreamMilestone.FirstAnswer,
                        Stopwatch.GetElapsedTime(streamStarted),
                        responder.Id);
                }

                yield return new AnswerStreamItem(update, null);
            }

            streamCompleted = true;
        }
        finally
        {
            traceActivity?.SetTag("copilot_studio.stream.delta.count", answerStream.DeltaCount);
            traceActivity?.SetTag(
                "copilot_studio.stream.final.suppressed",
                answerStream.SuppressedFinalCount);
            traceActivity?.SetTag(
                "copilot_studio.stream.final.replaced",
                answerStream.ReplacedFinalCount);
            AdapterTelemetry.RecordCopilotStudioAnswerStream(
                traceActivity,
                Stopwatch.GetElapsedTime(streamStarted),
                responder.Id,
                streamCompleted);
        }

        turnSummary.Merge(collectionSummary);
        collectionSummary.RecordTelemetry(traceActivity, allowOAuthChallenge);
    }

    private sealed record AnswerStreamItem(
        CopilotInvocationUpdate? Update,
        OAuthCardInfo? Card);

    /// <summary>
    /// Answers the agent's OAuthCard challenge with the caller's own token so Copilot Studio
    /// can act as the delegated user. Without this, an authentication-enabled agent never
    /// produces a message activity and the delegated call looks like an empty response.
    /// </summary>
    private async Task PerformTokenExchangeAsync(
        CopilotClient client,
        string conversationId,
        OAuthCardInfo card,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.token_exchange");
        if (string.IsNullOrEmpty(metadata.BearerToken))
        {
            throw new UnauthorizedAccessException(
                "The Copilot Studio agent requires a signed-in user, but the request carried no bearer token.");
        }

        // Never relay a token to a resource it was not minted for.
        if (!string.IsNullOrEmpty(card.ExchangeResourceUri))
        {
            var audience = TokenInspector.ReadAudience(metadata.BearerToken);
            if (audience is not null &&
                !string.Equals(audience, card.ExchangeResourceUri, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "The caller token audience does not match the resource requested by the Copilot Studio agent.");
            }
        }

        _logger.LogInformation(
            "Performing SSO token exchange for connection {Connection}.", card.ConnectionName);

        var exchange = new Activity
        {
            Type = "invoke",
            Name = "signin/tokenExchange",
            Value = new TokenExchangeInvokeRequest
            {
                ConnectionName = card.ConnectionName,
                Id = card.ExchangeResourceId,
                Token = metadata.BearerToken
            }
        };

        await foreach (var _ in client.ExecuteAsync(conversationId, exchange, cancellationToken))
        {
            // Drain the exchange response; the answer arrives on the following turn.
        }

        traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
    }

    private CopilotClient CreateClient(CopilotStudioAgentRuntime agent, string accessToken) =>
        new(
            agent.ConnectionSettings,
            _httpClientFactory,
            // The argument is the request URI and is deliberately discarded: the token was
            // already acquired for the correct Copilot Studio scope.
            (string _) => Task.FromResult(accessToken),
            _loggerFactory.CreateLogger<CopilotClient>(),
            agent.IsOrchestrator ? "copilot-studio-orchestrator" : "copilot-studio");

    private static OAuthCardInfo? ExtractOAuthCard(IActivity activity)
    {
        if (activity.Attachments is null || activity.Attachments.Count == 0)
        {
            return null;
        }

        foreach (var attachment in activity.Attachments)
        {
            if (!string.Equals(attachment.ContentType, OAuthCard.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var card = attachment.Content switch
            {
                OAuthCard typed => typed,
                JsonElement element => Deserialize(element.GetRawText()),
                not null => Deserialize(JsonSerializer.Serialize(attachment.Content)),
                _ => null
            };

            if (card is not null && !string.IsNullOrEmpty(card.ConnectionName))
            {
                return new OAuthCardInfo(
                    card.ConnectionName,
                    card.TokenExchangeResource?.Id,
                    card.TokenExchangeResource?.Uri);
            }
        }

        return null;

        static OAuthCard? Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<OAuthCard>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    internal readonly record struct OAuthCardInfo(
        string ConnectionName,
        string? ExchangeResourceId,
        string? ExchangeResourceUri);

    private sealed record CopilotStudioAgentRuntime(
        ConnectionSettings ConnectionSettings,
        string Scope,
        string DisplayName,
        bool IsOrchestrator);
}

/// <summary>
/// Raised when Copilot Studio completes a request without returning the protocol data required
/// to continue the delegated turn.
/// </summary>
public sealed class CopilotStudioResponseException(string message) : Exception(message);

internal sealed class CopilotStudioPlanFailureException(string message) : Exception(message);
