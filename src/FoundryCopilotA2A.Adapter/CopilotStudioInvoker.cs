using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Adapter;

public sealed record CopilotInvocationResult(
    string Text,
    string? ConversationId,
    string? ResponseId);

public sealed record CopilotInvocationUpdate(
    string Text,
    string? ConversationId,
    string? ResponseId,
    bool IsInformative = false);

public interface ICopilotStudioInvoker
{
    IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed class MockCopilotStudioInvoker : ICopilotStudioInvoker
{
    public const string FailureReason =
        "The mock Copilot Studio service is unavailable. Try the request again.";

    /// <summary>Prompt that makes the mock reproduce a streamed Copilot Studio answer.</summary>
    public const string StreamProgressPrompt = "stream progress response";

    /// <summary>The answer the mock streams token by token for <see cref="StreamProgressPrompt"/>.</summary>
    public static readonly string[] StreamedAnswerDeltas =
        ["mock-copilot-studio", ": streamed", " answer", "."];

    private int _invocationCount;

    public int InvocationCount => _invocationCount;

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

        var conversationId = metadata.ContextId ?? $"mock-{Guid.NewGuid():N}";
        var historySuffix = metadata.History.Count == 0
            ? string.Empty
            : $" | context: {metadata.History.Count} earlier turns";

        await Task.Yield();
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
            $"mock-copilot-studio[{invocation}]: {prompt}{historySuffix}",
            conversationId,
            $"mock-response-{invocation}");
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

/// <summary>
/// Maps an A2A contextId to a Copilot Studio conversationId, partitioned by caller identity.
/// Bounded and sliding-expiry so it cannot grow without limit.
/// </summary>
public sealed class CopilotConversationStore : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CopilotConversationStore(IOptions<AdapterOptions> options)
    {
        _ttl = TimeSpan.FromMinutes(options.Value.ConversationTtlMinutes);
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = options.Value.MaxCacheEntries,
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });
    }

    public string? Get(string userId, string agentId, string? contextId) =>
        contextId is null ? null : _cache.Get<string>(ToKey(userId, agentId, contextId));

    public void Set(string userId, string agentId, string? contextId, string conversationId)
    {
        if (contextId is null)
        {
            return;
        }

        _cache.Set(ToKey(userId, agentId, contextId), conversationId, new MemoryCacheEntryOptions
        {
            SlidingExpiration = _ttl,
            Size = 1
        });
    }

    public void Remove(string userId, string agentId, string? contextId)
    {
        if (contextId is not null)
        {
            _cache.Remove(ToKey(userId, agentId, contextId));
        }
    }

    private static string ToKey(string userId, string agentId, string contextId) =>
        $"{userId}|{agentId}|{contextId}";

    public void Dispose() => _cache.Dispose();
}

public sealed class SdkCopilotStudioInvoker : ICopilotStudioInvoker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SdkCopilotStudioInvoker> _logger;
    private readonly OboTokenBroker _tokenBroker;
    private readonly CopilotConversationStore _conversationStore;
    private readonly IReadOnlyDictionary<string, CopilotStudioAgentRuntime> _agents;

    public SdkCopilotStudioInvoker(
        IOptions<CopilotStudioOptions> options,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<SdkCopilotStudioInvoker> logger,
        OboTokenBroker tokenBroker,
        CopilotConversationStore conversationStore)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _tokenBroker = tokenBroker;
        _conversationStore = conversationStore;

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
                    agent.DisplayName);
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
                GenAiTelemetry.RecordFailure(genAiActivity, exception);
                throw;
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return enumerator.Current;
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
        var client = CreateClient(agent.ConnectionSettings, accessToken);

        var cachedConversationId = _conversationStore.Get(
            metadata.UserId,
            metadata.AgentId,
            metadata.ContextId);
        traceActivity?.SetTag("copilot_studio.conversation.reused", cachedConversationId is not null);

        // Copilot Studio keeps the transcript against the conversation id, so history is only
        // replayed when a new conversation has to be started for an existing A2A context.
        var promptWithHistory = ConversationTranscript.Prepend(prompt, metadata.History);

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
                catch (Exception exception) when (!emitted &&
                                                  cachedConversationId is not null &&
                                                  !restarted &&
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
            await foreach (var activity in client.StartConversationAsync(
                emitStartConversationEvent: true,
                cancellationToken: cancellationToken))
            {
                if (activity is null)
                {
                    continue;
                }

                conversationId ??= activity.Conversation?.Id;
                oauthCard ??= ExtractOAuthCard(activity);
            }
        }

        if (conversationId is null)
        {
            var exception = new CopilotStudioResponseException(
                "Copilot Studio did not return a conversation id when starting the conversation. " +
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
            client.AskQuestionAsync(prompt, conversationId, cancellationToken),
            conversationId,
            activitySummary,
            allowOAuthChallenge: true,
            cancellationToken);
        var emittedFinalText = false;
        OAuthCardInfo? cardDuringTurn = null;
        await foreach (var item in answer)
        {
            cardDuringTurn ??= item.Card;
            if (item.Update is not null)
            {
                // A stream of whitespace fragments is not a usable answer, so it must not
                // satisfy the response check or suppress the sign-in retry below.
                emittedFinalText |= !item.Update.IsInformative &&
                                    !string.IsNullOrWhiteSpace(item.Update.Text);
                yield return item.Update;
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
                    CreateMessageActivity(prompt),
                    cancellationToken),
                conversationId,
                activitySummary,
                allowOAuthChallenge: false,
                cancellationToken))
            {
                if (item.Update is not null)
                {
                    emittedFinalText |= !item.Update.IsInformative &&
                                        !string.IsNullOrWhiteSpace(item.Update.Text);
                    yield return item.Update;
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

    private static async IAsyncEnumerable<AnswerStreamItem> ReadAnswerStreamAsync(
        IAsyncEnumerable<IActivity> activities,
        string conversationId,
        CopilotStudioActivitySummary turnSummary,
        bool allowOAuthChallenge,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.collect_answer");
        var collectionSummary = new CopilotStudioActivitySummary();
        var answerStream = new CopilotStudioAnswerStream();

        await foreach (var activity in activities.WithCancellation(cancellationToken))
        {
            if (activity is null)
            {
                continue;
            }

            var card = ExtractOAuthCard(activity);
            var connectionManagerText =
                CopilotStudioAttachmentText.ExtractConnectionManagerCardText(activity);
            var isMessage =
                string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase);
            var messageText = isMessage
                ? string.IsNullOrWhiteSpace(activity.Text)
                    ? connectionManagerText
                    : activity.Text
                : null;

            // A suppressed final message is still observed, so a fully streamed turn is never
            // misreported as an empty response.
            collectionSummary.Observe(
                activity,
                card is not null,
                messageText,
                connectionManagerText is not null);
            if (card is not null)
            {
                yield return new AnswerStreamItem(null, card);
            }

            if (answerStream.Next(activity, messageText) is not { } chunk)
            {
                continue;
            }

            yield return new AnswerStreamItem(
                new CopilotInvocationUpdate(
                    chunk.Text,
                    conversationId,
                    activity.Id,
                    chunk.IsInformative),
                null);
        }

        traceActivity?.SetTag("copilot_studio.stream.delta.count", answerStream.DeltaCount);
        traceActivity?.SetTag(
            "copilot_studio.stream.final.suppressed",
            answerStream.SuppressedFinalCount);
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

    private static Activity CreateMessageActivity(string text)
    {
        var activity = Activity.CreateMessageActivity();
        activity.Text = text;
        return (Activity)activity;
    }

    private CopilotClient CreateClient(ConnectionSettings connectionSettings, string accessToken) =>
        new(
            connectionSettings,
            _httpClientFactory,
            // The argument is the request URI and is deliberately discarded: the token was
            // already acquired for the correct Copilot Studio scope.
            (string _) => Task.FromResult(accessToken),
            _loggerFactory.CreateLogger<CopilotClient>(),
            "copilot-studio");

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
        string DisplayName);
}

/// <summary>
/// Raised when Copilot Studio completes a request without returning the protocol data required
/// to continue the delegated turn.
/// </summary>
public sealed class CopilotStudioResponseException(string message) : Exception(message);

internal sealed class CopilotStudioActivitySummary
{
    private readonly HashSet<string> _activityTypes = new(StringComparer.Ordinal);

    public int ActivityCount { get; private set; }

    public int MessageCount { get; private set; }

    public int TextMessageCount { get; private set; }

    public int AdaptiveCardTextMessageCount { get; private set; }

    public int AttachmentCount { get; private set; }

    public bool OAuthCardPresent { get; private set; }

    public bool HasTextResponse => TextMessageCount > 0;

    public string ActivityTypes =>
        _activityTypes.Count == 0
            ? "none"
            : string.Join(",", _activityTypes.Order(StringComparer.Ordinal));

    public void Observe(
        IActivity activity,
        bool oauthCardPresent,
        string? effectiveText = null,
        bool extractedFromAdaptiveCard = false)
    {
        ActivityCount++;
        _activityTypes.Add(NormalizeActivityType(activity.Type));
        AttachmentCount += activity.Attachments?.Count ?? 0;
        OAuthCardPresent |= oauthCardPresent;

        if (!string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MessageCount++;
        if (!string.IsNullOrWhiteSpace(effectiveText ?? activity.Text))
        {
            TextMessageCount++;
            if (extractedFromAdaptiveCard)
            {
                AdaptiveCardTextMessageCount++;
            }
        }
    }

    public void Merge(CopilotStudioActivitySummary other)
    {
        ActivityCount += other.ActivityCount;
        MessageCount += other.MessageCount;
        TextMessageCount += other.TextMessageCount;
        AdaptiveCardTextMessageCount += other.AdaptiveCardTextMessageCount;
        AttachmentCount += other.AttachmentCount;
        OAuthCardPresent |= other.OAuthCardPresent;
        _activityTypes.UnionWith(other._activityTypes);
    }

    public string CreateEmptyResponseMessage()
    {
        const string guidance =
            "The adapter requires at least one text message; make sure every published agent " +
            "route sends a final text response.";

        if (ActivityCount == 0)
        {
            return "Copilot Studio completed the delegated request without returning any activities. " +
                   guidance;
        }

        if (MessageCount == 0)
        {
            return $"Copilot Studio returned {ActivityCount} activities (types: {ActivityTypes}), " +
                   $"but none was a message. {guidance}";
        }

        var attachmentDescription = AttachmentCount == 0
            ? string.Empty
            : $" with {AttachmentCount} attachment{(AttachmentCount == 1 ? string.Empty : "s")}";
        return $"Copilot Studio returned {MessageCount} message " +
               $"activit{(MessageCount == 1 ? "y" : "ies")}{attachmentDescription}, but no text. " +
               guidance;
    }

    public void RecordTelemetry(System.Diagnostics.Activity? activity, bool allowOAuthChallenge)
    {
        activity?.SetTag("copilot_studio.activity.count", ActivityCount);
        activity?.SetTag("copilot_studio.activity.types", ActivityTypes);
        activity?.SetTag("copilot_studio.message.count", MessageCount);
        activity?.SetTag("copilot_studio.message.text.count", TextMessageCount);
        activity?.SetTag(
            "copilot_studio.message.adaptive_card_text.count",
            AdaptiveCardTextMessageCount);
        activity?.SetTag("copilot_studio.attachment.count", AttachmentCount);
        activity?.SetTag("copilot_studio.oauth_card.present", OAuthCardPresent);
        activity?.SetTag("copilot_studio.response.present", HasTextResponse);

        if (HasTextResponse || (allowOAuthChallenge && OAuthCardPresent))
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return;
        }

        var exception = new CopilotStudioResponseException(CreateEmptyResponseMessage());
        AdapterTelemetry.RecordFailure(activity, exception);
        AdapterTelemetry.RecordFailureReason(activity, exception.Message);
    }

    private static string NormalizeActivityType(string? activityType) =>
        activityType?.ToLowerInvariant() switch
        {
            "command" => "command",
            "commandresult" => "commandResult",
            "contactrelationupdate" => "contactRelationUpdate",
            "conversationupdate" => "conversationUpdate",
            "endofconversation" => "endOfConversation",
            "event" => "event",
            "handoff" => "handoff",
            "installationupdate" => "installationUpdate",
            "invoke" => "invoke",
            "message" => "message",
            "messagereaction" => "messageReaction",
            "suggestion" => "suggestion",
            "trace" => "trace",
            "typing" => "typing",
            null or "" => "unknown",
            _ => "other"
        };
}

internal static class CopilotStudioAttachmentText
{
    private const string AdaptiveCardContentType = "application/vnd.microsoft.card.adaptive";
    private const string ConnectionManagerActivityName = "connectors/connectionManagerCard";

    public static string? ExtractConnectionManagerCardText(IActivity activity)
    {
        if (!string.Equals(
                activity.Name,
                ConnectionManagerActivityName,
                StringComparison.OrdinalIgnoreCase) ||
            activity.Attachments is null)
        {
            return null;
        }

        var textBlocks = new List<string>();
        foreach (var attachment in activity.Attachments)
        {
            if (!string.Equals(
                    attachment.ContentType,
                    AdaptiveCardContentType,
                    StringComparison.OrdinalIgnoreCase) ||
                attachment.Content is null)
            {
                continue;
            }

            var content = attachment.Content switch
            {
                JsonElement element => element,
                JsonDocument document => document.RootElement,
                _ => JsonSerializer.SerializeToElement(
                    attachment.Content,
                    attachment.Content.GetType())
            };
            CollectTextBlocks(content, textBlocks);
        }

        return textBlocks.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, textBlocks);
    }

    private static void CollectTextBlocks(JsonElement element, List<string> textBlocks)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectTextBlocks(item, textBlocks);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "TextBlock", StringComparison.OrdinalIgnoreCase) &&
            element.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(text.GetString()))
        {
            textBlocks.Add(text.GetString()!);
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                CollectTextBlocks(property.Value, textBlocks);
            }
        }
    }
}

internal readonly record struct CopilotStudioAnswerChunk(string Text, bool IsInformative);

/// <summary>
/// Assembles one Copilot Studio turn into ordered chunks.
/// <para>
/// A streamed answer arrives as informative typing ("Generating plan..."), then typing deltas
/// that build the answer token by token, then a final message whose text is exactly the
/// concatenation of those deltas. All three share one stream id. Forwarding both the deltas and
/// the final message would therefore return the answer twice, so a final message is dropped once
/// its stream has already been forwarded. Agents that never stream still send only a message,
/// which is forwarded unchanged.
/// </para>
/// </summary>
internal sealed class CopilotStudioAnswerStream
{
    private readonly HashSet<string> _streamedIds = new(StringComparer.Ordinal);
    private bool _needsSeparator;

    public int DeltaCount { get; private set; }

    public int SuppressedFinalCount { get; private set; }

    /// <param name="messageText">
    /// The resolved text of a message activity, which may come from an adaptive card rather than
    /// <see cref="IActivity.Text"/>. Null for every activity that is not a message.
    /// </param>
    /// <returns>The chunk to forward, or null when the activity contributes nothing.</returns>
    public CopilotStudioAnswerChunk? Next(IActivity activity, string? messageText)
    {
        var isMessage = messageText is not null ||
                        string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase);
        var stream = CopilotStudioStreamingText.ReadStreamInfo(activity);

        // Only a delta that carried text can stand in for the final message; a stream of empty
        // deltas must still let that message through.
        var isDelta = stream.Role == CopilotStudioStreamRole.Delta &&
                      !string.IsNullOrEmpty(activity.Text);

        if (stream.Role == CopilotStudioStreamRole.Final &&
            stream.StreamId is not null &&
            _streamedIds.Contains(stream.StreamId))
        {
            SuppressedFinalCount++;
            return null;
        }

        var text = isMessage
            ? messageText
            : stream.Role is CopilotStudioStreamRole.Informative or CopilotStudioStreamRole.Delta
                ? activity.Text
                : null;

        // A delta may legitimately be a lone space or newline, so answer fragments are dropped
        // only when truly empty; everything else keeps the whitespace guard.
        if (isDelta ? string.IsNullOrEmpty(text) : string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!isMessage && !isDelta)
        {
            return new CopilotStudioAnswerChunk(text!, IsInformative: true);
        }

        // Deltas are fragments of a single answer, so only the first is separated from whatever
        // preceded it and the rest concatenate verbatim.
        var continuesStream = isDelta &&
                              stream.StreamId is not null &&
                              _streamedIds.Contains(stream.StreamId);
        var separated = _needsSeparator && !continuesStream
            ? Environment.NewLine + text
            : text!;
        _needsSeparator = true;

        if (isDelta)
        {
            DeltaCount++;
            if (stream.StreamId is not null)
            {
                _streamedIds.Add(stream.StreamId);
            }
        }

        return new CopilotStudioAnswerChunk(separated, IsInformative: false);
    }
}

/// <summary>What a Copilot Studio activity contributes to the streamed answer.</summary>
internal enum CopilotStudioStreamRole
{
    /// <summary>Not part of a stream; treated as a standalone response.</summary>
    None,

    /// <summary>Transient progress such as "Generating plan..."; never part of the answer.</summary>
    Informative,

    /// <summary>An incremental chunk of the answer.</summary>
    Delta,

    /// <summary>The completed answer. Repeats every delta already sent for the same stream.</summary>
    Final
}

internal readonly record struct CopilotStudioStreamInfo(
    CopilotStudioStreamRole Role,
    string? StreamId)
{
    public static readonly CopilotStudioStreamInfo None = new(CopilotStudioStreamRole.None, null);
}

internal static class CopilotStudioStreamingText
{
    /// <summary>
    /// Classifies an activity against the Copilot Studio streaming protocol. A streamed answer
    /// arrives as informative typing, then delta typing, then a final message that repeats the
    /// concatenation of those deltas, all sharing one stream id.
    /// </summary>
    public static CopilotStudioStreamInfo ReadStreamInfo(IActivity activity)
    {
        if (activity.ChannelData is null)
        {
            return CopilotStudioStreamInfo.None;
        }

        var channelData = activity.ChannelData switch
        {
            JsonElement element => element,
            JsonDocument document => document.RootElement,
            _ => JsonSerializer.SerializeToElement(
                activity.ChannelData,
                activity.ChannelData.GetType())
        };

        if (channelData.ValueKind != JsonValueKind.Object ||
            !channelData.TryGetProperty("streamType", out var streamType) ||
            streamType.ValueKind != JsonValueKind.String)
        {
            return CopilotStudioStreamInfo.None;
        }

        // Progress and deltas ride on typing activities; only the committed answer is a message.
        // Anything else carrying a streamType is outside the protocol and is ignored.
        var isTyping = string.Equals(activity.Type, "typing", StringComparison.OrdinalIgnoreCase);
        var isMessage = string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase);
        var role = streamType.GetString()?.ToLowerInvariant() switch
        {
            "informative" when isTyping => CopilotStudioStreamRole.Informative,
            "streaming" when isTyping => CopilotStudioStreamRole.Delta,
            "final" when isMessage => CopilotStudioStreamRole.Final,
            _ => CopilotStudioStreamRole.None
        };

        if (role == CopilotStudioStreamRole.None)
        {
            return CopilotStudioStreamInfo.None;
        }

        // The first activity of a stream omits streamId and establishes it through its own id.
        var streamId = channelData.TryGetProperty("streamId", out var id) &&
                       id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : activity.Id;

        return new CopilotStudioStreamInfo(role, streamId);
    }
}

public static class TokenInspector
{
    /// <summary>Best-effort audience read. The token itself is validated by the JWT middleware.</summary>
    public static string? ReadAudience(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.CanReadToken(jwt)
                ? handler.ReadJwtToken(jwt).Audiences.FirstOrDefault()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Distinguishes an app-only token from a delegated one. A delegated token carries a
    /// user identity, so it can be exchanged on-behalf-of. An app-only token has no user
    /// behind it, and Entra rejects OBO for it with AADSTS7000114.
    /// </summary>
    public static bool IsAppOnly(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(jwt))
            {
                return false;
            }

            var token = handler.ReadJwtToken(jwt);
            var identityType = token.Claims
                .FirstOrDefault(claim => claim.Type == "idtyp")?.Value;
            if (identityType is not null)
            {
                return string.Equals(identityType, "app", StringComparison.OrdinalIgnoreCase);
            }

            // Older tokens omit idtyp. A delegated token always carries a scope claim; an
            // app-only token carries roles (or nothing) instead.
            var hasScope = token.Claims.Any(claim =>
                claim.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope");
            return !hasScope;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class OboTokenBroker
{
    private readonly CopilotStudioOptions _options;
    private readonly AuthenticationOptions _authenticationOptions;
    private readonly Lazy<IConfidentialClientApplication> _application;

    public OboTokenBroker(
        IOptions<CopilotStudioOptions> options,
        IOptions<AuthenticationOptions> authenticationOptions)
    {
        _options = options.Value;
        _authenticationOptions = authenticationOptions.Value;

        // Built once. A plain "??=" on a singleton races under concurrent first requests and
        // produces several applications, each with its own token cache.
        _application = new Lazy<IConfidentialClientApplication>(
            () => ConfidentialClientApplicationBuilder
                .Create(_options.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, _options.TenantId)
                .WithClientSecret(_options.ClientSecret)
                .Build(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<string> AcquireAsync(
        string scope,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity(
            "entra.obo.acquire_token",
            System.Diagnostics.ActivityKind.Client);
        traceActivity?.SetTag("auth.flow", "on_behalf_of");
        if (string.IsNullOrWhiteSpace(metadata.BearerToken))
        {
            throw new UnauthorizedAccessException(
                "A delegated bearer token is required for the Copilot Studio OBO flow.");
        }

        // Only exchange a token that was actually issued for this adapter.
        var expectedAudience = _authenticationOptions.Audience;
        if (!string.IsNullOrWhiteSpace(expectedAudience))
        {
            var audience = TokenInspector.ReadAudience(metadata.BearerToken);
            if (audience is not null && !AudienceMatches(audience, expectedAudience))
            {
                throw new UnauthorizedAccessException(
                    "The caller token was not issued for this adapter and will not be exchanged.");
            }
        }

        // An app-only caller (for example a Foundry project managed identity) has no user to act
        // on behalf of. Entra rejects OBO for such tokens with AADSTS7000114, so fall back to the
        // client-credentials flow and call the downstream service as this application instead.
        if (TokenInspector.IsAppOnly(metadata.BearerToken))
        {
            traceActivity?.SetTag("auth.flow", "client_credentials");
            var appOnlyResult = await _application.Value
                .AcquireTokenForClient([ResolveDefaultScope(scope)])
                .ExecuteAsync(cancellationToken);

            traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return appOnlyResult.AccessToken;
        }

        var result = await _application.Value
            .AcquireTokenOnBehalfOf([scope], new UserAssertion(metadata.BearerToken))
            .ExecuteAsync(cancellationToken);

        traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return result.AccessToken;
    }

    /// <summary>
    /// The client-credentials flow only accepts resource-wide "/.default" scopes, never the
    /// individual delegated permissions used by the on-behalf-of flow.
    /// </summary>
    public static string ResolveDefaultScope(string scope)
    {
        if (scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
        {
            return scope;
        }

        var separator = scope.LastIndexOf('/');
        return separator <= 0
            ? scope
            : $"{scope[..separator]}/.default";
    }

    private static bool AudienceMatches(string audience, string expected) =>
        string.Equals(audience, expected, StringComparison.OrdinalIgnoreCase)
        // Entra may present the audience as either "api://<id>" or the bare client id.
        || string.Equals($"api://{audience}", expected, StringComparison.OrdinalIgnoreCase)
        || string.Equals(audience, expected.Replace("api://", string.Empty), StringComparison.OrdinalIgnoreCase);
}
