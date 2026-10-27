namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

public sealed record GraphActionView(
    string Id,
    string Kind,
    string Title,
    string Summary,
    GraphActionDetail[] Details,
    DateTimeOffset ExpiresAt);
