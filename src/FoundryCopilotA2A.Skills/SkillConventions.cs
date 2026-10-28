using System.Text.RegularExpressions;

namespace FoundryCopilotA2A.Skills;

/// <summary>Shared limits and names for knowledge skill folders; the API and CLI validate with the same rules.</summary>
public static partial class SkillConventions
{
    public const string SkillFileName = "SKILL.md";
    public const string ConnectorFileName = "connector.json";
    public const string ReferencesFolder = "references";

    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxInstructionsLength = 20_000;
    public const int MaxLabelLength = 60;
    public const int MaxHintLength = 120;
    public const int MaxResourceBytes = 64 * 1024;
    public const int MaxResourcesPerSkill = 20;
    public const int MaxAgentIdLength = 200;

    public const string AzureAiSearchConnector = "azure-ai-search";
    public const string ApimA2aConnector = "apim-a2a";
    public const string GraphConnector = "graph";
    public const string OfficeDocumentsConnector = "office-documents";

    /// <summary>The selection id shared by every APIM agent skill; agents are narrowed by the per-chat agent selection.</summary>
    public const string ApimSource = "a2a";

    /// <summary>The selection id of the single office-documents skill.</summary>
    public const string DocumentsSource = "documents";

    public static readonly string[] Connectors = [AzureAiSearchConnector, ApimA2aConnector, GraphConnector, OfficeDocumentsConnector];
    public static readonly string[] GraphSources = ["mail", "calendar", "todo", "planner", "teams", "people"];
    public static readonly string[] AllowedResourceExtensions = [".md", ".txt", ".json", ".csv"];

    /// <summary>Tool names owned by the runtime or by code-defined connectors; skill-defined tools cannot shadow them.</summary>
    public static readonly string[] ReservedToolNames =
        ["load_skill", "read_skill_resource", "search_documents", "retrieve_documents", "list_a2a_agents", "ask_a2a_agent",
            "create_word_document", "create_presentation"];

    public static bool IsValidName(string? value) =>
        value is { Length: > 0 and <= MaxNameLength } && NamePattern().IsMatch(value);

    public static bool IsValidToolName(string? value) =>
        value is not null && ToolNamePattern().IsMatch(value);

    public static bool IsReservedToolName(string name) =>
        ReservedToolNames.Contains(name, StringComparer.Ordinal) || name.StartsWith("graph_", StringComparison.Ordinal);

    public static bool IsValidSearchField(string? value) =>
        value is not null && SearchFieldPattern().IsMatch(value);

    public static bool IsValidArgumentName(string? value) =>
        value is not null && ArgumentPattern().IsMatch(value);

    public static bool IsValidConfigurationSection(string? value) =>
        value is not null && ConfigurationSectionPattern().IsMatch(value);

    /// <summary>A tool name derived from a skill name, for example <c>policy-docs</c> becomes <c>search_policy_docs</c>.</summary>
    public static string DefaultSearchToolName(string skillName) => "search_" + skillName.Replace('-', '_');

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SearchFieldPattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.]*(:[A-Za-z0-9_.-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationSectionPattern();
}
