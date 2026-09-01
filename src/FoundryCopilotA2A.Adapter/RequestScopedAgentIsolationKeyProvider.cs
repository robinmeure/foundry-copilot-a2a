using Microsoft.Agents.AI.Hosting;

namespace FoundryCopilotA2A.Adapter;

public sealed class AgentIsolationKeyContext
{
    private readonly AsyncLocal<string?> _current = new();

    public string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

internal sealed class RequestScopedAgentIsolationKeyProvider(
    AgentIsolationKeyContext context) : AgentIsolationKeyProvider
{
    public override ValueTask<string?> GetIsolationKeyAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(context.Current);
}
