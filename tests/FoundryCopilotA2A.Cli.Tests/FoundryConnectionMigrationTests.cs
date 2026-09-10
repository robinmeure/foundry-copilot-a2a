using System.Net;
using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class FoundryConnectionMigrationTests
{
    private const string ConnectionRoot =
        "/subscriptions/test/resourceGroups/group/providers/Microsoft.CognitiveServices/" +
        "accounts/account/projects/project/connections";
    private const string Target =
        "https://gateway.example/copilot-studio-local/a2a-agents/specialist/a2a";
    private const string Scope = "api://backend/access_as_user";

    [Theory]
    [InlineData("--reuse-connection", null)]
    [InlineData("--replace-connection-name", "old")]
    [InlineData("--smoke-prompt", "hello")]
    public async Task PreparationCannotPublishReplaceOrSmokeAnAgent(string option, string? value)
    {
        var arguments = new List<string>
        {
            "--agent-url", "https://account.services.ai.azure.com/api/projects/project/agents/orchestrator",
            "--adapter-url", "https://gateway.example/copilot-studio-local",
            "--audience", "api://backend", "--tenant-id", "tenant",
            "--subscription-id", "subscription", "--resource-group", "group",
            "--account-name", "account", "--project-name", "project",
            "--target-agent-id", "specialist", "--target-agent-name", "Specialist",
            "--prepare-connection", option
        };
        if (value is not null)
        {
            arguments.Add(value);
        }
        using var handler = new CitadelTestHandler(_ =>
            throw new InvalidOperationException("No remote operation may run for this invalid combination."));
        using var client = new HttpClient(handler);
        var context = new CliContext(TextWriter.Null, TextWriter.Null, new ProcessRunner(), client);

        var exception = await Assert.ThrowsAsync<CliException>(() =>
            FoundryCommands.ConfigureChainAsync(
                context, CommandArguments.Parse(arguments), CancellationToken.None));

        Assert.Contains("--prepare-connection", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OAuthCreationNeverOverwritesAnExistingOrUnreadableConnection(HttpStatusCode status)
    {
        var requests = new List<HttpMethod>();
        using var handler = new CitadelTestHandler(request =>
        {
            requests.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(status));
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<CliException>(() => FoundryCommands.PutRemoteA2AConnectionAsync(
            client, "arm-token", "test", "group", "account", "project", "existing",
            Target, "api://backend", ChainAuthenticationMode.OAuth,
            "tenant", "backend", "secret", CancellationToken.None));

        Assert.Equal([HttpMethod.Get], requests);
    }

    [Theory]
    [InlineData(Target, "OAuth2", true, Scope, true)]
    [InlineData(Target + "/", "OAuth2", true, Scope, true)]
    [InlineData("https://tunnel.example/a2a-agents/specialist/a2a", "OAuth2", true, Scope, false)]
    [InlineData(Target, "ProjectManagedIdentity", true, Scope, false)]
    [InlineData(Target, "OAuth2", false, Scope, false)]
    [InlineData(Target, "OAuth2", true, "api://different/access_as_user", false)]
    public async Task ReusingAConnectionRequiresTheActualGatewayAndDelegatedConfiguration(
        string target, string authType, bool portalProvisioned, string scope, bool accepted)
    {
        var calls = 0;
        using var handler = new CitadelTestHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("arm-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(CitadelCommandsTests.Json(new
            {
                properties = new
                {
                    target,
                    authType,
                    category = "RemoteA2A",
                    scopes = new[] { scope, "offline_access" },
                    metadata = portalProvisioned
                        ? new Dictionary<string, string> { ["type"] = "custom_A2A" }
                        : []
                }
            }));
        });
        using var client = new HttpClient(handler);

        var action = () => FoundryCommands.ValidateReusableConnectionAsync(
            client, "arm-token", $"{ConnectionRoot}/new", Target, "api://backend",
            ChainAuthenticationMode.OAuth, CancellationToken.None);
        if (accepted)
        {
            await action();
        }
        else
        {
            await Assert.ThrowsAsync<CliException>(action);
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public void MigrationReplacesOnlyTheSelectedToolAndPinsTheGatewayUrl()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            kind = "prompt",
            model = "existing-model",
            instructions = "Keep my routing instructions.",
            tools = new object[]
            {
                new { type = "web_search" },
                new { type = "a2a_preview", project_connection_id = $"{ConnectionRoot}/old" },
                new { type = "a2a_preview", project_connection_id = $"{ConnectionRoot}/unrelated" }
            }
        }));
        var inventory = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{ConnectionRoot}/old", $"{ConnectionRoot}/new", $"{ConnectionRoot}/unrelated"
        };
        var definition = FoundryCommands.BuildChainDefinition(
            document.RootElement, $"{ConnectionRoot}/new", "specialist", "Specialist",
            inventory, Target, $"{ConnectionRoot}/old");
        using var updated = JsonDocument.Parse(definition.ToJsonString());
        var repeated = FoundryCommands.BuildChainDefinition(
            updated.RootElement, $"{ConnectionRoot}/new", "specialist", "Specialist",
            inventory, Target, $"{ConnectionRoot}/old");
        var tools = repeated["tools"]!.AsArray();

        Assert.Equal(definition.ToJsonString(), repeated.ToJsonString());
        Assert.Equal("existing-model", repeated["model"]!.GetValue<string>());
        Assert.StartsWith("Keep my routing instructions.", repeated["instructions"]!.GetValue<string>());
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, tool => tool!["type"]!.GetValue<string>() == "web_search");
        Assert.Contains(tools, tool =>
            tool!["project_connection_id"]?.GetValue<string>() == $"{ConnectionRoot}/unrelated");
        Assert.DoesNotContain(tools, tool =>
            tool!["project_connection_id"]?.GetValue<string>() == $"{ConnectionRoot}/old");
        var gatewayTool = Assert.Single(tools, tool =>
            tool!["project_connection_id"]?.GetValue<string>() == $"{ConnectionRoot}/new");
        Assert.Equal(Target, gatewayTool!["base_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConnectionInventoryIncludesEveryPageBeforePruning()
    {
        var calls = 0;
        using var handler = new CitadelTestHandler(request =>
        {
            calls++;
            Assert.Equal("management.azure.com", request.RequestUri!.Host);
            Assert.Equal("arm-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(CitadelCommandsTests.Json(new
            {
                value = new[] { new { id = $"{ConnectionRoot}/connection-{calls}" } },
                nextLink = calls == 1
                    ? $"https://management.azure.com{ConnectionRoot}?api-version=2025-06-01&skip=2"
                    : null
            }));
        });
        using var client = new HttpClient(handler);

        var inventory = await ListAsync(client);

        Assert.Equal(2, calls);
        Assert.Equal(2, inventory.Count);
        Assert.Contains($"{ConnectionRoot}/connection-2", inventory);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ConnectionListFailureNeverLooksLikeAnEmptyProject(HttpStatusCode status)
    {
        using var handler = new CitadelTestHandler(_ =>
            Task.FromResult(new HttpResponseMessage(status)));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<CliException>(() => ListAsync(client));
    }

    [Theory]
    [InlineData("https://untrusted.example/connections")]
    [InlineData("https://management.azure.com/subscriptions/different/connections")]
    [InlineData("https://management.azure.com" + ConnectionRoot + "?api-version=2025-06-01")]
    public async Task InvalidPaginationStopsBeforeForwardingAnotherCredential(string nextLink)
    {
        var calls = 0;
        using var handler = new CitadelTestHandler(_ =>
        {
            calls++;
            return Task.FromResult(CitadelCommandsTests.Json(new
            {
                value = Array.Empty<object>(),
                nextLink
            }));
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<CliException>(() => ListAsync(client));
        Assert.Equal(1, calls);
    }

    private static Task<HashSet<string>> ListAsync(HttpClient client) =>
        FoundryCommands.ListProjectConnectionIdsAsync(
            client, "arm-token", "test", "group", "account", "project", CancellationToken.None);
}
