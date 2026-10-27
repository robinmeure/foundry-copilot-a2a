using System.Text.Json;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Chat.Tools;

internal sealed class KnowledgeFunction(
    string name,
    string description,
    JsonElement schema,
    Func<JsonElement, CancellationToken, Task<object>> invoke) : AIFunction
{
    public override string Name => name;
    public override string Description => description;
    public override JsonElement JsonSchema => schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        return await invoke(JsonSerializer.SerializeToElement(arguments), cancellationToken);
    }
}
