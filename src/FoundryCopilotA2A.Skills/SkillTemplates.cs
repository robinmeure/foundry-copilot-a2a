using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Skills;

/// <summary>Starter skill folders for each connector. Every template passes <see cref="SkillCatalog"/> validation.</summary>
public static class SkillTemplates
{
    /// <summary>Returns the files of a new skill folder, keyed by path relative to that folder.</summary>
    public static IReadOnlyDictionary<string, string> Create(
        string connector, string name, string description, string? label = null, string? agentId = null, string? source = null)
    {
        if (!SkillConventions.IsValidName(name))
        {
            throw new ArgumentException("Skill names use 1-64 lower-case letters, digits and single hyphens.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(description) || description.Length > SkillConventions.MaxDescriptionLength)
        {
            throw new ArgumentException($"A description of 1-{SkillConventions.MaxDescriptionLength} characters is required.", nameof(description));
        }
        if (label is { Length: > SkillConventions.MaxLabelLength })
        {
            throw new ArgumentException($"Labels are at most {SkillConventions.MaxLabelLength} characters.", nameof(label));
        }

        var metadata = new List<(string Key, string Value)> { ("connector", connector) };
        string body;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (connector)
        {
            case SkillConventions.ApimA2aConnector:
                if (string.IsNullOrWhiteSpace(agentId) || agentId.Length > SkillConventions.MaxAgentIdLength ||
                    agentId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                {
                    throw new ArgumentException("An APIM agent skill needs the agent's catalog id (--agent-id).", nameof(agentId));
                }
                metadata.Add(("agent-id", agentId));
                metadata.Add(("group", "Agents"));
                body = $"""
                    # {label ?? name}

                    Ask `{agentId}` with `ask_a2a_agent` when the question matches this agent's expertise.
                    Send one focused question; do not forward unrelated private context.

                    <!-- Administrator: describe what this agent knows, good questions to ask, and its limits. -->
                    """;
                break;
            case SkillConventions.AzureAiSearchConnector:
                metadata.Add(("group", "Search"));
                files[SkillConventions.ConnectorFileName] = SearchConnectorTemplate(name, label ?? name);
                body = $"""
                    # {label ?? name}

                    Use `{SkillConventions.DefaultSearchToolName(name)}` for questions about this index.
                    Cite only the returned [[cite:n]] markers.

                    <!-- Administrator: describe the corpus, its language, useful filters and its limits. -->
                    """;
                break;
            case SkillConventions.GraphConnector:
                if (source is null || !SkillConventions.GraphSources.Contains(source, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"A Graph skill needs --source {string.Join('|', SkillConventions.GraphSources)}.", nameof(source));
                }
                metadata.Add(("source", source));
                metadata.Add(("group", "Microsoft 365"));
                body = $"""
                    # {label ?? name}

                    <!-- Describe when and how to use the {source} Graph tools. -->
                    """;
                break;
            default:
                throw new ArgumentException(
                    $"Connector must be one of {string.Join(", ", SkillConventions.Connectors)}.", nameof(connector));
        }
        if (label is not null)
        {
            metadata.Add(("label", label));
        }

        var skill = new StringBuilder()
            .Append("---\n")
            .Append("name: ").Append(name).Append('\n')
            .Append("description: >-\n");
        foreach (var line in description.Trim().Replace("\r\n", "\n").Split('\n'))
        {
            skill.Append("  ").Append(line.Trim()).Append('\n');
        }
        skill.Append("metadata:\n");
        foreach (var (key, value) in metadata)
        {
            skill.Append("  ").Append(key).Append(": ").Append(Quote(value)).Append('\n');
        }
        skill.Append("---\n\n").Append(body.Replace("\r\n", "\n")).Append('\n');
        files[SkillConventions.SkillFileName] = skill.ToString();
        return files;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string SearchConnectorTemplate(string name, string label) => JsonSerializer.Serialize(new
    {
        configurationSection = $"Skills:{name}",
        tool = new
        {
            name = SkillConventions.DefaultSearchToolName(name),
            description = $"Search the shared {label} reference index. Returns a bounded sample, not an exhaustive list."
        },
        apiVersion = "2024-07-01",
        maxResults = 8,
        maxContentLength = 3000,
        maxQueryLength = 2000,
        search = new
        {
            queryType = "simple",
            searchFields = new[] { "title", "content" },
            select = new[] { "id", "title", "content" }
        },
        filters = Array.Empty<object>(),
        result = new
        {
            idPrefix = name,
            id = "id",
            title = new[] { "title" },
            defaultTitle = label,
            content = new[] { "content" },
            locatorLabel = label,
            locator = new[] { "id" }
        },
        coverage = $"Top matching {label} excerpts; results are not exhaustive.",
        mock = new
        {
            title = $"Mock {label}",
            content = $"Mock {label} passage for local development.",
            locator = $"{label} | mock data"
        }
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
}
