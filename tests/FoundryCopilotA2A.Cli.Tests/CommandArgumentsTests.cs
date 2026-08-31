namespace FoundryCopilotA2A.Cli.Tests;

public class CommandArgumentsTests
{
    [Fact]
    public void ParsesSeparatedAndEqualsValues()
    {
        var arguments = CommandArguments.Parse(
            ["--tenant-id", "tenant", "--client-id=client"]);

        Assert.Equal("tenant", arguments.Require("tenant-id"));
        Assert.Equal("client", arguments.Require("client-id"));
    }

    [Fact]
    public void ParsesBooleanFlagWithoutAValue()
    {
        var arguments = CommandArguments.Parse(["--admin-consent"]);

        Assert.True(arguments.Flag("admin-consent"));
    }

    [Fact]
    public void ParsesExplicitFalseFlag()
    {
        var arguments = CommandArguments.Parse(["--admin-consent=false"]);

        Assert.False(arguments.Flag("admin-consent"));
    }

    [Fact]
    public void RejectsDuplicateOptions()
    {
        var exception = Assert.Throws<CliException>(
            () => CommandArguments.Parse(["--port", "1", "--port", "2"]));

        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void RejectsUnknownOptions()
    {
        var arguments = CommandArguments.Parse(["--typo", "value"]);

        var exception = Assert.Throws<CliException>(
            () => arguments.EnsureOnly("known"));

        Assert.Contains("--typo", exception.Message);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("ftp://example.test")]
    public void RejectsInvalidHttpUrls(string value)
    {
        var arguments = CommandArguments.Parse(["--url", value]);

        Assert.Throws<CliException>(() => arguments.AbsoluteHttpUri("url"));
    }

    [Fact]
    public void ParsesFoundryResponsesEndpoint()
    {
        var address = FoundryAgentAddress.Parse(new Uri(
            "https://account.services.ai.azure.com/api/projects/project/agents/" +
            "jolly-agent/endpoint/protocols/openai/responses"));

        Assert.Equal(
            "https://account.services.ai.azure.com/api/projects/project",
            address.ProjectEndpoint);
        Assert.Equal("jolly-agent", address.AgentName);
    }

    [Fact]
    public void RejectsUrlWithoutFoundryAgentPath()
    {
        Assert.Throws<CliException>(() =>
            FoundryAgentAddress.Parse(new Uri(
                "https://account.services.ai.azure.com/api/projects/project")));
    }

    [Fact]
    public void ResolvesExplicitAdapterProject()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(Path.GetFullPath(path), RepoPaths.ResolveAdapterProject(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WindowsAzureCliBatchFileResolvesToBundledPython()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"foundry-cli-test-{Guid.NewGuid():N}");
        var wbin = Path.Combine(root, "wbin");
        Directory.CreateDirectory(wbin);
        var launcher = Path.Combine(wbin, "az.cmd");
        var python = Path.Combine(root, "python.exe");
        File.WriteAllText(launcher, "@echo off");
        File.WriteAllText(python, string.Empty);

        try
        {
            var resolved = ExecutableResolver.Resolve(launcher);

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(python, resolved.FileName);
                Assert.Equal(["-IBm", "azure.cli"], resolved.PrefixArguments);
            }
            else
            {
                Assert.Equal(launcher, resolved.FileName);
                Assert.Empty(resolved.PrefixArguments);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryTextFileIsRemovedOnDispose()
    {
        string path;
        using (var file = await TemporaryTextFile.CreateAsync(
                   """{"safe":true}""", CancellationToken.None))
        {
            path = file.Path;
            Assert.True(File.Exists(path));
            Assert.StartsWith("@", file.AzureCliReference);
        }

        Assert.False(File.Exists(path));
    }
}
