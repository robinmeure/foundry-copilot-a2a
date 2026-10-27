using System.Text.Json;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

/// <summary>A validated change the model staged; nothing is written until the user confirms it.</summary>
public sealed record GraphActionProposal(
    string Tool,
    string Kind,
    string Title,
    string Summary,
    GraphActionDetail[] Details,
    JsonElement Arguments);
