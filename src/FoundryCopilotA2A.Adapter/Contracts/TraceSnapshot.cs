namespace FoundryCopilotA2A.Adapter;

public sealed record TraceSnapshot(
    string TraceId,
    bool Complete,
    double DurationMs,
    IReadOnlyList<TraceSpanSnapshot> Spans,
    bool Truncated = false);

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
