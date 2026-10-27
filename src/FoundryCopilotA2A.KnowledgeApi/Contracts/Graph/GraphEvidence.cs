using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

/// <summary>Separates authorized Graph passages from partial-source failure warnings.</summary>
public sealed record GraphEvidence(Passage[] Passages, string[] Warnings);
