using FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;

/// <summary>The validated, non-streaming chat response returned to clients.</summary>
public sealed record ChatResponse(string Answer, string[] Warnings, CitationPayload Citations)
{
    public GraphActionView[] Actions { get; init; } = [];
}
