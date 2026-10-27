namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;

/// <summary>Maps an answer marker to a source and its human-readable evidence location.</summary>
public sealed record CitationReference(string SourceId, string Marker, string Locator);
