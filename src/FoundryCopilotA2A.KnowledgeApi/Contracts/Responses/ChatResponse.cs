using FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Files;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;

/// <summary>The validated, non-streaming chat response returned to clients.</summary>
public sealed record ChatResponse(string Answer, string[] Warnings, CitationPayload Citations)
{
    public GraphActionView[] Actions { get; init; } = [];

    /// <summary>Files generated this turn; download them from <c>api/conversations/{conversation}/files/{id}</c> before they expire.</summary>
    public GeneratedFileView[] Files { get; init; } = [];
}
