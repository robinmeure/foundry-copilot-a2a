using System.Text.Json;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

public sealed record GraphToolResult(
    JsonElement[] Items,
    bool Truncated,
    string Coverage);
