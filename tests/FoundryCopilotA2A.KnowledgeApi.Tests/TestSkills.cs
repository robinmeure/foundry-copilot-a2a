using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using FoundryCopilotA2A.Skills;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

/// <summary>The repository's real skill folders, as copied into the test output.</summary>
internal static class TestSkills
{
    public static KnowledgeSkillCatalog Catalog { get; } = KnowledgeSkillCatalog.Load(TestConfiguration.Create());

    public static SkillDefinition Get(string name) => Catalog.Skills.Single(skill => skill.Name == name);

    public static KnowledgeSkillRegistry Registry(
        GraphSkill graph, GraphActions actions, IA2aKnowledgeService a2a, AzureAiSearchConnector search) =>
        new(Catalog, [new GraphSkillConnector(graph, actions), new ApimA2aSkillConnector(a2a), search, new OfficeDocumentsConnector()]);
}
