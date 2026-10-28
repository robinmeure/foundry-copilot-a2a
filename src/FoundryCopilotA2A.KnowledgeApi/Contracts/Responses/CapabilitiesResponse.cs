using FoundryCopilotA2A.KnowledgeApi.Services.Skills;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;

/// <summary>Reports backend limits and enabled sources without exposing deployment credentials.</summary>
public sealed record CapabilitiesResponse(
    string Backend,
    int MaxFileBytes,
    string[] Extensions,
    string? DocumentModel,
    bool Graph,
    bool A2a = false,
    bool Kamervragen = false,
    IReadOnlyList<KnowledgeSourceDescriptor>? Sources = null);
