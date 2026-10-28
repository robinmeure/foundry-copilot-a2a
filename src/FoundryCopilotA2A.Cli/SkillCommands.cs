using FoundryCopilotA2A.Skills;

namespace FoundryCopilotA2A.Cli;

/// <summary>Scaffolds and validates the knowledge skill folders loaded by the Knowledge API.</summary>
internal static class SkillCommands
{
    private static string DefaultDirectory => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(RepoPaths.ResolveAdapterProject(null))!, "..", "..", "skills"));

    public static async Task<int> NewAsync(CliContext context, CommandArguments args, CancellationToken ct)
    {
        args.EnsureOnly("connector", "name", "description", "label", "agent-id", "source", "directory", "help");
        var connector = args.Require("connector");
        var name = args.Require("name");
        var directory = Path.GetFullPath(args.Optional("directory") ?? DefaultDirectory);
        IReadOnlyDictionary<string, string> files;
        try
        {
            files = SkillTemplates.Create(connector, name, args.Require("description"), args.Optional("label"),
                args.Optional("agent-id"), args.Optional("source"));
        }
        catch (ArgumentException exception)
        {
            throw new CliException(exception.Message.Split(" (Parameter", 2)[0]);
        }

        var folder = Path.Combine(directory, name);
        if (Directory.Exists(folder) || File.Exists(folder))
        {
            throw new CliException($"'{folder}' already exists; skills are never overwritten.");
        }
        Directory.CreateDirectory(folder);
        foreach (var (path, content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, path), content, ct);
        }

        try
        {
            await SkillCatalog.LoadAsync([directory], ct);
        }
        catch (SkillValidationException exception)
        {
            Directory.Delete(folder, recursive: true);
            throw new CliException("The new skill conflicts with existing skills and was not created:" +
                Environment.NewLine + string.Join(Environment.NewLine, exception.Errors.Select(error => "  " + error)));
        }

        context.Out.WriteLine($"Created skill '{name}' in {folder}.");
        foreach (var path in files.Keys.Order(StringComparer.Ordinal))
        {
            context.Out.WriteLine($"  {path}");
        }
        context.Out.WriteLine(connector switch
        {
            SkillConventions.ApimA2aConnector =>
                "Next: approve the agent id in A2A:AllowedAgentIds, then refine SKILL.md and restart the Knowledge API.",
            SkillConventions.AzureAiSearchConnector =>
                $"Next: map connector.json to the index schema, set Skills:{name}:Enabled/Endpoint/Index in configuration, " +
                "grant the API identity Search Index Data Reader, then restart the Knowledge API.",
            SkillConventions.OfficeDocumentsConnector =>
                "Next: refine SKILL.md, grant delegated Files.ReadWrite for Save to OneDrive, then restart the Knowledge API.",
            _ => "Next: refine SKILL.md and restart the Knowledge API."
        });
        return 0;
    }

    public static async Task<int> ValidateAsync(CliContext context, CommandArguments args, CancellationToken ct)
    {
        args.EnsureOnly("directory", "help");
        var directory = Path.GetFullPath(args.Optional("directory") ?? DefaultDirectory);
        if (!Directory.Exists(directory))
        {
            throw new CliException($"Skill directory '{directory}' does not exist.");
        }
        IReadOnlyList<SkillDefinition> skills;
        try
        {
            skills = await SkillCatalog.LoadAsync([directory], ct);
        }
        catch (SkillValidationException exception)
        {
            context.Error.WriteLine($"{exception.Errors.Count} skill error(s) in {directory}:");
            foreach (var error in exception.Errors)
            {
                context.Error.WriteLine($"  {error}");
            }
            return 2;
        }

        context.Out.WriteLine($"{skills.Count} valid skill(s) in {directory}:");
        foreach (var skill in skills)
        {
            var target = skill.AgentId is { } agentId ? $"agent {agentId}" : $"source {skill.Source}";
            context.Out.WriteLine($"  {skill.Name,-32} {skill.Connector,-16} {target}");
        }
        return 0;
    }
}
