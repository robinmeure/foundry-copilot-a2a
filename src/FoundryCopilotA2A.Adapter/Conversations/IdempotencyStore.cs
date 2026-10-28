using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using A2A;
using Microsoft.Extensions.Caching.Memory;

namespace FoundryCopilotA2A.Adapter;

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
        IConfiguration configuration,
        IHostApplicationLifetime lifetime)
    {
        _ttl = TimeSpan.FromMinutes(configuration.GetValue("Adapter:IdempotencyTtlMinutes", 15));
        _operationTimeout = TimeSpan.FromSeconds(configuration.GetValue("Adapter:RequestTimeoutSeconds", 60));
        _applicationStopping = lifetime.ApplicationStopping;

        // Unique message IDs may never be read again, so expiration must not depend on a replay.
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = configuration.GetValue<long>("Adapter:MaxCacheEntries", 10_000),
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
        IdempotencyEntry entry;
        // The span covers only the lookup, like the streaming path; the invocation records its own
        // duration and outcome beside it.
        using (var activity = AdapterTelemetry.StartActivity("a2a.idempotency.get_or_add"))
        {
            (entry, var cacheHit) = GetOrCreateEntry(key, payloadHash, factory, activity);
            activity?.SetTag("a2a.idempotency.cache_hit", cacheHit);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        }

        try
        {
            // Each caller waits with its OWN token. The shared work is never bound to whichever
            // caller happened to create the entry, so one client disconnecting cannot cancel
            // another client's in-flight request.
            return await entry.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This caller went away. Deliberately keep the entry: the delegated call may already
            // have committed a side effect, and a retry must not repeat it.
            throw;
        }
        catch
        {
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

        // The shared invocation outlives this short idempotency span, so it starts under the
        // caller's span and appears beside the idempotency step rather than nested inside it.
        var current = System.Diagnostics.Activity.Current;
        System.Diagnostics.Activity.Current = activity is not null && ReferenceEquals(current, activity)
            ? activity.Parent
            : current;
        try
        {
            entry.Start();
        }
        finally
        {
            System.Diagnostics.Activity.Current = current;
        }

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
        private Func<CancellationToken, IAsyncEnumerable<CopilotInvocationUpdate>>? _factory;
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
            var answer = new InvocationAnswer();

            try
            {
                var factory = _factory ?? throw new InvalidOperationException("The invocation has already completed.");
                await foreach (var update in factory(timeoutSource.Token)
                    .WithCancellation(timeoutSource.Token))
                {
                    if (!HasPublishableContent(update))
                    {
                        continue;
                    }

                    answer.Add(update);
                    Publish(update);
                }

                Complete(answer.ToResult());
            }
            catch (Exception exception)
            {
                Complete(exception);
            }
            finally
            {
                _factory = null;
            }
        }

        private static bool HasPublishableContent(CopilotInvocationUpdate update)
        {
            if (update.PlanUpdate is not null || !string.IsNullOrEmpty(update.Text))
            {
                return true;
            }

            if (update.IsInformative)
            {
                return false;
            }

            return update.Citations is { Sources.Count: > 0 } || update.Cards is { Cards.Count: > 0 };
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

        // Only the producer updates this answer. Subscriber synchronization belongs to IdempotencyEntry.
        private sealed class InvocationAnswer
        {
            private readonly StringBuilder _text = new();
            private string? _conversationId;
            private string? _responseId;
            private string? _conversationHandle;
            private AgentResponder? _responder;
            private CitationBundle? _citations;
            private AdaptiveCardBundle? _cards;

            public void Add(CopilotInvocationUpdate update)
            {
                if (!update.IsInformative && update.PlanUpdate is null)
                {
                    if (update.ReplacesAnswer)
                    {
                        _text.Clear();
                        _citations = null;
                        _cards = null;
                    }

                    _text.Append(update.Text);
                    _responder = update.Responder;
                    _citations = AgentCitations.Merge(_citations, update.Citations);
                    _cards = A2AResponseText.MergeCards(_cards, update.Cards);
                }

                _conversationId = update.ConversationId ?? _conversationId;
                _responseId = update.ResponseId ?? _responseId;
                _conversationHandle = update.ConversationHandle ?? _conversationHandle;
            }

            public CopilotInvocationResult ToResult()
            {
                if (_text.Length == 0)
                {
                    throw new InvalidOperationException("The delegated agent returned no text response.");
                }

                return new CopilotInvocationResult(_text.ToString(), _conversationId, _responseId)
                {
                    Responder = _responder,
                    Citations = _citations,
                    Cards = _cards,
                    ConversationHandle = _conversationHandle
                };
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
