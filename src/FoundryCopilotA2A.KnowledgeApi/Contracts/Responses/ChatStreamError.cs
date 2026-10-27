namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;

/// <summary>The final server-sent event payload used when a stream fails after headers were sent.</summary>
public sealed record ChatStreamError(
    int Status,
    string Code,
    string Message,
    string? Challenge = null,
    string? RetryAfter = null);
