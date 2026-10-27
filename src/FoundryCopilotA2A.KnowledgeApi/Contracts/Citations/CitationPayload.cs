namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;

/// <summary>Versioned citation envelope shared by normal chat responses and the final stream event.</summary>
public sealed record CitationPayload(string SchemaVersion, CitationSource[] Sources, CitationReference[] Citations);
