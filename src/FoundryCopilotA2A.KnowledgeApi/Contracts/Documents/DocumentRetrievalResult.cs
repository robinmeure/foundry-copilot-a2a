namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;

/// <summary>Carries retrieved passages, per-document coverage and optionally synthesized findings.</summary>
public sealed record DocumentRetrievalResult(
    string Mode,
    Passage[] Passages,
    DocumentCoverage[] Coverage,
    DocumentFinding[] Findings,
    bool Synthesized);
