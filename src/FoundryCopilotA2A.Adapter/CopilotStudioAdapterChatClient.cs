using System.Runtime.CompilerServices;
using A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter;

public sealed class CopilotStudioAdapterChatClient(
    IAgentInvoker invoker,
    A2ARequestMetadataAccessor metadataAccessor,
    IdempotencyStore idempotencyStore,
    ILogger<CopilotStudioAdapterChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = AdapterTelemetry.StartActivity("a2a.adapter.get_response");
        var prompt = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new AdapterRequestException("An A2A request must contain a non-empty user message.");
        }

        var metadata = metadataAccessor.Current;
        activity?.SetTag("copilot_studio.agent.id", metadata.AgentId);
        activity?.SetTag("a2a.context.present", metadata.ContextId is not null);
        activity?.SetTag("a2a.message_id.present", metadata.MessageId is not null);

        if (metadata.MessageId is null)
        {
            // Replay protection is not optional: without a messageId a retry would repeat the
            // side effect on the Copilot Studio side.
            throw new AdapterRequestException(
                "An A2A request must carry a messageId so the delegated call can be made idempotent.");
        }

        // The key must include the caller identity AND the conversation, otherwise a caller who
        // reuses another caller's messageId is served that caller's cached response.
        var cacheKey = IdempotencyStore.BuildKey(
            metadata.UserId,
            metadata.AgentId,
            metadata.ContextId,
            metadata.MessageId);
        var payloadHash = metadata.PayloadHash ?? PayloadHash.ComputeFromPrompt(prompt);

        try
        {
            var result = await idempotencyStore.GetOrAddAsync(
                cacheKey,
                payloadHash,
                async token =>
                {
                    var invocation = await invoker.InvokeAsync(prompt, metadata, token);
                    if (CopilotStudioResponseClassifier.IsUnsupportedHarnessResponse(invocation.Text))
                    {
                        activity?.SetTag("copilot_studio.client.compatible", false);
                        logger.LogWarning(
                            "Agent {AgentId} returned the retired enhanced task completion response.",
                            metadata.AgentId);
                        return invocation with { Text = CopilotStudioResponseClassifier.Guidance };
                    }

                    return invocation;
                },
                cancellationToken);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return ToChatResponse(result, metadata);
        }
        catch (Exception exception) when (exception is not A2AException
                                          && exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
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
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static ChatResponse ToChatResponse(
        CopilotInvocationResult result,
        A2ARequestMetadata metadata) =>
        new(new ChatMessage(ChatRole.Assistant, result.Text))
        {
            ConversationId = metadata.ContextId ?? result.ConversationId,
            ResponseId = result.ResponseId,
            ModelId = metadata.AgentId
        };
}

/// <summary>Raised for conditions that should surface to the A2A caller as a defined error.</summary>
public sealed class AdapterRequestException : A2AException
{
    public AdapterRequestException(string message)
        : base(message, A2AErrorCode.InvalidParams)
    {
    }

    public AdapterRequestException(string message, Exception innerException)
        : base(message, innerException, A2AErrorCode.InternalError)
    {
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

/// <summary>Raised when a messageId is replayed with different content.</summary>
public sealed class IdempotencyConflictException(string message)
    : A2AException(message, A2AErrorCode.InvalidRequest);

public sealed class IdempotencyStore : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationToken _applicationStopping;
    private readonly Lock _gate = new();

    public IdempotencyStore(
        IOptions<AdapterOptions> options,
        IHostApplicationLifetime lifetime)
    {
        var value = options.Value;
        _ttl = TimeSpan.FromMinutes(value.IdempotencyTtlMinutes);
        _operationTimeout = TimeSpan.FromSeconds(value.RequestTimeoutSeconds);
        _applicationStopping = lifetime.ApplicationStopping;

        // A bounded cache with real expiration. The previous ConcurrentDictionary only evaluated
        // its TTL when the same key was read again, and A2A messageIds are unique per message,
        // so entries were never revisited and never released.
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = value.MaxCacheEntries,
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });
    }

    public static string BuildKey(
        string userId,
        string agentId,
        string? contextId,
        string messageId) =>
        string.Join('|', userId, agentId, contextId ?? "no-context", messageId);

    /// <summary>
    /// Non-mutating replay check used by the transport edge so a conflicting replay can be
    /// refused with a precise JSON-RPC error. <see cref="GetOrAddAsync"/> remains the atomic
    /// authority; this only improves the diagnostics of the common case.
    /// </summary>
    public bool IsConflictingReplay(string key, string payloadHash)
    {
        lock (_gate)
        {
            return _cache.TryGetValue<IdempotencyEntry>(key, out var existing)
                && existing is not null
                && !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal);
        }
    }

    public async Task<CopilotInvocationResult> GetOrAddAsync(
        string key,
        string payloadHash,
        Func<CancellationToken, Task<CopilotInvocationResult>> factory,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("a2a.idempotency.get_or_add");
        IdempotencyEntry entry;
        var cacheHit = false;

        lock (_gate)
        {
            if (_cache.TryGetValue<IdempotencyEntry>(key, out var existing) && existing is not null)
            {
                cacheHit = true;
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    activity?.SetTag("a2a.idempotency.conflict", true);
                    throw new IdempotencyConflictException(
                        "This messageId has already been used with different content. " +
                        "Reusing a messageId for a different request is not permitted.");
                }

                entry = existing;
            }
            else
            {
                entry = new IdempotencyEntry(
                    payloadHash,
                    new Lazy<Task<CopilotInvocationResult>>(
                        () => RunAsync(factory),
                        LazyThreadSafetyMode.ExecutionAndPublication));

                _cache.Set(key, entry, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _ttl,
                    Size = 1
                });
            }
        }

        activity?.SetTag("a2a.idempotency.cache_hit", cacheHit);

        try
        {
            // Each caller waits with its OWN token. The shared work is never bound to whichever
            // caller happened to create the entry, so one client disconnecting cannot cancel
            // another client's in-flight request.
            var response = await entry.Response.Value.WaitAsync(cancellationToken);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This caller went away. Deliberately keep the entry: the delegated call may already
            // have committed a side effect, and a retry must not repeat it.
            throw;
        }
        catch
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            Evict(key, entry);
            throw;
        }
    }

    private async Task<CopilotInvocationResult> RunAsync(
        Func<CancellationToken, Task<CopilotInvocationResult>> factory)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
        timeoutSource.CancelAfter(_operationTimeout);
        return await factory(timeoutSource.Token);
    }

    private void Evict(string key, IdempotencyEntry entry)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue<IdempotencyEntry>(key, out var current) &&
                ReferenceEquals(current, entry))
            {
                _cache.Remove(key);
            }
        }
    }

    public void Dispose() => _cache.Dispose();

    private sealed record IdempotencyEntry(
        string PayloadHash,
        Lazy<Task<CopilotInvocationResult>> Response);
}
