using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using FoundryCopilotA2A.Skills;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Skills;

/// <summary>
/// One Microsoft 365 workload (the skill's <c>source</c>) through delegated Graph access. Read tools and
/// staged change proposals stay typed in code; the skill folder contributes workload guidance.
/// </summary>
public sealed class GraphSkillConnector(GraphSkill graph, GraphActions actions) : IKnowledgeSkillConnector
{
    public string Name => SkillConventions.GraphConnector;

    public IEnumerable<string> Validate(SkillDefinition skill) =>
        GraphSkill.Sources.Contains(skill.Source, StringComparer.Ordinal)
            ? []
            : [$"{skill.Name}: source '{skill.Source}' is not a supported Microsoft 365 workload."];

    // Delegated Graph access needs no deployment switch; consent is checked per call.
    public bool IsConfigured(SkillDefinition skill) => true;

    public ApiException NotConfigured(SkillDefinition skill) =>
        new(400, $"{skill.Source}_disabled", $"{skill.Label} is not available.");

    public IReadOnlyList<KnowledgeSkillTool> GetTools(SkillDefinition skill, SkillScope scope)
    {
        var tools = new List<KnowledgeSkillTool>();
        foreach (var tool in GraphSkill.Enabled([skill.Source]))
        {
            tools.Add(KnowledgeSkillTool.FromDefinition(tool.Definition, async (call, ct) =>
            {
                var result = await graph.Execute(call.Context, tool.Name, call.Arguments, [.. call.Scope.Sources], ct,
                    call.User.GraphTimeZone);
                return new GraphItemsToolResult(result, ToolStatus.Completed(tool.Name, result.Items.Length));
            }));
        }
        foreach (var tool in GraphActions.Enabled([skill.Source]))
        {
            tools.Add(KnowledgeSkillTool.FromDefinition(tool.Definition, async (call, ct) =>
            {
                var proposal = await actions.ProposeAsync(call.Context, tool.Name, call.Arguments, [.. call.Scope.Sources], ct,
                    call.User.GraphTimeZone);
                return new ProposalToolResult(proposal, ToolStatus.Proposed(proposal.Title));
            }) with { StagesChange = true });
        }
        return tools;
    }

    public string? Guardrails(SkillDefinition skill, IReadOnlyList<KnowledgeSkillTool> tools) =>
        tools.Any(tool => tool.StagesChange)
            ? GraphSkill.Instructions + GraphActions.Instructions
            : GraphSkill.Instructions;
}
