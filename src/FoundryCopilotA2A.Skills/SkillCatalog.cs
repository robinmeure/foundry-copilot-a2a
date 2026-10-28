using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.Skills;

/// <summary>
/// Discovers skill folders (each a direct child folder containing <c>SKILL.md</c>) with the Agent Framework
/// Agent Skills parser and applies this repository's stricter connector rules on top.
/// </summary>
public static class SkillCatalog
{
    public static async Task<IReadOnlyList<SkillDefinition>> LoadAsync(IEnumerable<string> directories, CancellationToken ct)
    {
        var errors = new List<string>();
        var skills = new List<SkillDefinition>();
        foreach (var directory in directories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
            {
                errors.Add($"{directory}: skills folder does not exist.");
                continue;
            }
            skills.AddRange(await LoadDirectoryAsync(directory, errors, ct));
        }
        ValidateCatalog(skills, errors);
        if (errors.Count > 0)
        {
            throw new SkillValidationException(errors);
        }
        return skills.OrderBy(skill => skill.Order).ThenBy(skill => skill.Name, StringComparer.Ordinal).ToArray();
    }

    private static async Task<List<SkillDefinition>> LoadDirectoryAsync(string root, List<string> errors, CancellationToken ct)
    {
        var parsed = new Dictionary<string, AgentFileSkill>(StringComparer.OrdinalIgnoreCase);
        var source = new AgentFileSkillsSource(root);
        foreach (var skill in await source.GetSkillsAsync(new AgentSkillsSourceContext(ParserAgent.Instance, null), ct))
        {
            if (skill is AgentFileSkill file)
            {
                parsed[SkillDirectory(file.Path)] = file;
            }
        }

        var result = new List<SkillDefinition>();
        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(folder, SkillConventions.SkillFileName);
            if (!File.Exists(file))
            {
                continue;
            }
            var folderName = Path.GetFileName(folder);
            if (!parsed.TryGetValue(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), out var skill))
            {
                errors.Add($"{folderName}: SKILL.md needs YAML frontmatter with a valid name and description.");
                continue;
            }
            var definition = await ReadAsync(skill, folder, folderName, errors, ct);
            if (definition is not null)
            {
                result.Add(definition);
            }
        }
        return result;
    }

    private static async Task<SkillDefinition?> ReadAsync(AgentFileSkill skill, string folder, string folderName, List<string> errors, CancellationToken ct)
    {
        var count = errors.Count;
        var name = skill.Frontmatter.Name;
        var description = skill.Frontmatter.Description?.Trim() ?? "";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (skill.Frontmatter.Metadata is { } values)
        {
            foreach (var pair in values)
            {
                metadata[pair.Key] = pair.Value?.ToString() ?? "";
            }
        }

        if (!SkillConventions.IsValidName(name))
        {
            errors.Add($"{folderName}: name must be 1-{SkillConventions.MaxNameLength} lowercase letters, digits and single hyphens.");
        }
        else if (name != folderName)
        {
            errors.Add($"{folderName}: name '{name}' must match the folder name.");
        }
        if (description.Length is 0 or > SkillConventions.MaxDescriptionLength)
        {
            errors.Add($"{folderName}: description is required and must be at most {SkillConventions.MaxDescriptionLength} characters.");
        }

        var instructions = Body(await File.ReadAllTextAsync(Path.Combine(folder, SkillConventions.SkillFileName), ct));
        if (instructions.Length is 0 or > SkillConventions.MaxInstructionsLength)
        {
            errors.Add($"{folderName}: SKILL.md body is required and must be at most {SkillConventions.MaxInstructionsLength:N0} characters.");
        }

        JsonElement? connectorConfiguration = null;
        var connectorFile = Path.Combine(folder, SkillConventions.ConnectorFileName);
        if (File.Exists(connectorFile))
        {
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(connectorFile, ct),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                connectorConfiguration = json.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                errors.Add($"{folderName}: connector.json is not valid JSON ({exception.Message}).");
            }
        }

        var definition = new SkillDefinition(name, description, instructions, folder, metadata,
            ReadResources(folder, folderName, errors), connectorConfiguration);
        ValidateConnector(definition, folderName, connectorFile, errors);
        return errors.Count == count ? definition : null;
    }

    private static void ValidateConnector(SkillDefinition skill, string folderName, string connectorFile, List<string> errors)
    {
        if (!SkillConventions.Connectors.Contains(skill.Connector, StringComparer.Ordinal))
        {
            errors.Add($"{folderName}: metadata.connector must be one of {string.Join(", ", SkillConventions.Connectors)}.");
            return;
        }
        if (skill.Label.Length > SkillConventions.MaxLabelLength || skill.Hint is { Length: > SkillConventions.MaxHintLength })
        {
            errors.Add($"{folderName}: metadata.label must be at most {SkillConventions.MaxLabelLength} and metadata.hint at most {SkillConventions.MaxHintLength} characters.");
        }
        if (skill.Get("order") is { } order && !int.TryParse(order, out _))
        {
            errors.Add($"{folderName}: metadata.order must be an integer.");
        }
        if (skill.AgentId is not null && skill.Connector != SkillConventions.ApimA2aConnector)
        {
            errors.Add($"{folderName}: metadata.agent-id is only valid for apim-a2a skills.");
        }

        switch (skill.Connector)
        {
            case SkillConventions.GraphConnector:
                if (!SkillConventions.GraphSources.Contains(skill.Source, StringComparer.Ordinal))
                {
                    errors.Add($"{folderName}: metadata.source must be one of {string.Join(", ", SkillConventions.GraphSources)}.");
                }
                break;
            case SkillConventions.ApimA2aConnector:
                if (skill.Source != SkillConventions.ApimSource)
                {
                    errors.Add($"{folderName}: apim-a2a skills always use source '{SkillConventions.ApimSource}'.");
                }
                if (skill.AgentId is { } agentId &&
                    (agentId.Length > SkillConventions.MaxAgentIdLength || agentId.Any(char.IsControl) || agentId.Any(char.IsWhiteSpace)))
                {
                    errors.Add($"{folderName}: metadata.agent-id must be at most {SkillConventions.MaxAgentIdLength} characters without whitespace.");
                }
                break;
            case SkillConventions.OfficeDocumentsConnector:
                if (skill.Source != SkillConventions.DocumentsSource)
                {
                    errors.Add($"{folderName}: office-documents skills always use source '{SkillConventions.DocumentsSource}'.");
                }
                if (skill.ConnectorConfiguration is not null || File.Exists(connectorFile))
                {
                    errors.Add($"{folderName}: office-documents skills do not use a connector.json.");
                }
                break;
            case SkillConventions.AzureAiSearchConnector:
                if (!SkillConventions.IsValidName(skill.Source))
                {
                    errors.Add($"{folderName}: metadata.source must be lowercase letters, digits and hyphens.");
                }
                if (skill.ConnectorConfiguration is not { } json)
                {
                    if (!File.Exists(connectorFile))
                    {
                        errors.Add($"{folderName}: azure-ai-search skills need a connector.json.");
                    }
                    break;
                }
                try
                {
                    errors.AddRange(SearchSkillSettings.Parse(json).Validate(skill.Name).Select(error => $"{folderName}: connector.json {error}"));
                }
                catch (JsonException exception)
                {
                    errors.Add($"{folderName}: connector.json does not match the azure-ai-search contract ({exception.Message}).");
                }
                break;
        }
    }

    private static IReadOnlyList<string> ReadResources(string folder, string folderName, List<string> errors)
    {
        var references = Path.Combine(folder, SkillConventions.ReferencesFolder);
        if (!Directory.Exists(references))
        {
            return [];
        }
        if (Directory.EnumerateDirectories(references).Any())
        {
            errors.Add($"{folderName}: references/ must not contain sub-folders.");
        }
        var resources = new List<string>();
        foreach (var file in Directory.EnumerateFiles(references).Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            if (!SkillConventions.AllowedResourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{folderName}: references/{fileName} must be one of {string.Join(", ", SkillConventions.AllowedResourceExtensions)}.");
            }
            else if (new FileInfo(file).Length > SkillConventions.MaxResourceBytes)
            {
                errors.Add($"{folderName}: references/{fileName} exceeds {SkillConventions.MaxResourceBytes / 1024} KB.");
            }
            else
            {
                resources.Add($"{SkillConventions.ReferencesFolder}/{fileName}");
            }
        }
        if (resources.Count > SkillConventions.MaxResourcesPerSkill)
        {
            errors.Add($"{folderName}: at most {SkillConventions.MaxResourcesPerSkill} reference files are supported.");
        }
        return resources;
    }

    private static void ValidateCatalog(List<SkillDefinition> skills, List<string> errors)
    {
        foreach (var group in skills.GroupBy(skill => skill.Name).Where(group => group.Count() > 1))
        {
            errors.Add($"{group.Key}: skill name is defined in more than one folder ({string.Join(", ", group.Select(skill => skill.DirectoryPath))}).");
        }
        foreach (var group in skills.Where(skill => skill.Connector != SkillConventions.ApimA2aConnector)
            .GroupBy(skill => skill.Source).Where(group => group.Count() > 1))
        {
            errors.Add($"source '{group.Key}' is claimed by more than one skill ({string.Join(", ", group.Select(skill => skill.Name))}).");
        }
        foreach (var skill in skills.Where(skill => skill.Connector == SkillConventions.AzureAiSearchConnector &&
            (skill.Source is SkillConventions.ApimSource or SkillConventions.DocumentsSource ||
                SkillConventions.GraphSources.Contains(skill.Source))))
        {
            errors.Add($"{skill.Name}: source '{skill.Source}' is reserved for another connector.");
        }
        var apim = skills.Where(skill => skill.Connector == SkillConventions.ApimA2aConnector).ToArray();
        if (apim.Count(skill => skill.AgentId is null) > 1)
        {
            errors.Add("only one apim-a2a skill may omit metadata.agent-id.");
        }
        foreach (var group in apim.Where(skill => skill.AgentId is not null)
            .GroupBy(skill => skill.AgentId!, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add($"agent-id '{group.Key}' is described by more than one skill.");
        }
        foreach (var group in skills.Where(skill => skill.Connector == SkillConventions.AzureAiSearchConnector && skill.ConnectorConfiguration is not null)
            .GroupBy(skill => SearchSkillSettings.Parse(skill.ConnectorConfiguration!.Value).ToolNameFor(skill.Name)).Where(group => group.Count() > 1))
        {
            errors.Add($"tool '{group.Key}' is defined by more than one skill.");
        }
    }

    /// <summary>The Markdown after the YAML frontmatter; that is what a model receives when it loads the skill.</summary>
    public static string Body(string content)
    {
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return text.Trim();
        }
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return "";
        }
        var bodyStart = text.IndexOf('\n', end + 4);
        return bodyStart < 0 ? "" : text[(bodyStart + 1)..].Trim();
    }

    private static string SkillDirectory(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(Path.GetFileName(full), SkillConventions.SkillFileName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(full)!
            : full;
    }

    // AgentFileSkillsSource needs an agent for its context, but discovery never calls the model.
    private sealed class ParserAgent : IChatClient
    {
        public static readonly AIAgent Instance = new ChatClientAgent(new ParserAgent());

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
