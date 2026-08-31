using System.Net;
using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public class CliApplicationTests
{
    [Fact]
    public async Task HelpListsTheReplacementCommands()
    {
        var (application, output, _) = CreateApplication();

        var exitCode = await application.RunAsync(["--help"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("register-app", output.ToString());
        Assert.Contains("register-spa", output.ToString());
        Assert.Contains("test-foundry", output.ToString());
        Assert.Contains("enable-foundry-a2a", output.ToString());
        Assert.Contains("configure-foundry-chain", output.ToString());

        var commandOutput = new StringWriter();
        var applicationWithCommandOutput = new CliApplication(
            new CliContext(commandOutput, TextWriter.Null, new ProcessRunner(), new HttpClient()));
        await applicationWithCommandOutput.RunAsync(
            ["register-spa", "--help"], CancellationToken.None);
        Assert.Contains("--api-client-id", commandOutput.ToString());
    }

    [Fact]
    public void ConfigureFoundryChainPreservesExistingToolsAndInstructions()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "kind": "prompt",
              "model": "gpt-chat-latest",
              "instructions": "Use web search when it helps.",
              "tools": [{ "type": "web_search" }]
            }
            """);

        var definition = FoundryCommands.BuildChainDefinition(
            document.RootElement,
            "/subscriptions/test/connections/reverser",
            "reverser-classic",
            "Reverser Classic");
        var updatedAgain = FoundryCommands.BuildChainDefinition(
            JsonDocument.Parse(definition.ToJsonString()).RootElement,
            "/subscriptions/test/connections/reverser",
            "reverser-classic",
            "Reverser Classic");

        var tools = updatedAgain["tools"]!.AsArray();
        Assert.Contains(tools, tool => tool!["type"]!.GetValue<string>() == "web_search");
        Assert.Single(
            tools,
            tool => tool!["type"]!.GetValue<string>() == "a2a_preview");
        var instructions = updatedAgain["instructions"]!.GetValue<string>();
        Assert.Contains("Use web search when it helps.", instructions);
        Assert.Equal(1, CountOccurrences(instructions, "foundry-copilot-a2a:reverser-classic"));
    }

    [Fact]
    public void ChainDefinitionRemovesToolsForDeletedConnections()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "kind": "prompt",
              "instructions": "Use web search when it helps.",
              "tools": [
                { "type": "web_search" },
                { "type": "a2a_preview", "project_connection_id": "/subscriptions/test/connections/deleted" },
                { "type": "a2a_preview", "project_connection_id": "/subscriptions/test/connections/kept" }
              ]
            }
            """);

        var definition = FoundryCommands.BuildChainDefinition(
            document.RootElement,
            "/subscriptions/test/connections/reverser",
            "reverser-classic",
            "Reverser Classic",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "/subscriptions/test/connections/kept",
                "/subscriptions/test/connections/reverser"
            });

        var tools = definition["tools"]!.AsArray();
        var connections = tools
            .Where(tool => tool!["type"]!.GetValue<string>() == "a2a_preview")
            .Select(tool => tool!["project_connection_id"]!.GetValue<string>())
            .ToArray();

        // The dangling reference to a deleted connection must not survive, otherwise the agent
        // keeps an A2A tool it can never successfully call.
        Assert.DoesNotContain("/subscriptions/test/connections/deleted", connections);
        Assert.Contains("/subscriptions/test/connections/kept", connections);
        Assert.Contains("/subscriptions/test/connections/reverser", connections);
        Assert.Contains(tools, tool => tool!["type"]!.GetValue<string>() == "web_search");
    }

    [Fact]
    public void ChainBaseUrlSupportsSiblingCardAndRuntimePaths()
    {
        var url = FoundryCommands.BuildChainBaseUrl(
            "https://adapter.example/",
            "reverser classic");

        Assert.Equal(
            "https://adapter.example/a2a-agents/reverser%20classic/",
            url);
        Assert.Equal(
            "https://adapter.example/a2a-agents/reverser%20classic/a2a",
            $"{url}a2a");
    }

    [Fact]
    public async Task RemoteA2AConnectionUsesExplicitArmCoordinatesAndUserTokenAuth()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = Clone(request);
            return Json("""{"properties":{"provisioningState":"Succeeded"}}""");
        });
        using var httpClient = new HttpClient(handler);

        await FoundryCommands.PutRemoteA2AConnectionAsync(
            httpClient,
            "arm-token",
            "subscription",
            "resource group",
            "foundry-account",
            "foundry-project",
            "copilot-a2a",
            "https://adapter.example/a2a-agents/reverser",
            "api://adapter",
            ChainAuthenticationMode.UserEntraToken,
            "tenant",
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Put, capturedRequest.Method);
        Assert.Equal(
            "https://management.azure.com/subscriptions/subscription/resourceGroups/" +
            "resource%20group/providers/Microsoft.CognitiveServices/accounts/foundry-account/" +
            "projects/foundry-project/connections/copilot-a2a?api-version=2025-04-01-preview",
            capturedRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("arm-token", capturedRequest.Headers.Authorization.Parameter);

        using var body = JsonDocument.Parse(
            await capturedRequest.Content!.ReadAsStringAsync());
        var properties = body.RootElement.GetProperty("properties");
        Assert.Equal("UserEntraToken", properties.GetProperty("authType").GetString());
        Assert.Equal("RemoteA2A", properties.GetProperty("category").GetString());
        Assert.Equal("api://adapter", properties.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task RemoteA2AOAuthConnectionUsesIdentityPassthroughAndReturnsRedirectUrl()
    {
       HttpRequestMessage? capturedRequest = null;
       var handler = new StubHttpMessageHandler(request =>
       {
           capturedRequest = Clone(request);
           return Json(
               """{"properties":{"provisioningState":"Succeeded","redirectUrl":"https://consent.example/redirect"}}""");
       });
       using var httpClient = new HttpClient(handler);

       var redirectUrl = await FoundryCommands.PutRemoteA2AConnectionAsync(
           httpClient,
           "arm-token",
           "subscription",
           "resource-group",
           "account",
           "project",
           "a2a-reverser",
           "https://adapter.example/a2a-agents/reverser/a2a",
           "api://adapter",
           ChainAuthenticationMode.OAuth,
           "tenant-id",
           "oauth-client",
           "oauth-secret",
           CancellationToken.None);

       Assert.Equal("https://consent.example/redirect", redirectUrl);
       using var body = JsonDocument.Parse(
           await capturedRequest!.Content!.ReadAsStringAsync());
       var properties = body.RootElement.GetProperty("properties");
       Assert.Equal("OAuth2", properties.GetProperty("authType").GetString());
       Assert.Equal(
           "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/authorize",
           properties.GetProperty("authorizationUrl").GetString());
       Assert.Equal(
           ["api://adapter/access_as_user", "offline_access"],
           properties.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()));
       Assert.Equal(
           "oauth-client",
           properties.GetProperty("credentials").GetProperty("clientId").GetString());
       Assert.Equal(
           "oauth-secret",
           properties.GetProperty("credentials").GetProperty("clientSecret").GetString());
    }

    [Fact]
    public async Task ArmCreatedOAuthConnectionIsRejected()
    {
        // An ARM PUT leaves metadata empty; only a portal-created connection carries
        // type "custom_A2A". listConsentLinks cannot be used here because it reports
        // ConnectorNamespaceConnectionNotFound for working connections too.
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"properties":{"authType":"OAuth2","metadata":{}}}"""));
        using var httpClient = new HttpClient(handler);

        var provisioned = await FoundryCommands.IsPortalProvisionedOAuthConnectionAsync(
            httpClient,
            "arm-token",
            "/subscriptions/s/resourceGroups/r/providers/Microsoft.CognitiveServices/" +
            "accounts/a/projects/p/connections/a2a-reverser",
            CancellationToken.None);

        Assert.False(provisioned);
        Assert.Contains("Foundry portal", FoundryCommands.ConnectorGatewayMissingMessage);
        Assert.Contains("--reuse-connection", FoundryCommands.ConnectorGatewayMissingMessage);
    }

    [Fact]
    public async Task PortalCreatedOAuthConnectionIsAccepted()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"properties":{"authType":"OAuth2","metadata":{"type":"custom_A2A","oAuthProvider":"custom"}}}"""));
        using var httpClient = new HttpClient(handler);

        var provisioned = await FoundryCommands.IsPortalProvisionedOAuthConnectionAsync(
            httpClient,
            "arm-token",
            "/subscriptions/s/resourceGroups/r/providers/Microsoft.CognitiveServices/" +
            "accounts/a/projects/p/connections/a2a-reverser",
            CancellationToken.None);

        Assert.True(provisioned);
    }

    [Theory]
    [InlineData("reverser-classic", "a2a-reverser-classic")]
    [InlineData(
       "this-is-a-very-long-target-agent-identifier-that-needs-truncation",
       "a2a-this-is-a-very-long-ta-99983f")]
    public void DefaultConnectionNameIsValidAndDeterministic(string targetId, string expected)
    {
       var name = FoundryCommands.BuildDefaultConnectionName(targetId);

       Assert.Equal(expected, name);
       Assert.InRange(name.Length, 3, 33);
    }

    [Fact]
    public void UserTokenPreflightProvidesConsentRemediation()
    {
       var message = FoundryCommands.UserTokenFailureMessage(
           "AADSTS65001: The user or administrator has not consented.");

       Assert.Contains("Preauthorize Azure CLI application", message);
       Assert.Contains("--auth-mode oauth", message);
    }

    [Theory]
    [InlineData("eyJheader.payload.signature", "eyJheader.payload.signature")]
    [InlineData("""{"token":"eyJheader.payload.signature"}""", "eyJheader.payload.signature")]
    public void AzdAccessTokenSupportsCurrentAndStructuredOutput(
        string output,
        string expected)
    {
        Assert.Equal(expected, FoundryCommands.ParseAzdAccessToken(output));
    }

    [Fact]
    public async Task EnableFoundryA2AHelpRequiresExplicitCardFields()
    {
        var (application, output, _) = CreateApplication();

        var exitCode = await application.RunAsync(
            ["enable-foundry-a2a", "--help"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("--description", output.ToString());
        Assert.Contains("--skill-description", output.ToString());
        Assert.Contains("--replace-card", output.ToString());
        Assert.Contains("--smoke-prompt", output.ToString());
    }

    [Fact]
    public async Task RunAdapterDoesNotAcceptASecretOnTheCommandLine()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "run-adapter",
                "--tenant-id", "tenant",
                "--client-id", "client",
                "--direct-connect-url", "https://example.test/conversations",
                "--client-secret", "observable-secret"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown option '--client-secret'", error.ToString());
        Assert.DoesNotContain("observable-secret", error.ToString());
    }

    [Fact]
    public async Task TestAdapterExercisesCardAndA2AMessage()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(Clone(request));
            return request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/agent-card.json" => Json(
                    """{"name":"Specialist Agent Router"}"""),
                "/a2a/copilot-studio" => Json(
                    """{"result":{"message":{"parts":[{"text":"mock-copilot-studio"}]}}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var (application, output, error) = CreateApplication(handler);

        var exitCode = await application.RunAsync(
            ["test-adapter", "--base-url", "https://adapter.example"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains("smoke test passed", output.ToString());
        Assert.Equal(2, requests.Count);
        Assert.Equal("1.0", requests[1].Headers.GetValues("A2A-Version").Single());
        Assert.Contains("SendMessage", await requests[1].Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task HttpTimeoutReturnsACleanError()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new TaskCanceledException("timed out"));
        var (application, _, error) = CreateApplication(handler);

        var exitCode = await application.RunAsync(
            ["test-adapter"],
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal($"Error: The operation timed out.{Environment.NewLine}", error.ToString());
    }

    private static (
        CliApplication Application,
        StringWriter Output,
        StringWriter Error) CreateApplication(HttpMessageHandler? handler = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        var application = new CliApplication(
            new CliContext(output, error, new ProcessRunner(), httpClient));
        return (application, output, error);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                Encoding.UTF8,
                request.Content.Headers.ContentType?.MediaType ?? "text/plain");
        }

        return clone;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
