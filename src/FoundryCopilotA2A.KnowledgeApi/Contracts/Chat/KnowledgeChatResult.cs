using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Files;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;

/// <summary>A completed turn before its evidence is mapped into the public citation response.</summary>
public sealed record KnowledgeChatResult(
    string Answer,
    Passage[] Evidence,
    string[] Warnings,
    GraphActionView[] Actions)
{
    public GeneratedFileView[] Files { get; init; } = [];
}
