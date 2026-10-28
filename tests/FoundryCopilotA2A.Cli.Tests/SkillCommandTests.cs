namespace FoundryCopilotA2A.Cli.Tests;

public sealed class SkillCommandTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("skill-cli-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private async Task<(int Exit, string Out, string Error)> Run(params string[] arguments)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new CliApplication(new(output, error, new ProcessRunner(), new HttpClient()));
        var exit = await app.RunAsync([.. arguments, "--directory", directory], default);
        return (exit, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("new-skill")]
    [InlineData("validate-skills")]
    public async Task CommandsHaveExplicitHelp(string command)
    {
        var output = new StringWriter();
        var app = new CliApplication(new(output, TextWriter.Null, new ProcessRunner(), new HttpClient()));
        Assert.Equal(0, await app.RunAsync([command, "--help"], default));
        Assert.Contains(command, output.ToString());
    }

    [Fact]
    public async Task ScaffoldsValidApimSearchAndGraphSkills()
    {
        var apim = await Run("new-skill", "--connector", "apim-a2a", "--name", "apim-agent-hr", "--agent-id", "hr-policies",
            "--label", "HR \"policies\"", "--description", "HR policy specialist.\nAnswers leave and benefits questions.");
        Assert.Equal(0, apim.Exit);
        var skill = await File.ReadAllTextAsync(Path.Combine(directory, "apim-agent-hr", "SKILL.md"));
        Assert.Contains("agent-id: \"hr-policies\"", skill);
        Assert.Contains("label: \"HR \\\"policies\\\"\"", skill);
        Assert.Contains("A2A:AllowedAgentIds", apim.Out);

        Assert.Equal(0, (await Run("new-skill", "--connector", "azure-ai-search", "--name", "wetten",
            "--description", "Shared Dutch legislation index.")).Exit);
        Assert.True(File.Exists(Path.Combine(directory, "wetten", "connector.json")));

        Assert.Equal(0, (await Run("new-skill", "--connector", "graph", "--name", "m365-mail",
            "--source", "mail", "--description", "Outlook mail.")).Exit);

        var validated = await Run("validate-skills");
        Assert.Equal(0, validated.Exit);
        Assert.Contains("3 valid skill(s)", validated.Out);
        Assert.Contains("agent hr-policies", validated.Out);
        Assert.Contains("source wetten", validated.Out);
    }

    [Fact]
    public async Task ScaffoldsAnOfficeDocumentsSkillThatUsesTheDocumentsSource()
    {
        var created = await Run("new-skill", "--connector", "office-documents", "--name", "office-documents",
            "--description", "Word and PowerPoint files.");
        Assert.Equal(0, created.Exit);
        Assert.Contains("Files.ReadWrite", created.Out);
        Assert.Contains("source: \"documents\"", await File.ReadAllTextAsync(Path.Combine(directory, "office-documents", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(directory, "office-documents", "connector.json")));

        var validated = await Run("validate-skills");
        Assert.Equal(0, validated.Exit);
        Assert.Contains("1 valid skill(s)", validated.Out);

        Assert.Equal(2, (await Run("new-skill", "--connector", "office-documents", "--name", "slides",
            "--description", "More slides.")).Exit);
    }

    [Theory]
    [InlineData("--connector", "apim-a2a", "--name", "no-agent", "--description", "d")]
    [InlineData("--connector", "graph", "--name", "files", "--source", "files", "--description", "d")]
    [InlineData("--connector", "ftp", "--name", "ftp", "--description", "d")]
    [InlineData("--connector", "apim-a2a", "--name", "Bad_Name", "--agent-id", "x", "--description", "d")]
    [InlineData("--connector", "apim-a2a", "--name", "no-description", "--agent-id", "x", "--description", " ")]
    public async Task InvalidSkillsAreRejectedWithoutWritingFiles(params string[] arguments)
    {
        Assert.Equal(2, (await Run(["new-skill", .. arguments])).Exit);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task ConflictsAreRolledBackAndExistingSkillsAreNeverOverwritten()
    {
        Assert.Equal(0, (await Run("new-skill", "--connector", "graph", "--name", "mail", "--source", "mail", "--description", "d")).Exit);
        var duplicateSource = await Run("new-skill", "--connector", "graph", "--name", "mail-two", "--source", "mail", "--description", "d");
        Assert.Equal(2, duplicateSource.Exit);
        Assert.False(Directory.Exists(Path.Combine(directory, "mail-two")));
        Assert.Equal(2, (await Run("new-skill", "--connector", "graph", "--name", "mail", "--source", "mail", "--description", "other")).Exit);
        Assert.Contains("description: >-\n  d\n", await File.ReadAllTextAsync(Path.Combine(directory, "mail", "SKILL.md")));
    }

    [Fact]
    public async Task ValidationReportsBrokenFolders()
    {
        Directory.CreateDirectory(Path.Combine(directory, "broken"));
        await File.WriteAllTextAsync(Path.Combine(directory, "broken", "SKILL.md"), "---\nname: other\n---\nBody.");
        var result = await Run("validate-skills");
        Assert.Equal(2, result.Exit);
        Assert.Contains("skill error(s)", result.Error);
    }
}
