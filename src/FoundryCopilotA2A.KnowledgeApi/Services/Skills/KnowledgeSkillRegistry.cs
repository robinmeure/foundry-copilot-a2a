using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.Skills;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Skills;

/// <summary>A skill whose source is selected and configured for this turn, with its tools.</summary>
public sealed record AvailableSkill(SkillDefinition Skill, IReadOnlyList<KnowledgeSkillTool> Tools, string? Guardrails);

/// <summary>A selectable knowledge source, derived from skill folders for the chat UI.</summary>
public sealed record KnowledgeSourceDescriptor(string Id, string Label, string? Hint, string? Group);

/// <summary>Joins skill folders to the code connectors that execute them.</summary>
public sealed class KnowledgeSkillRegistry
{
    private readonly Dictionary<string, IKnowledgeSkillConnector> connectors;

    public KnowledgeSkillRegistry(KnowledgeSkillCatalog catalog, IEnumerable<IKnowledgeSkillConnector> connectors)
    {
        this.connectors = connectors.ToDictionary(connector => connector.Name, StringComparer.Ordinal);
        Skills = catalog.Skills;
        var errors = new List<string>();
        foreach (var skill in Skills)
        {
            if (!this.connectors.TryGetValue(skill.Connector, out var connector))
            {
                errors.Add($"{skill.Name}: connector '{skill.Connector}' is not registered.");
                continue;
            }
            errors.AddRange(connector.Validate(skill));
        }
        if (errors.Count > 0)
        {
            throw new SkillValidationException(errors);
        }
    }

    public IReadOnlyList<SkillDefinition> Skills { get; }

    /// <summary>Skills the model may load this turn: source selected, backend configured and at least one tool permitted.</summary>
    public IReadOnlyList<AvailableSkill> Available(SkillScope scope)
    {
        var available = new List<AvailableSkill>();
        foreach (var skill in Skills)
        {
            var connector = connectors[skill.Connector];
            if (!scope.Sources.Contains(skill.Source) || !connector.IsConfigured(skill))
            {
                continue;
            }
            var tools = connector.GetTools(skill, scope);
            if (tools.Count > 0)
            {
                available.Add(new AvailableSkill(skill, tools, connector.Guardrails(skill, tools)));
            }
        }
        return available;
    }

    /// <summary>Configured sources in display order; APIM per-agent skills share the generic skill's source.</summary>
    public IReadOnlyList<KnowledgeSourceDescriptor> Sources() => Skills
        .GroupBy(skill => skill.Source, StringComparer.Ordinal)
        .Where(group => IsConfigured(group.Key))
        .Select(group => group.FirstOrDefault(skill => skill.AgentId is null) ?? group.First())
        .Select(skill => new KnowledgeSourceDescriptor(skill.Source, skill.Label, skill.Hint, skill.Group))
        .ToArray();

    public bool IsKnown(string source) => Skills.Any(skill => skill.Source == source);

    public bool IsConfigured(string source) =>
        Skills.Any(skill => skill.Source == source && connectors[skill.Connector].IsConfigured(skill));

    /// <summary>The source label for prompts and errors; falls back to the source id.</summary>
    public string LabelFor(string source) =>
        Skills.Where(skill => skill.Source == source).OrderBy(skill => skill.AgentId is null ? 0 : 1)
            .Select(skill => skill.Label).FirstOrDefault() ?? source;

    public void EnsureKnown(IEnumerable<string> sources)
    {
        if (sources.Any(source => !IsKnown(source)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_source", "Unknown knowledge source.");
        }
    }

    public void EnsureConfigured(IEnumerable<string> sources)
    {
        foreach (var source in sources.Distinct(StringComparer.Ordinal))
        {
            if (IsKnown(source) && !IsConfigured(source))
            {
                var skill = Skills.First(candidate => candidate.Source == source);
                throw connectors[skill.Connector].NotConfigured(skill);
            }
        }
    }
}
