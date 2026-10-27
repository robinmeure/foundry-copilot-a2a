namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;

/// <summary>Identifies a source and its originating agent independently of citation locations.</summary>
public sealed record CitationSource(string Id, string Title, string? Url, OriginatingAgentInfo OriginatingAgent);
