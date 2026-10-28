using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using FoundryCopilotA2A.Skills;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class SkillCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "skill-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void RepositorySkillsCoverEveryGraphSourceAndConnector()
    {
        Assert.Equal(GraphSkill.Sources.Order(), SkillConventions.GraphSources.Order());
        var skills = TestSkills.Catalog.Skills;
        Assert.Equal(SkillConventions.GraphSources.Order(),
            skills.Where(skill => skill.Connector == SkillConventions.GraphConnector).Select(skill => skill.Source).Order());
        Assert.Contains(skills, skill => skill.Connector == SkillConventions.AzureAiSearchConnector);
        Assert.Contains(skills, skill => skill.Connector == SkillConventions.ApimA2aConnector && skill.AgentId is null);
        Assert.Equal("Mock APIM knowledge agent for local development. Answers general questions about the sample knowledge base without calling Azure.",
            TestSkills.Catalog.AgentDescription("mock-apim-knowledge"));
    }

    [Fact]
    public async Task NewApimAgentSkillNeedsOnlyAFolder()
    {
        Write("apim-agent-hr", """
            ---
            name: apim-agent-hr
            description: HR policy specialist for leave, onboarding and benefits questions.
            metadata:
              connector: apim-a2a
              agent-id: hr-policies
            ---

            Ask `hr-policies` for HR policy questions.
            """);
        var skill = Assert.Single(await SkillCatalog.LoadAsync([directory], default));
        Assert.Equal("a2a", skill.Source);
        Assert.Equal("hr-policies", skill.AgentId);
        Assert.Equal("apim-agent-hr", skill.Label);
    }

    [Theory]
    [InlineData("Wrong-Name", "name: Wrong-Name\ndescription: d\nmetadata:\n  connector: graph\n  source: mail")]
    [InlineData("no-description", "name: no-description\nmetadata:\n  connector: graph\n  source: mail")]
    [InlineData("bad-connector", "name: bad-connector\ndescription: d\nmetadata:\n  connector: ftp")]
    [InlineData("unknown-graph", "name: unknown-graph\ndescription: d\nmetadata:\n  connector: graph\n  source: files")]
    [InlineData("search-without-config", "name: search-without-config\ndescription: d\nmetadata:\n  connector: azure-ai-search")]
    public async Task InvalidSkillFoldersFailAtStartup(string name, string frontMatter)
    {
        Write(name, $"---\n{frontMatter}\n---\n\nBody.");
        var error = await Assert.ThrowsAsync<SkillValidationException>(() => SkillCatalog.LoadAsync([directory], default));
        Assert.NotEmpty(error.Errors);
    }

    [Fact]
    public async Task DuplicateSourcesAndNamesAreRejected()
    {
        Write("mail-a", "---\nname: mail-a\ndescription: d\nmetadata:\n  connector: graph\n  source: mail\n---\nBody.");
        Write("mail-b", "---\nname: mail-b\ndescription: d\nmetadata:\n  connector: graph\n  source: mail\n---\nBody.");
        await Assert.ThrowsAsync<SkillValidationException>(() => SkillCatalog.LoadAsync([directory], default));
    }

    [Fact]
    public void RegistryRejectsSkillsWithoutARegisteredConnector()
    {
        var error = Assert.Throws<SkillValidationException>(() =>
            new KnowledgeSkillRegistry(TestSkills.Catalog, []));
        Assert.Contains(error.Errors, message => message.StartsWith("kamervragen: connector 'azure-ai-search'"));
    }

    [Fact]
    public async Task CapabilitiesDescribeConfiguredSourcesFromSkillFolders()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
        });
        using var client = api.CreateClient();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/capabilities");
        var sources = capabilities.GetProperty("sources").EnumerateArray()
            .ToDictionary(source => source.GetProperty("id").GetString()!);
        Assert.Equal(new[] { "a2a", "calendar", "documents", "kamervragen", "mail", "people", "planner", "teams", "todo" },
            sources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Kamervragen", sources["kamervragen"].GetProperty("label").GetString());
        Assert.False(string.IsNullOrWhiteSpace(sources["a2a"].GetProperty("label").GetString()));
    }

    private void Write(string name, string content)
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, name));
        File.WriteAllText(Path.Combine(folder.FullName, SkillConventions.SkillFileName), content.Replace("\r\n", "\n"));
    }
}
