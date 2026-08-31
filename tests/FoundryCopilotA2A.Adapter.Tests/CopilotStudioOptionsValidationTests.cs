namespace FoundryCopilotA2A.Adapter.Tests;

using Microsoft.Extensions.Options;

/// <summary>
/// Copilot Studio hands makers a direct connection URL, and the adapter must accept it as a
/// complete way to address an agent. An earlier version demanded EnvironmentId and SchemaName
/// unconditionally, which refused to start against a real agent even though the invoker already
/// supported the URL. These tests pin both addressing modes.
/// </summary>
public class CopilotStudioOptionsValidationTests
{
    private const string DirectConnectUrl =
        "https://contoso.crm.dynamics.com/copilotstudio/dataverse-backed/authenticated/bots/x/conversations";

    private static CopilotStudioOptions WithCredentials() => new()
    {
        TenantId = "11111111-2222-3333-4444-555555555555",
        ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        ClientSecret = "not-a-real-secret"
    };

    [Fact]
    public void DirectConnectUrlAloneIsSufficient()
    {
        var options = WithCredentials();
        options.DirectConnectUrl = DirectConnectUrl;

        var exception = Record.Exception(options.Validate);

        Assert.Null(exception);
    }

    [Fact]
    public void EnvironmentAndSchemaNameAreSufficient()
    {
        var options = WithCredentials();
        options.EnvironmentId = "Default-11111111-2222-3333-4444-555555555555";
        options.SchemaName = "cr123_agent";

        var exception = Record.Exception(options.Validate);

        Assert.Null(exception);
    }

    [Fact]
    public void NeitherAddressingModeIsRejected()
    {
        var options = WithCredentials();

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("DirectConnectUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialDiscoveryConfigurationIsRejected()
    {
        var options = WithCredentials();
        options.EnvironmentId = "Default-11111111-2222-3333-4444-555555555555";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void MultipleNamedAgentsCanShareCredentials()
    {
        var options = WithCredentials();
        options.DefaultAgent = "primary";
        options.Agents =
            new Dictionary<string, CopilotStudioAgentOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["primary"] = new()
                {
                    DisplayName = "Primary",
                    DirectConnectUrl = DirectConnectUrl
                },
                ["reverser"] = new()
                {
                    DisplayName = "Reverser",
                    DirectConnectUrl = DirectConnectUrl.Replace("/x/", "/reverser/")
                }
            };

        var exception = Record.Exception(options.Validate);
        var agents = options.ResolveAgents();

        Assert.Null(exception);
        Assert.Equal(2, agents.Count);
        Assert.Contains(agents, agent => agent.Id == "reverser");
    }

    [Fact]
    public void MissingDefaultNamedAgentIsRejected()
    {
        var options = WithCredentials();
        options.DefaultAgent = "missing";
        options.Agents["configured"] = new()
        {
            DisplayName = "Configured",
            DirectConnectUrl = DirectConnectUrl
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("DefaultAgent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationKeyAliasCanExposeAHyphenatedAgentId()
    {
        var options = WithCredentials();
        options.DefaultAgent = "reverser-classic";
        options.Agents["reverser_classic"] = new()
        {
            Id = "reverser-classic",
            DisplayName = "Reverser Classic",
            DirectConnectUrl = DirectConnectUrl
        };

        var exception = Record.Exception(options.Validate);
        var agent = Assert.Single(options.ResolveAgents());

        Assert.Null(exception);
        Assert.Equal("reverser-classic", agent.Id);
    }

    [Fact]
    public void GitHubCopilotHarnessAgentIsExposedAsUnsupportedAndRejectedBeforeInvocation()
    {
        var options = WithCredentials();
        options.DefaultAgent = "classic";
        options.Agents["classic"] = new()
        {
            DisplayName = "Classic",
            DirectConnectUrl = DirectConnectUrl
        };
        options.Agents["new-agent"] = new()
        {
            DisplayName = "New agent",
            DirectConnectUrl = DirectConnectUrl.Replace("/x/", "/new/"),
            Harness = CopilotStudioHarness.GitHubCopilot
        };
        var catalog = new AgentCatalog(
            Options.Create(new AdapterOptions { Backend = "CopilotStudio" }),
            Options.Create(options),
            Options.Create(new FoundryOptions()));

        var descriptor = Assert.Single(catalog.Agents, agent => agent.Id == "new-agent");
        Assert.False(descriptor.Supported);
        Assert.Contains("standard-harness agent", descriptor.StatusMessage);
        Assert.Throws<AdapterRequestException>(() => catalog.ResolveAgentId("new-agent"));
        Assert.Equal("classic", catalog.ResolveAgentId(null));
    }

    [Theory]
    [InlineData("", "client", "secret")]
    [InlineData("tenant", "", "secret")]
    [InlineData("tenant", "client", "")]
    public void MissingCredentialsAreRejectedEvenWithAValidAddress(
        string tenantId, string clientId, string clientSecret)
    {
        var options = new CopilotStudioOptions
        {
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = clientSecret,
            DirectConnectUrl = DirectConnectUrl
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
