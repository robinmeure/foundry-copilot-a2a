using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter;

public sealed class AgentCatalog
{
    public const string MockAgentId = "mock";
    public const string MockFailureAgentId = "mock-failure";

    private readonly bool _useMockBackend;
    private readonly CopilotStudioOptions _copilotStudioOptions;
    private readonly IReadOnlyList<AgentDescriptor> _agents;
    private readonly IReadOnlyDictionary<string, ResolvedFoundryAgent> _foundryAgents;

    public AgentCatalog(
        IConfiguration configuration,
        IOptions<CopilotStudioOptions> copilotStudioOptions,
        IOptions<FoundryOptions> foundryOptions)
    {
        _useMockBackend = string.Equals(
            configuration["Adapter:Backend"] ?? "Mock", "Mock", StringComparison.OrdinalIgnoreCase);
        _copilotStudioOptions = copilotStudioOptions.Value;

        var configuredCopilotStudioAgents = _useMockBackend
            ? [
                new AgentDescriptor(
                    MockAgentId,
                    "Mock Copilot Studio",
                    AgentProvider.CopilotStudio,
                    true,
                    null,
                    []),
                new AgentDescriptor(
                    MockFailureAgentId,
                    "Mock Failure (always fails)",
                    AgentProvider.CopilotStudio,
                    true,
                    null,
                    [])
            ]
            : _copilotStudioOptions.ResolveAgents()
                .Select(agent =>
                {
                    var supported = agent.Harness == CopilotStudioHarness.Standard;
                    return new AgentDescriptor(
                        agent.Id,
                        agent.DisplayName,
                        AgentProvider.CopilotStudio,
                        supported,
                        supported ? null : CopilotStudioResponseClassifier.Guidance,
                        agent.ChainTargets);
                })
                .ToArray();
        var copilotStudioAgents =
            !_useMockBackend && configuration.GetValue<bool>("Adapter:EnableFailureMock")
                ? configuredCopilotStudioAgents
                    .Append(new AgentDescriptor(
                        MockFailureAgentId,
                        "Mock Failure (always fails)",
                        AgentProvider.CopilotStudio,
                        true,
                        null,
                        []))
                    .ToArray()
                : configuredCopilotStudioAgents;
        _foundryAgents = foundryOptions.Value.ResolveAgents()
            .ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        var foundryAgents = _foundryAgents.Values
            .Select(agent => new AgentDescriptor(
                agent.Id,
                agent.DisplayName,
                AgentProvider.Foundry,
                true,
                null,
                agent.ChainTargets));
        _agents = copilotStudioAgents.Concat(foundryAgents).ToArray();

        var duplicate = _agents
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Agent ID '{duplicate.Key}' is configured more than once.");
        }

        var copilotTargets = copilotStudioAgents
            .Where(agent => agent.Supported)
            .ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        _agents = _agents.Select(agent =>
        {
            var chainTargets = agent.ChainTargets
                .Select(target => target.Trim())
                .Where(target => target.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(target =>
                {
                    if (!agent.Supported)
                    {
                        throw new InvalidOperationException(
                            $"Unsupported agent '{agent.Id}' cannot have chain targets.");
                    }

                    if (string.Equals(agent.Id, target, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Agent '{agent.Id}' cannot be its own chain target.");
                    }

                    if (!copilotTargets.TryGetValue(target, out var specialist))
                    {
                        throw new InvalidOperationException(
                            $"Agent '{agent.Id}' chain target '{target}' is not a " +
                            "supported Copilot Studio agent.");
                    }

                    return specialist.Id;
                })
                .ToArray();
            return agent with { ChainTargets = chainTargets };
        }).ToArray();
    }

    public IReadOnlyList<AgentDescriptor> Agents => _agents;

    public string DefaultAgentId =>
        _useMockBackend ? MockAgentId : _copilotStudioOptions.DefaultAgent;

    public AgentDescriptor ResolveAgent(string? requestedAgentId)
    {
        var agentId = string.IsNullOrWhiteSpace(requestedAgentId)
            ? DefaultAgentId
            : requestedAgentId.Trim();

        var configuredAgent = _agents.FirstOrDefault(agent =>
            string.Equals(agent.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (configuredAgent is null)
        {
            throw new AdapterRequestException(
                $"Agent '{agentId}' is not configured.");
        }

        if (!configuredAgent.Supported)
        {
            throw new AdapterRequestException(configuredAgent.StatusMessage!);
        }

        return configuredAgent;
    }

    public bool TryResolveAgent(string? requestedAgentId, out AgentDescriptor? agent)
    {
        var agentId = string.IsNullOrWhiteSpace(requestedAgentId)
            ? DefaultAgentId
            : requestedAgentId.Trim();
        agent = _agents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, agentId, StringComparison.OrdinalIgnoreCase));
        return agent is not null;
    }

    public string ResolveAgentId(string? requestedAgentId) =>
        ResolveAgent(requestedAgentId).Id;

    public ResolvedFoundryAgent ResolveFoundryAgent(string agentId) =>
        _foundryAgents.TryGetValue(agentId, out var agent)
            ? agent
            : throw new AdapterRequestException(
                $"Foundry agent '{agentId}' is not configured.");

    public AgentDescriptor ResolveChainTarget(string orchestratorAgentId, string targetAgentId)
    {
        var orchestrator = ResolveAgent(orchestratorAgentId);
        if (!orchestrator.CanOrchestrate)
        {
            throw new AdapterRequestException(
                $"Agent '{orchestrator.Id}' has no configured native chain targets.");
        }

        var target = ResolveAgent(targetAgentId);
        if (target.ProviderKind != AgentProvider.CopilotStudio ||
            !orchestrator.ChainTargets.Contains(target.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new AdapterRequestException(
                $"Agent '{target.Id}' is not a configured chain target for '{orchestrator.Id}'.");
        }

        return target;
    }
}
