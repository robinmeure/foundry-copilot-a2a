using Microsoft.Agents.CopilotStudio.Client.Discovery;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

namespace FoundryCopilotA2A.Adapter;

public sealed class AdapterOptions
{
    public const string SectionName = "Adapter";

    public string Backend { get; set; } = "Mock";

    public string PublicBaseUrl { get; set; } = "http://localhost:5099";

    public int IdempotencyTtlMinutes { get; set; } = 15;

    public int ConversationTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Must be set explicitly to run without authentication. Prevents an unsecured deployment
    /// from starting by accident, since both caches partition on the caller identity.
    /// </summary>
    public bool AllowAnonymousDevelopmentMode { get; set; }

    /// <summary>Bounds a single delegated invocation so a hung backend cannot pin a request open.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Per-attempt budget for an outbound Foundry A2A call. A chained call runs an LLM turn, an
    /// inbound A2A call back into this adapter, and a Copilot Studio turn, so it needs far more
    /// than the 10s default. The call is not retried, because a retry re-runs the whole chain.
    /// </summary>
    public int FoundryRequestTimeoutSeconds { get; set; } = 120;

    /// <summary>Hard cap on cache entries. A2A messageIds are unique per message, so without a cap the caches only grow.</summary>
    public long MaxCacheEntries { get; set; } = 10_000;

    /// <summary>Exact browser origins allowed to call the A2A endpoint.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    public bool UseMockBackend =>
        string.Equals(Backend, "Mock", StringComparison.OrdinalIgnoreCase);
}

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public Dictionary<string, FoundryAgentOptions> Agents { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ResolvedFoundryAgent> ResolveAgents() =>
        Agents.Select(entry =>
        {
            var id = string.IsNullOrWhiteSpace(entry.Value.Id)
                ? entry.Key
                : entry.Value.Id;
            if (id.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                    character is not '-' and not '_'))
            {
                throw new InvalidOperationException(
                    "Foundry agent IDs may contain only ASCII letters, digits, '-' and '_'.");
            }

            if (string.IsNullOrWhiteSpace(entry.Value.DisplayName))
            {
                throw new InvalidOperationException(
                    $"Foundry agent '{id}' requires a DisplayName.");
            }

            if (!Uri.TryCreate(entry.Value.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps ||
                !endpoint.AbsolutePath.EndsWith(
                    "/endpoint/protocols/a2a",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Foundry agent '{id}' requires an HTTPS A2A protocol endpoint.");
            }

            return new ResolvedFoundryAgent(
                id,
                entry.Value.DisplayName,
                endpoint,
                entry.Value.ChainTargets);
        }).ToArray();
}

public sealed class FoundryAgentOptions
{
    public string? Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public string[] ChainTargets { get; set; } = [];
}

public sealed record ResolvedFoundryAgent(
    string Id,
    string DisplayName,
    Uri Endpoint,
    IReadOnlyList<string> ChainTargets);

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; set; }

    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Explicit issuer allow-list. When empty, the v1.0 and v2.0 issuer forms are derived
    /// from the tenant in <see cref="Authority"/>.
    /// </summary>
    public string[] ValidIssuers { get; set; } = [];

    /// <summary>
    /// Explicit audience allow-list. When empty, both the "api://&lt;id&gt;" and bare-id forms
    /// of <see cref="Audience"/> are accepted.
    /// </summary>
    public string[] ValidAudiences { get; set; } = [];

    public IReadOnlyCollection<string> ResolveValidIssuers()
    {
        if (ValidIssuers.Length > 0)
        {
            return ValidIssuers;
        }

        var tenantId = ExtractTenantId(Authority);
        if (tenantId is null)
        {
            // Unrecognised authority shape: fall back to the configured value verbatim rather
            // than guessing, so validation stays strict.
            return [Authority];
        }

        return
        [
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            $"https://sts.windows.net/{tenantId}/"
        ];
    }

    public IReadOnlyCollection<string> ResolveValidAudiences()
    {
        if (ValidAudiences.Length > 0)
        {
            return ValidAudiences;
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            return [];
        }

        var bare = Audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
            ? Audience["api://".Length..]
            : Audience;

        return bare == Audience ? [Audience, $"api://{Audience}"] : [Audience, bare];
    }

    private static string? ExtractTenantId(string authority)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segment = uri.Segments
            .Select(s => s.Trim('/'))
            .FirstOrDefault(s => Guid.TryParse(s, out _) || s.Contains('.', StringComparison.Ordinal));

        return string.IsNullOrEmpty(segment) ? null : segment;
    }
}

public sealed class CopilotStudioOptions
{
    public const string SectionName = "CopilotStudio";

    public string? DirectConnectUrl { get; set; }

    public string EnvironmentId { get; set; } = string.Empty;

    public string SchemaName { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

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
            string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException(
                "CopilotStudio TenantId, ClientId, and ClientSecret are required.");
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
                entry.Value.Harness))
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
}

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
    CopilotStudioHarness Harness);

public enum AgentProvider
{
    CopilotStudio,
    Foundry
}

public sealed record AgentDescriptor(
    string Id,
    string DisplayName,
    [property: JsonIgnore] AgentProvider ProviderKind,
    bool Supported,
    string? StatusMessage,
    IReadOnlyList<string> ChainTargets)
{
    public string Provider => ProviderKind switch
    {
        AgentProvider.CopilotStudio => "copilotStudio",
        AgentProvider.Foundry => "foundry",
        _ => throw new InvalidOperationException($"Unsupported agent provider '{ProviderKind}'.")
    };
}

public sealed class AgentCatalog
{
    public const string MockAgentId = "mock";

    private readonly AdapterOptions _adapterOptions;
    private readonly CopilotStudioOptions _copilotStudioOptions;
    private readonly IReadOnlyList<AgentDescriptor> _agents;
    private readonly IReadOnlyDictionary<string, ResolvedFoundryAgent> _foundryAgents;

    public AgentCatalog(
        IOptions<AdapterOptions> adapterOptions,
        IOptions<CopilotStudioOptions> copilotStudioOptions,
        IOptions<FoundryOptions> foundryOptions)
    {
        _adapterOptions = adapterOptions.Value;
        _copilotStudioOptions = copilotStudioOptions.Value;

        var copilotStudioAgents = _adapterOptions.UseMockBackend
            ? [new AgentDescriptor(
                MockAgentId,
                "Mock Copilot Studio",
                AgentProvider.CopilotStudio,
                true,
                null,
                [])]
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
                        []);
                })
                .ToArray();
        _foundryAgents = foundryOptions.Value.ResolveAgents()
            .ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        var copilotTargets = copilotStudioAgents
            .Where(agent => agent.Supported)
            .ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        var foundryAgents = _foundryAgents.Values
            .Select(agent =>
            {
                var chainTargets = agent.ChainTargets
                    .Select(target => target.Trim())
                    .Where(target => target.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var missingTarget = chainTargets.FirstOrDefault(
                    target => !copilotTargets.ContainsKey(target));
                if (missingTarget is not null)
                {
                    throw new InvalidOperationException(
                        $"Foundry agent '{agent.Id}' chain target '{missingTarget}' is not a " +
                        "supported Copilot Studio agent.");
                }

                return new AgentDescriptor(
                    agent.Id,
                    agent.DisplayName,
                    AgentProvider.Foundry,
                    true,
                    null,
                    chainTargets);
            });
        _agents = copilotStudioAgents.Concat(foundryAgents).ToArray();

        var duplicate = _agents
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Agent ID '{duplicate.Key}' is configured more than once.");
        }
    }

    public IReadOnlyList<AgentDescriptor> Agents => _agents;

    public string DefaultAgentId =>
        _adapterOptions.UseMockBackend ? MockAgentId : _copilotStudioOptions.DefaultAgent;

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

    public string ResolveAgentId(string? requestedAgentId) =>
        ResolveAgent(requestedAgentId).Id;

    public ResolvedFoundryAgent ResolveFoundryAgent(string agentId) =>
        _foundryAgents.TryGetValue(agentId, out var agent)
            ? agent
            : throw new AdapterRequestException(
                $"Foundry agent '{agentId}' is not configured.");

    public AgentDescriptor ResolveChainTarget(string foundryAgentId, string targetAgentId)
    {
        var foundryAgent = ResolveAgent(foundryAgentId);
        if (foundryAgent.ProviderKind != AgentProvider.Foundry)
        {
            throw new AdapterRequestException(
                $"Agent '{foundryAgent.Id}' cannot be used as Agent A in a chain.");
        }

        var target = ResolveAgent(targetAgentId);
        if (target.ProviderKind != AgentProvider.CopilotStudio ||
            !foundryAgent.ChainTargets.Contains(target.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new AdapterRequestException(
                $"Agent '{target.Id}' is not a configured chain target for '{foundryAgent.Id}'.");
        }

        return target;
    }
}
