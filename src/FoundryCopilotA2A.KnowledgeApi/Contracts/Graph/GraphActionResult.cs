namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

public sealed record GraphActionResult(
    string Id,
    string Status,
    string Message,
    string? WebLink = null);
