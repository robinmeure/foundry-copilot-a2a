namespace FoundryCopilotA2A.Cli.Tests;

public sealed class KnowledgeCommandTests
{
    [Theory]
    [InlineData("run-knowledge")]
    [InlineData("create-knowledge-index")]
    [InlineData("test-knowledge-search")]
    public async Task CommandsHaveExplicitHelp(string command)
    {
        var output = new StringWriter();
        var app = new CliApplication(new(output, TextWriter.Null, new ProcessRunner(), new HttpClient()));
        Assert.Equal(0, await app.RunAsync([command, "--help"], default));
        Assert.Contains(command, output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexCreationPreviewDoesNotRequestCredentials(bool explicitPreview)
    {
        var output = new StringWriter();
        var app = new CliApplication(new(output, TextWriter.Null, new ProcessRunner(), new HttpClient()));
        var arguments = new List<string>
        {
            "create-knowledge-index", "--endpoint", "https://example.search.windows.net", "--dimensions", "1024"
        };
        if (explicitPreview) arguments.Add("--apply=false");
        Assert.Equal(0, await app.RunAsync(arguments.ToArray(), default));
        Assert.Contains("\"dimensions\":1024", output.ToString());
        Assert.Contains("Preview only", output.ToString());
    }

    [Theory]
    [InlineData("run-knowledge", "--urls", "http://0.0.0.0:5100")]
    [InlineData("run-knowledge", "--backend", "unknown")]
    [InlineData("create-knowledge-index", "--endpoint", "https://evil.test")]
    [InlineData("test-knowledge-search", "--endpoint", "https://evil.test")]
    [InlineData("test-knowledge-search", "--endpoint", "https://example.search.windows.net/path")]
    [InlineData("test-knowledge-search", "--endpoint", "https://example.search.windows.net?secret=value")]
    [InlineData("test-knowledge-search", "--endpoint", "https://example.search.windows.net:444")]
    public async Task InvalidRuntimeAndResourceTargetsAreRejected(string command, string option, string value)
    {
        var app = new CliApplication(new(TextWriter.Null, TextWriter.Null, new ProcessRunner(), new HttpClient()));
        Assert.Equal(2, await app.RunAsync([command, option, value], default));
    }

    [Fact]
    public async Task SearchReplayRejectsMissingOrNonObjectRequestBeforeRequestingCredentials()
    {
        var file = Path.GetTempFileName();
        try
        {
            var app = new CliApplication(new(TextWriter.Null, TextWriter.Null, new ProcessRunner(), new HttpClient()));
            await File.WriteAllTextAsync(file, "[]");
            Assert.Equal(2, await app.RunAsync(["test-knowledge-search", "--endpoint",
                "https://example.search.windows.net", "--request-file", file], default));
            File.Delete(file);
            Assert.Equal(2, await app.RunAsync(["test-knowledge-search", "--endpoint",
                "https://example.search.windows.net", "--request-file", file], default));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
