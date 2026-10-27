namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;

/// <summary>A chat turn submitted to the knowledge API.</summary>
public sealed record ChatRequest(
    string ConversationId,
    string Text,
    string[]? Sources = null,
    string? TimeZone = null,
    string[]? Agents = null);
