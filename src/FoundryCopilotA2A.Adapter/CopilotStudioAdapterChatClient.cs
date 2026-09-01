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
            yield return new ChatResponseUpdate(ChatRole.Assistant, update.Text)
            {
                ConversationId = metadata.ContextId ?? update.ConversationId,
                ResponseId = update.ResponseId,
                MessageId = $"response-{metadata.MessageId}",
                ModelId = metadata.AgentId
            };
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
        var prompt = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new AdapterRequestException("An A2A request must contain a non-empty user message.");
        }

        var metadata = metadataAccessor.Current;
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
        await foreach (var update in invoker.StreamAsync(prompt, metadata, cancellationToken))
        {
            if (CopilotStudioResponseClassifier.IsUnsupportedHarnessResponse(update.Text))
            {
                activity?.SetTag("copilot_studio.client.compatible", false);
                logger.LogWarning(
                    "Agent {AgentId} returned the retired enhanced task completion response.",
                    metadata.AgentId);
                yield return update with { Text = CopilotStudioResponseClassifier.Guidance };
                continue;
            }

            yield return update;
        }
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
        Func<CancellationToken, IAsyncEnumerable<CopilotInvocationUpdate>> factory,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("a2a.idempotency.get_or_add");
        var (entry, cacheHit) = GetOrCreateEntry(key, payloadHash, factory, activity);
        activity?.SetTag("a2a.idempotency.cache_hit", cacheHit);

        try
        {
            // Each caller waits with its OWN token. The shared work is never bound to whichever
            // caller happened to create the entry, so one client disconnecting cannot cancel
            // another client's in-flight request.
            var response = await entry.Completion.WaitAsync(cancellationToken);
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

    public IAsyncEnumerable<CopilotInvocationUpdate> GetOrAddStreaming(
        string key,
        string payloadHash,
        Func<CancellationToken, IAsyncEnumerable<CopilotInvocationUpdate>> factory,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("a2a.idempotency.get_or_add_stream");
        var (entry, cacheHit) = GetOrCreateEntry(key, payloadHash, factory, activity);
        activity?.SetTag("a2a.idempotency.cache_hit", cacheHit);
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return entry.Subscribe(cancellationToken);
    }

    private (IdempotencyEntry Entry, bool CacheHit) GetOrCreateEntry(
        string key,
        string payloadHash,
        Func<CancellationToken, IAsyncEnumerable<CopilotInvocationUpdate>> factory,
        System.Diagnostics.Activity? activity)
    {
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
                    factory,
                    _operationTimeout,
                    _applicationStopping);
                _cache.Set(key, entry, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _ttl,
                    Size = 1
                });
            }
        }

        entry.Start();
        _ = entry.Completion.ContinueWith(
            _ => Evict(key, entry),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return (entry, cacheHit);
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

    private sealed class IdempotencyEntry
    {
        private readonly Func<CancellationToken, IAsyncEnumerable<CopilotInvocationUpdate>> _factory;
        private readonly TimeSpan _operationTimeout;
        private readonly CancellationToken _applicationStopping;
        private readonly Lock _gate = new();
        private readonly List<CopilotInvocationUpdate> _updates = [];
        private readonly HashSet<Channel<CopilotInvocationUpdate>> _subscribers = [];
        private readonly TaskCompletionSource<CopilotInvocationResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private bool _finished;

        public IdempotencyEntry(
            string payloadHash,
            Func<CancellationToken, IAsyncEnumerable<CopilotInvocationUpdate>> factory,
            TimeSpan operationTimeout,
            CancellationToken applicationStopping)
        {
            PayloadHash = payloadHash;
            _factory = factory;
            _operationTimeout = operationTimeout;
            _applicationStopping = applicationStopping;
        }

        public string PayloadHash { get; }

        public Task<CopilotInvocationResult> Completion => _completion.Task;

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = ProduceAsync();
            }
        }

        public async IAsyncEnumerable<CopilotInvocationUpdate> Subscribe(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Channel<CopilotInvocationUpdate>? channel = null;
            CopilotInvocationUpdate[] replay;
            lock (_gate)
            {
                replay = [.. _updates];
                if (!_finished)
                {
                    channel = Channel.CreateUnbounded<CopilotInvocationUpdate>(
                        new UnboundedChannelOptions
                        {
                            SingleReader = true,
                            SingleWriter = true,
                            AllowSynchronousContinuations = false
                        });
                    _subscribers.Add(channel);
                }
            }

            foreach (var update in replay)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }

            if (channel is null)
            {
                await Completion.WaitAsync(cancellationToken);
                yield break;
            }

            try
            {
                await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return update;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _subscribers.Remove(channel);
                }
            }
        }

        private async Task ProduceAsync()
        {
            using var timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
            timeoutSource.CancelAfter(_operationTimeout);
            var text = new StringBuilder();
            string? conversationId = null;
            string? responseId = null;

            try
            {
                await foreach (var update in _factory(timeoutSource.Token)
                    .WithCancellation(timeoutSource.Token))
                {
                    if (string.IsNullOrEmpty(update.Text))
                    {
                        continue;
                    }

                    text.Append(update.Text);
                    conversationId = update.ConversationId ?? conversationId;
                    responseId = update.ResponseId ?? responseId;
                    Publish(update);
                }

                if (text.Length == 0)
                {
                    throw new InvalidOperationException(
                        "The delegated agent returned no text response.");
                }

                Complete(new CopilotInvocationResult(text.ToString(), conversationId, responseId));
            }
            catch (Exception exception)
            {
                Complete(exception);
            }
        }

        private void Publish(CopilotInvocationUpdate update)
        {
            lock (_gate)
            {
                _updates.Add(update);
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(update);
                }
            }
        }

        private void Complete(CopilotInvocationResult result)
        {
            lock (_gate)
            {
                _finished = true;
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }
            }

            _completion.TrySetResult(result);
        }

        private void Complete(Exception exception)
        {
            lock (_gate)
            {
                _finished = true;
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryComplete(exception);
                }
            }

            _completion.TrySetException(exception);
        }
    }
}
