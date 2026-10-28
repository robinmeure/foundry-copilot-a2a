using Microsoft.Agents.CopilotStudio.Client.Discovery;

namespace FoundryCopilotA2A.Adapter;

public sealed class CopilotStudioOptions
{
    public const string SectionName = "CopilotStudio";

    public string? DirectConnectUrl { get; set; }

    public string EnvironmentId { get; set; } = string.Empty;

    public string SchemaName { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string ManagedIdentityClientId { get; set; } = string.Empty;

    public string DefaultAgent { get; set; } = "default";

    public Dictionary<string, CopilotStudioAgentOptions> Agents { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Power Platform cloud. Must be set: the SDK defaults to <c>Unknown</c>, which makes
    /// <c>CopilotClient.ScopeFromSettings</c> throw "Invalid cluster category value: Unknown".
    /// </summary>
    public PowerPlatformCloud Cloud { get; set; } = PowerPlatformCloud.Prod;

    /// <summary>Only used when <see cref="Cloud"/> is <c>Other</c>.</summary>
    public string? CustomPowerPlatformCloud { get; set; }

    public AgentType AgentType { get; set; } = AgentType.Published;

    /// <summary>
    /// Validates that the agent can actually be addressed.
    /// </summary>
    /// <remarks>
    /// An agent is addressed either by the direct connection URL that Copilot Studio publishes,
    /// or by environment plus schema name. Demanding both rejects the connection string makers
    /// are given, and deriving the environment host by hand is the step most likely to be wrong.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId) ||
            string.IsNullOrWhiteSpace(ClientId) ||
            (string.IsNullOrWhiteSpace(ClientSecret) &&
             string.IsNullOrWhiteSpace(ManagedIdentityClientId)))
        {
            throw new InvalidOperationException(
                "CopilotStudio TenantId, ClientId, and either ClientSecret or " +
                "ManagedIdentityClientId are required.");
        }

        if (!string.IsNullOrWhiteSpace(ManagedIdentityClientId) &&
            !Guid.TryParse(ManagedIdentityClientId, out _))
        {
            throw new InvalidOperationException(
                "CopilotStudio ManagedIdentityClientId must be a GUID.");
        }

        if (Agents.Count == 0)
        {
            ValidateAddress("default", DirectConnectUrl, EnvironmentId, SchemaName);
            return;
        }

        var resolvedAgentIds = Agents
            .Select(entry => ResolveAgentId(entry.Key, entry.Value))
            .ToArray();

        if (!resolvedAgentIds.Any(agentId =>
                string.Equals(agentId, DefaultAgent, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"CopilotStudio:DefaultAgent '{DefaultAgent}' is not present in CopilotStudio:Agents.");
        }

        foreach (var (agentId, agent) in Agents)
        {
            var resolvedAgentId = ResolveAgentId(agentId, agent);
            if (resolvedAgentId.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                               character is not '-' and not '_'))
            {
                throw new InvalidOperationException(
                    "CopilotStudio agent IDs may contain only ASCII letters, digits, '-' and '_'.");
            }

            if (string.IsNullOrWhiteSpace(agent.DisplayName))
            {
                throw new InvalidOperationException(
                    $"CopilotStudio agent '{agentId}' requires a DisplayName.");
            }

            ValidateAddress(
                resolvedAgentId,
                agent.DirectConnectUrl,
                agent.EnvironmentId,
                agent.SchemaName);
        }

        if (resolvedAgentIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            resolvedAgentIds.Length)
        {
            throw new InvalidOperationException(
                "CopilotStudio agent IDs must be unique.");
        }
    }

    public IReadOnlyList<ResolvedCopilotStudioAgent> ResolveAgents()
    {
        if (Agents.Count == 0)
        {
            return
            [
                new(
                    DefaultAgent,
                    "Copilot Studio specialist",
                    DirectConnectUrl,
                    EnvironmentId,
                    SchemaName,
                    CopilotStudioHarness.Standard)
            ];
        }

        return Agents
            .Select(entry => new ResolvedCopilotStudioAgent(
                ResolveAgentId(entry.Key, entry.Value),
                entry.Value.DisplayName,
                entry.Value.DirectConnectUrl,
                entry.Value.EnvironmentId,
                entry.Value.SchemaName,
                entry.Value.Harness)
            {
                ChainTargets = entry.Value.ChainTargets
            })
            .OrderBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveAgentId(
        string configurationKey,
        CopilotStudioAgentOptions options) =>
        string.IsNullOrWhiteSpace(options.Id) ? configurationKey : options.Id;

    private static void ValidateAddress(
        string agentId,
        string? directConnectUrl,
        string environmentId,
        string schemaName)
    {
        var hasDirectConnectUrl = !string.IsNullOrWhiteSpace(directConnectUrl);
        var hasDiscovery = !string.IsNullOrWhiteSpace(environmentId) &&
                           !string.IsNullOrWhiteSpace(schemaName);

        if (!hasDirectConnectUrl && !hasDiscovery)
        {
            throw new InvalidOperationException(
                $"CopilotStudio agent '{agentId}' requires either DirectConnectUrl, " +
                "or both EnvironmentId and SchemaName.");
        }
    }
}

public sealed class CopilotStudioAgentOptions
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? DirectConnectUrl { get; set; }

    public string EnvironmentId { get; set; } = string.Empty;

    public string SchemaName { get; set; } = string.Empty;

    public CopilotStudioHarness Harness { get; set; } = CopilotStudioHarness.Standard;

    public string[] ChainTargets { get; set; } = [];
}
