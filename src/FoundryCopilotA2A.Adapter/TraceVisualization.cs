using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace FoundryCopilotA2A.Adapter;

public sealed record TraceSnapshot(
    string TraceId,
    bool Complete,
    double DurationMs,
    IReadOnlyList<TraceSpanSnapshot> Spans);

public sealed record TraceSpanSnapshot(
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    string Source,
    string? Destination,
    DateTimeOffset StartedAt,
    double DurationMs,
    string Status,
    IReadOnlyDictionary<string, string> Attributes,
    TraceHttpExchange? Http);

public sealed record TraceHttpExchange(
    TraceHttpRequest Request,
    TraceHttpResponse? Response,
    string? Error);

public sealed record TraceHttpRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

public sealed record TraceHttpResponse(int Status, string? Body);

public sealed class SanitizedTraceStore : IDisposable
{
    private static readonly TimeSpan TraceTtl = TimeSpan.FromMinutes(15);
    private readonly MemoryCache _cache;

    public SanitizedTraceStore(IOptions<AdapterOptions> options)
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = options.Value.MaxCacheEntries,
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });
    }

    public void Register(ActivityTraceId traceId, string ownerId, string agentId)
    {
        _cache.Set(
            traceId.ToHexString(),
            new TraceBuffer(ownerId, agentId),
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TraceTtl,
                Size = 1
            });
    }

    public void Record(Activity activity)
    {
        if (!TryGetBuffer(activity.TraceId, out var buffer))
        {
            return;
        }

        buffer.Record(activity);
        if (activity.Kind == ActivityKind.Server &&
            activity.GetTagItem("a2a.runtime") is true)
        {
            buffer.MarkComplete();
        }
    }

    public void RecordHttpExchange(
        Activity activity,
        TraceHttpRequest request,
        TraceHttpResponse? response,
        string? error)
    {
        if (TryGetBuffer(activity.TraceId, out var buffer))
        {
            buffer.RecordHttpExchange(activity, request, response, error);
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
        private int _complete;

        public string OwnerId { get; } = ownerId;

        public string AgentId { get; } = agentId;

        public void Record(Activity activity)
        {
            var span = _spans.GetOrAdd(
                activity.SpanId.ToHexString(),
                _ => CapturedSpan.FromActivity(activity, AgentId));
            span.UpdateFromActivity(activity, AgentId);
        }

        public void RecordHttpExchange(
            Activity activity,
            TraceHttpRequest request,
            TraceHttpResponse? response,
            string? error)
        {
            var span = _spans.GetOrAdd(
                activity.SpanId.ToHexString(),
                _ => CapturedSpan.FromActivity(activity, AgentId));
            span.SetHttp(new TraceHttpExchange(request, response, error));
        }

        public void MarkComplete() => Interlocked.Exchange(ref _complete, 1);

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
                Volatile.Read(ref _complete) == 1,
                Math.Round(duration, 1),
                spans);
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

public sealed class CopilotStudioTraceHandler(
    SanitizedTraceStore traceStore,
    A2ARequestMetadataAccessor metadataAccessor)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity(
            "copilot_studio.http.exchange",
            ActivityKind.Client);
        var agentId = metadataAccessor.Current.AgentId;
        activity?.SetTag("copilot_studio.agent.id", agentId);
        activity?.SetTag("http.request.method", request.Method.Method);

        var capturedRequest = new TraceHttpRequest(
            request.Method.Method,
            TraceSanitizer.SanitizeUrl(request.RequestUri, agentId),
            TraceSanitizer.SanitizeHeaders(request),
            await TraceSanitizer.ReadSanitizedRequestBodyAsync(request, cancellationToken));

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var capturedResponse = new TraceHttpResponse(
                (int)response.StatusCode,
                "Response content is consumed as Copilot Studio activities and is not buffered by tracing.");
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            activity?.SetStatus(
                response.IsSuccessStatusCode ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            if (activity is not null)
            {
                traceStore.RecordHttpExchange(activity, capturedRequest, capturedResponse, null);
            }

            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            if (activity is not null)
            {
                traceStore.RecordHttpExchange(
                    activity,
                    capturedRequest,
                    null,
                    exception.GetType().Name);
            }

            throw;
        }
    }
}

internal static class TraceSanitizer
{
    private const int MaximumBodyCharacters = 12_000;
    private static readonly HashSet<string> AllowedAttributes =
    [
        "a2a.context.present",
        "a2a.chain.enabled",
        "a2a.chain.target_agent",
        "a2a.idempotency.cache_hit",
        "a2a.message_id.present",
        "a2a.outcome",
        "auth.flow",
        "copilot_studio.agent.id",
        "copilot_studio.backend",
        "copilot_studio.client.compatible",
        "copilot_studio.conversation.restarted",
        "copilot_studio.conversation.reused",
        "copilot_studio.oauth_card.present",
        "copilot_studio.response.present",
        "copilot_studio.token_exchange.required",
        "error.type",
        "foundry.agent.id",
        "http.request.method",
        "http.response.status_code",
        "network.protocol.version",
        "rpc.method",
        "rpc.system",
        "url.path"
    ];

    public static IReadOnlyDictionary<string, string> SanitizeAttributes(
        Activity activity,
        string agentId)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Value is null)
            {
                continue;
            }

            if (AllowedAttributes.Contains(tag.Key))
            {
                result[tag.Key] = tag.Value.ToString() ?? string.Empty;
            }
            else if (tag.Key == "url.full")
            {
                result[tag.Key] = Uri.TryCreate(tag.Value.ToString(), UriKind.Absolute, out var uri)
                    ? SanitizeUrl(uri, agentId)
                    : "[redacted URL]";
            }
            else if (tag.Key == "server.address")
            {
                result[tag.Key] = ResolveDestination(activity) ?? "External service";
            }
        }

        return result;
    }

    public static string? ResolveDestination(Activity activity)
    {
        var address = activity.GetTagItem("server.address")?.ToString();
        if (string.IsNullOrWhiteSpace(address))
        {
            return activity.Kind == ActivityKind.Server ? "A2A adapter" : null;
        }

        if (address.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Entra ID";
        }

        if (address.Contains("environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Copilot Studio API";
        }

        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            address.Equals("127.0.0.1", StringComparison.Ordinal))
        {
            return "A2A adapter";
        }

        return "External service";
    }

    public static string SanitizeName(string name)
    {
        if (name.Contains("environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP connection to Copilot Studio API";
        }

        if (name.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP connection to Microsoft Entra ID";
        }

        return name;
    }

    public static string SanitizeUrl(Uri? uri, string agentId)
    {
        if (uri is null)
        {
            return "[unknown URL]";
        }

        if (uri.Host.Contains("environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            var conversationSuffix = uri.AbsolutePath.Contains(
                "/conversations/",
                StringComparison.OrdinalIgnoreCase)
                ? "/{conversation}"
                : string.Empty;
            return $"https://[power-platform-environment]/copilotstudio/agents/{agentId}/conversations{conversationSuffix}";
        }

        if (uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = uri.AbsolutePath.Contains("/token", StringComparison.OrdinalIgnoreCase)
                ? "/oauth2/v2.0/token"
                : "/identity-metadata";
            return $"https://login.microsoftonline.com/[tenant]{suffix}";
        }

        if (uri.IsLoopback)
        {
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }

        return $"{uri.Scheme}://[external-service]{uri.AbsolutePath}";
    }

    public static IReadOnlyDictionary<string, string> SanitizeHeaders(HttpRequestMessage request)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers.Authorization is not null)
        {
            headers["Authorization"] = $"{request.Headers.Authorization.Scheme} [redacted]";
        }

        if (request.Headers.Accept.Count > 0)
        {
            headers["Accept"] = string.Join(", ", request.Headers.Accept);
        }

        if (request.Content?.Headers.ContentType is not null)
        {
            headers["Content-Type"] = request.Content.Headers.ContentType.ToString();
        }

        return headers;
    }

    public static async Task<string?> ReadSanitizedRequestBodyAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return null;
        }

        var raw = await request.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(raw);
            SanitizeNode(node);
            return Limit(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
        }
        catch (JsonException)
        {
            return "[non-JSON request body omitted]";
        }
    }

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (IsSensitiveProperty(property.Key))
                {
                    jsonObject[property.Key] = "[redacted]";
                }
                else
                {
                    SanitizeNode(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                SanitizeNode(item);
            }
        }
    }

    private static bool IsSensitiveProperty(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("connectionString", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Url", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Uri", StringComparison.OrdinalIgnoreCase);

    private static string Limit(string value) =>
        value.Length <= MaximumBodyCharacters
            ? value
            : $"{value[..MaximumBodyCharacters]}\n[truncated]";
}
