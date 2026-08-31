using System.IdentityModel.Tokens.Jwt;
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

public interface ICopilotStudioInvoker
{
    Task<CopilotInvocationResult> InvokeAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed class MockCopilotStudioInvoker : ICopilotStudioInvoker
{
    private int _invocationCount;

    public int InvocationCount => _invocationCount;

    public Task<CopilotInvocationResult> InvokeAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.mock.invoke");
        traceActivity?.SetTag("copilot_studio.backend", "mock");
        traceActivity?.SetTag("a2a.history.turns", metadata.History.Count);
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = Interlocked.Increment(ref _invocationCount);
        var conversationId = metadata.ContextId ?? $"mock-{Guid.NewGuid():N}";
        var historySuffix = metadata.History.Count == 0
            ? string.Empty
            : $" | context: {metadata.History.Count} earlier turns";

        return Task.FromResult(new CopilotInvocationResult(
            $"mock-copilot-studio[{invocation}]: {prompt}{historySuffix}",
            conversationId,
            $"mock-response-{invocation}"));
    }
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

    public async Task<CopilotInvocationResult> InvokeAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken)
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
        try
        {
            return await InvokeCoreAsync(
                agent, prompt, metadata, traceActivity, cancellationToken);
        }
        catch (Exception exception)
        {
            GenAiTelemetry.RecordFailure(genAiActivity, exception);
            throw;
        }
    }

    private async Task<CopilotInvocationResult> InvokeCoreAsync(
        CopilotStudioAgentRuntime agent,
        string prompt,
        A2ARequestMetadata metadata,
        System.Diagnostics.Activity? traceActivity,
        CancellationToken cancellationToken)
    {        var isAppOnlyCaller = TokenInspector.IsAppOnly(metadata.BearerToken ?? string.Empty);
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

        try
        {
            return await ExecuteTurnAsync(
                client,
                cachedConversationId is null ? promptWithHistory : prompt,
                metadata,
                cachedConversationId,
                cancellationToken);
        }
        // An app-only caller needs the CopilotStudio.Copilots.Invoke *application* role, which is
        // separate from the delegated permission the user flow relies on. Without it Copilot
        // Studio answers 403, which is otherwise indistinguishable from an agent-level denial.
        // The Copilot Studio client does not always populate StatusCode, so match the message too.
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
        catch (Exception exception) when (cachedConversationId is not null
                                          && exception is not OperationCanceledException)
        {
            traceActivity?.AddEvent(new System.Diagnostics.ActivityEvent(
                "copilot_studio.conversation.restart"));
            traceActivity?.SetTag("copilot_studio.conversation.restarted", true);
            // Copilot Studio expires conversations on its own schedule, so a cached id can be
            // dead well before our local TTL lapses. Drop it and start a fresh conversation once.
            _logger.LogWarning(
                exception,
                "Cached Copilot Studio conversation was rejected; restarting the conversation once.");
            _conversationStore.Remove(metadata.UserId, metadata.AgentId, metadata.ContextId);
            return await ExecuteTurnAsync(client, promptWithHistory, metadata, null, cancellationToken);
        }
    }

    private async Task<CopilotInvocationResult> ExecuteTurnAsync(
        CopilotClient client,
        string prompt,
        A2ARequestMetadata metadata,
        string? conversationId,
        CancellationToken cancellationToken)
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
            throw new InvalidOperationException(
                "Copilot Studio did not return a conversation id when starting the conversation.");
        }

        // The agent may demand a signed-in user before it will answer anything.
        if (oauthCard is not null)
        {
            traceActivity?.SetTag("copilot_studio.token_exchange.required", true);
            await PerformTokenExchangeAsync(
                client, conversationId, oauthCard.Value, metadata, cancellationToken);
        }

        var (text, responseId, cardDuringTurn) = await CollectAnswerAsync(
            client.AskQuestionAsync(prompt, conversationId, cancellationToken),
            cancellationToken);

        // The card can also arrive in response to the user's message rather than at startup.
        if (text.Length == 0 && cardDuringTurn is not null)
        {
            traceActivity?.SetTag("copilot_studio.token_exchange.required", true);
            await PerformTokenExchangeAsync(
                client, conversationId, cardDuringTurn.Value, metadata, cancellationToken);

            var continuation = await CollectAnswerAsync(
                client.ExecuteAsync(
                    conversationId,
                    CreateMessageActivity(prompt),
                    cancellationToken),
                cancellationToken);
            text = continuation.Text;
            responseId = continuation.ResponseId ?? responseId;
        }

        if (text.Length == 0)
        {
            throw new InvalidOperationException(
                "Copilot Studio returned no message activity for the delegated request.");
        }

        _conversationStore.Set(
            metadata.UserId,
            metadata.AgentId,
            metadata.ContextId,
            conversationId);
        traceActivity?.SetTag("copilot_studio.response.present", true);
        traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return new CopilotInvocationResult(text, conversationId, responseId);
    }

    private static async Task<(string Text, string? ResponseId, OAuthCardInfo? Card)> CollectAnswerAsync(
        IAsyncEnumerable<IActivity> activities,
        CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity("copilot_studio.collect_answer");
        var builder = new StringBuilder();
        string? responseId = null;
        OAuthCardInfo? card = null;

        await foreach (var activity in activities.WithCancellation(cancellationToken))
        {
            if (activity is null)
            {
                continue;
            }

            responseId = activity.Id ?? responseId;
            card ??= ExtractOAuthCard(activity);

            if (string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(activity.Text))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(activity.Text);
            }
        }

        traceActivity?.SetTag("copilot_studio.response.present", builder.Length > 0);
        traceActivity?.SetTag("copilot_studio.oauth_card.present", card is not null);
        traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return (builder.ToString(), responseId, card);
    }

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
