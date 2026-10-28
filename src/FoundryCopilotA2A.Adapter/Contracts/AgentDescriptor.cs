using System.Text.Json.Serialization;

namespace FoundryCopilotA2A.Adapter;

public sealed record ResolvedFoundryAgent(
    string Id,
    string DisplayName,
    Uri Endpoint,
    IReadOnlyList<string> ChainTargets);

public enum CopilotStudioHarness
{
    Standard,
    GitHubCopilot
}

public sealed record ResolvedCopilotStudioAgent(
    string Id,
    string DisplayName,
    string? DirectConnectUrl,
    string EnvironmentId,
    string SchemaName,
    CopilotStudioHarness Harness)
{
    public IReadOnlyList<string> ChainTargets { get; init; } = [];
}

public enum AgentProvider
{
    CopilotStudio,
    Foundry,
    ApiManagement
}

public sealed record AgentDescriptor(
    string Id,
    string DisplayName,
    [property: JsonIgnore] AgentProvider ProviderKind,
    bool Supported,
    string? StatusMessage,
    IReadOnlyList<string> ChainTargets)
{
    public bool CanOrchestrate => Supported && ChainTargets.Count > 0;

    public string Provider => ProviderKind switch
    {
        AgentProvider.CopilotStudio => "copilotStudio",
        AgentProvider.Foundry => "foundry",
        AgentProvider.ApiManagement => "apiManagement",
        _ => throw new InvalidOperationException($"Unsupported agent provider '{ProviderKind}'.")
    };
}
