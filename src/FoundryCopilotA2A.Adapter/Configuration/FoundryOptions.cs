namespace FoundryCopilotA2A.Adapter;

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
