using System.Text.Json;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

public sealed record GraphTool(
    string Name,
    string Source,
    string Description,
    JsonElement Parameters)
{
    public object Definition => new
    {
        type = "function",
        name = Name,
        description = Description,
        parameters = Parameters,
        strict = true
    };
}
