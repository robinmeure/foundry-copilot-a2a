using System.Globalization;
using System.Text.Json;

namespace FoundryCopilotA2A.Skills;

/// <summary>
/// One validated skill folder: Agent Skills frontmatter, the instruction body that <c>load_skill</c> returns,
/// optional <c>references/</c> files and the raw <c>connector.json</c>.
/// </summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Instructions,
    string DirectoryPath,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<string> Resources,
    JsonElement? ConnectorConfiguration)
{
    public string Connector => Get("connector") ?? "";

    /// <summary>The knowledge-source selection id that enables this skill for a chat turn.</summary>
    public string Source => Get("source") ?? Connector switch
    {
        SkillConventions.ApimA2aConnector => SkillConventions.ApimSource,
        SkillConventions.OfficeDocumentsConnector => SkillConventions.DocumentsSource,
        _ => Name
    };

    public string Label => Get("label") ?? Name;
    public string? Hint => Get("hint");
    public string? Group => Get("group");

    /// <summary>Set on APIM agent skills; the administrator-written description then applies to that one agent.</summary>
    public string? AgentId => Get("agent-id");

    public int Order => int.TryParse(Get("order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : 1000;

    public string? Get(string key) =>
        Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
