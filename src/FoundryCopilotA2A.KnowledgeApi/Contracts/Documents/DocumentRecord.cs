namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;

/// <summary>Metadata for an uploaded or imported knowledge document.</summary>
public sealed record DocumentRecord(
    string Id,
    string Scope,
    string Name,
    DateTimeOffset ExpiresAt,
    int Chunks,
    string? DriveId = null,
    string? ItemId = null,
    string? ETag = null,
    string? WebUrl = null,
    string Status = "Ready",
    string? UserId = null)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
