using FoundryCopilotA2A.Skills;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Skills;

/// <summary>The validated skill folders configured under <c>Skills:Paths</c>, loaded once per process.</summary>
public sealed class KnowledgeSkillCatalog(IReadOnlyList<SkillDefinition> skills)
{
    public const string DefaultPath = "skills";

    public IReadOnlyList<SkillDefinition> Skills { get; } = skills;

    /// <summary>The administrator-written description of an APIM agent, from its <c>agent-id</c> skill.</summary>
    public string? AgentDescription(string agentId) => Skills
        .FirstOrDefault(skill => skill.Connector == SkillConventions.ApimA2aConnector &&
            string.Equals(skill.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
        ?.Description;

    public static KnowledgeSkillCatalog Load(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Skills:Paths").Get<string[]>();
        var directories = (configured is { Length: > 0 } ? configured : [DefaultPath])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
        // Discovery is local file IO at startup; there is no request context to block.
        return new KnowledgeSkillCatalog(SkillCatalog.LoadAsync(directories, CancellationToken.None).GetAwaiter().GetResult());
    }
}
