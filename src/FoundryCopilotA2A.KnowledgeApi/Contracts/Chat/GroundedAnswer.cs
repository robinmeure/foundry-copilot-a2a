using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;

/// <summary>Carries validated evidence and proposed actions alongside the session state to commit.</summary>
public sealed record GroundedAnswer(
    string Answer,
    Passage[] Evidence,
    string[] Warnings)
{
    internal JsonElement? SessionState { get; init; }

    public GraphActionProposal[] Actions { get; init; } = [];
}
