using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using OpenTelemetry;

namespace FoundryCopilotA2A.Adapter;

public sealed class SanitizedTraceStore : IDisposable
{
    private static readonly TimeSpan TraceTtl = TimeSpan.FromMinutes(15);
    internal const int MaxRequestsPerTrace = 128;
    internal const int MaxSpansPerTrace = 1024;
    private const string OwnerProperty = "adapter.trace.owner";
    private const string AgentProperty = "adapter.trace.agent";
    private const string BufferProperty = "adapter.trace.buffer";
    private readonly MemoryCache _cache;
    private readonly Lock _gate = new();

    public SanitizedTraceStore(IConfiguration configuration)
        : this(configuration, null)
    {
    }

    internal SanitizedTraceStore(IConfiguration configuration, ISystemClock? clock)
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            Clock = clock,
            SizeLimit = configuration.GetValue<long>("Adapter:MaxCacheEntries", 10_000),
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });
    }

    public bool Register(Activity activity, string ownerId, string agentId)
    {
        activity.SetCustomProperty(BufferProperty, null);
        activity.SetCustomProperty(OwnerProperty, ownerId);
        activity.SetCustomProperty(AgentProperty, agentId);
        lock (_gate)
        {
            if (TryGetBuffer(activity.TraceId, out var existing))
            {
                if (!string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    return false;
                }
                if (!existing.BeginRequest(activity.SpanId))
                {
                    return false;
                }
                activity.SetCustomProperty(BufferProperty, existing);
                existing.Record(activity, agentId);
                return true;
            }

            var buffer = new TraceBuffer(ownerId, agentId);
            buffer.BeginRequest(activity.SpanId);
            _cache.Set(
                activity.TraceId.ToHexString(),
                buffer,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TraceTtl, Size = 1 });
            activity.SetCustomProperty(BufferProperty, buffer);
            buffer.Record(activity, agentId);
            return true;
        }
    }

    public void Record(Activity activity)
    {
        if (!TryGetBuffer(activity, out var buffer, out var agentId))
        {
            return;
        }

        buffer.Record(activity, agentId);
        if (activity.Kind == ActivityKind.Server &&
            activity.GetTagItem("a2a.runtime") is true)
        {
            buffer.MarkComplete(activity.SpanId);
        }
    }

    public void RecordHttpExchange(
        Activity activity,
        TraceHttpRequest request,
        TraceHttpResponse? response,
        string? error)
    {
        if (TryGetBuffer(activity, out var buffer, out var agentId))
        {
            buffer.RecordHttpExchange(activity, agentId, request, response, error);
        }
    }

    public bool TryGet(string traceId, string ownerId, out TraceSnapshot? snapshot)
    {
        snapshot = null;
        if (!_cache.TryGetValue(traceId, out TraceBuffer? buffer) ||
            buffer is null ||
            !string.Equals(buffer.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return false;
        }

        snapshot = buffer.CreateSnapshot(traceId);
        return true;
    }

    public string? GetAgentId(ActivityTraceId traceId) =>
        TryGetBuffer(traceId, out var buffer) ? buffer.AgentId : null;

    public void Dispose() => _cache.Dispose();

    private bool TryGetBuffer(Activity activity, out TraceBuffer buffer, out string agentId)
    {
        Activity? request = activity;
        while (request is not null && request.GetCustomProperty(OwnerProperty) is not string)
        {
            request = request.Parent;
        }
        agentId = request?.GetCustomProperty(AgentProperty) as string ?? string.Empty;
        return TryGetBuffer(activity.TraceId, out buffer) &&
               ReferenceEquals(request?.GetCustomProperty(BufferProperty), buffer) &&
               string.Equals(
                   request?.GetCustomProperty(OwnerProperty) as string, buffer.OwnerId, StringComparison.Ordinal);
    }

    private bool TryGetBuffer(ActivityTraceId traceId, out TraceBuffer buffer)
    {
        if (_cache.TryGetValue(traceId.ToHexString(), out TraceBuffer? value) &&
            value is not null)
        {
            buffer = value;
            return true;
        }

        buffer = null!;
        return false;
    }

    private sealed class TraceBuffer(string ownerId, string agentId)
    {
        private readonly ConcurrentDictionary<string, CapturedSpan> _spans = new();
        private readonly ConcurrentDictionary<ActivitySpanId, bool> _requests = new();
        private readonly Lock _gate = new();
        private volatile bool _truncated;

        public string OwnerId { get; } = ownerId;

        public string AgentId { get; } = agentId;

        public void Record(Activity activity, string currentAgentId)
        {
            var span = GetOrCreateSpan(activity, currentAgentId);
            span?.UpdateFromActivity(activity, currentAgentId);
        }

        public void RecordHttpExchange(
            Activity activity,
            string currentAgentId,
            TraceHttpRequest request,
            TraceHttpResponse? response,
            string? error)
        {
            var span = GetOrCreateSpan(activity, currentAgentId);
            span?.SetHttp(new TraceHttpExchange(request, response, error));
        }

        private CapturedSpan? GetOrCreateSpan(Activity activity, string currentAgentId)
        {
            var spanId = activity.SpanId.ToHexString();
            lock (_gate)
            {
                if (_spans.TryGetValue(spanId, out var existing))
                {
                    return existing;
                }
                if (_spans.Count >= MaxSpansPerTrace)
                {
                    _truncated = true;
                    return null;
                }

                var span = CapturedSpan.FromActivity(activity, currentAgentId);
                _spans[spanId] = span;
                return span;
            }
        }

        public bool BeginRequest(ActivitySpanId spanId)
        {
            lock (_gate)
            {
                if (_requests.ContainsKey(spanId))
                {
                    return true;
                }
                if (_requests.Count >= MaxRequestsPerTrace)
                {
                    _truncated = true;
                    return false;
                }
                return _requests.TryAdd(spanId, false);
            }
        }

        public void MarkComplete(ActivitySpanId spanId) => _requests.TryUpdate(spanId, true, false);

        public TraceSnapshot CreateSnapshot(string traceId)
        {
            var spans = _spans.Values
                .Select(span => span.CreateSnapshot())
                .OrderBy(span => span.StartedAt)
                .ToArray();
            var startedAt = spans.FirstOrDefault()?.StartedAt;
            var endedAt = spans.Length == 0
                ? startedAt
                : spans.Max(span => span.StartedAt.AddMilliseconds(span.DurationMs));
            var duration = startedAt is null || endedAt is null
                ? 0
                : Math.Max(0, (endedAt.Value - startedAt.Value).TotalMilliseconds);

            return new TraceSnapshot(
                traceId,
                !_requests.IsEmpty && _requests.Values.All(complete => complete),
                Math.Round(duration, 1),
                spans,
                _truncated);
        }
    }

    private sealed class CapturedSpan
    {
        private readonly object _gate = new();
        private TraceSpanSnapshot _snapshot;

        private CapturedSpan(TraceSpanSnapshot snapshot) => _snapshot = snapshot;

        public static CapturedSpan FromActivity(Activity activity, string agentId) =>
            new(CreateSanitizedSnapshot(activity, agentId, null));

        public void UpdateFromActivity(Activity activity, string agentId)
        {
            lock (_gate)
            {
                _snapshot = CreateSanitizedSnapshot(activity, agentId, _snapshot.Http);
            }
        }

        public void SetHttp(TraceHttpExchange exchange)
        {
            lock (_gate)
            {
                _snapshot = _snapshot with { Http = exchange };
            }
        }

        public TraceSpanSnapshot CreateSnapshot()
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    private static TraceSpanSnapshot CreateSanitizedSnapshot(
        Activity activity,
        string agentId,
        TraceHttpExchange? exchange)
    {
        var attributes = TraceSanitizer.SanitizeAttributes(activity, agentId);
        return new TraceSpanSnapshot(
            activity.SpanId.ToHexString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            TraceSanitizer.SanitizeName(activity.DisplayName),
            activity.Kind.ToString(),
            activity.Source.Name,
            TraceSanitizer.ResolveDestination(activity),
            activity.StartTimeUtc,
            Math.Round(activity.Duration.TotalMilliseconds, 1),
            activity.Status.ToString(),
            attributes,
            exchange);
    }
}

public sealed class SanitizedTraceProcessor(SanitizedTraceStore store)
    : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data) => store.Record(data);
}
