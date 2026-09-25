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
        Assert.Contains("register-consent-bypass-app", output.ToString());
        Assert.Contains("test-foundry", output.ToString());
        Assert.Contains("enable-foundry-a2a", output.ToString());
        Assert.Contains("configure-foundry-chain", output.ToString());
        Assert.Contains("get-connector-consent-bypass", output.ToString());
        Assert.Contains("set-connector-consent-bypass", output.ToString());
        Assert.Contains("register-hub", output.ToString());
        Assert.Contains("grant-hub-access", output.ToString());

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
           if (request.Method == HttpMethod.Get)
           {
               return new HttpResponseMessage(HttpStatusCode.NotFound);
           }
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
       Assert.Equal("RemoteA2A", properties.GetProperty("category").GetString());
       Assert.Equal("ServicesAndApps", properties.GetProperty("group").GetString());
       Assert.True(properties.GetProperty("isSharedToAll").GetBoolean());
       Assert.Empty(properties.GetProperty("sharedUserList").EnumerateArray());
       Assert.Equal("Azure", properties.GetProperty("metadata").GetProperty("ApiType").GetString());
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
    public async Task MetadataLessOAuthConnectionIsRejected()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            """{"properties":{"authType":"OAuth2","metadata":{}}}"""));
        using var httpClient = new HttpClient(handler);

        var hasMetadata = await FoundryCommands.HasNativeOAuthConnectionMetadataAsync(
            httpClient,
            "arm-token",
            "/subscriptions/s/resourceGroups/r/providers/Microsoft.CognitiveServices/" +
            "accounts/a/projects/p/connections/a2a-reverser",
            CancellationToken.None);

        Assert.False(hasMetadata);
        Assert.Contains("Foundry portal", FoundryCommands.ConnectorGatewayMissingMessage);
        Assert.Contains("--reuse-connection", FoundryCommands.ConnectorGatewayMissingMessage);
    }

    [Theory]
    [InlineData("""{"type":"custom_A2A","oAuthProvider":"custom"}""")]
    [InlineData("""{"ApiType":"Azure"}""")]
    public async Task NativeOAuthConnectionMetadataIsRecognized(string metadata)
    {
        var handler = new StubHttpMessageHandler(_ => Json(JsonSerializer.Serialize(new
        {
            properties = new { authType = "OAuth2", metadata = JsonSerializer.Deserialize<JsonElement>(metadata) }
        })));
        using var httpClient = new HttpClient(handler);

        var hasMetadata = await FoundryCommands.HasNativeOAuthConnectionMetadataAsync(
            httpClient,
            "arm-token",
            "/subscriptions/s/resourceGroups/r/providers/Microsoft.CognitiveServices/" +
            "accounts/a/projects/p/connections/a2a-reverser",
            CancellationToken.None);

        Assert.True(hasMetadata);
    }

    [Fact]
    public async Task ConnectionReadFailureIsNotMisreportedAsMissingMetadata()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<CliException>(() =>
            FoundryCommands.HasNativeOAuthConnectionMetadataAsync(
                httpClient,
                "arm-token",
                "/subscriptions/s/resourceGroups/r/providers/Microsoft.CognitiveServices/" +
                "accounts/a/projects/p/connections/a2a-reverser",
                CancellationToken.None));

        Assert.Contains("HTTP 403", exception.Message);
        Assert.Contains("No agent definition was changed", exception.Message);
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
    public async Task RegisterHubRejectsANonGuidAdapterClientId()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            ["register-hub", "--api-client-id", "not-a-guid"],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must be an application (client) ID", error.ToString());
    }

    [Fact]
    public async Task RegisterHubRejectsANonGuidManagedIdentityPrincipal()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "register-hub",
                "--api-client-id", "11111111-1111-1111-1111-111111111111",
                "--managed-identity-principal-id", "not-a-guid"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("object (principal) ID", error.ToString());
    }

    [Fact]
    public async Task RegisterOAuthClientRejectsANonGuidApiClientId()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "register-oauth-client",
                "--api-client-id", "not-a-guid",
                "--display-name", "fca2a-dev-agent-oauth-client"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must be an application (client) ID", error.ToString());
    }

    [Fact]
    public async Task AddFederatedCredentialRejectsANonGuidPrincipal()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "add-federated-credential",
                "--client-id", "11111111-1111-1111-1111-111111111111",
                "--principal-id", "not-a-guid"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must be an application (client) ID", error.ToString());
    }

    [Fact]
    public async Task RegisterWebRedirectRejectsHttpCallbacks()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "register-web-redirect",
                "--client-id", "11111111-1111-1111-1111-111111111111",
                "--redirect-uri", "http://callback.example/redirect"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must be an HTTPS URL", error.ToString());
    }

    [Fact]
    public async Task RegisterHubDoesNotAcceptAClientSecret()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "register-hub",
                "--api-client-id", "11111111-1111-1111-1111-111111111111",
                "--client-secret", "observable-secret"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown option '--client-secret'", error.ToString());
        Assert.DoesNotContain("observable-secret", error.ToString());
    }

    [Fact]
    public async Task GrantHubAccessRejectsTheSameApplicationTwice()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "grant-hub-access",
                "--client-id", "11111111-1111-1111-1111-111111111111",
                "--hub-client-id", "11111111-1111-1111-1111-111111111111"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must be different applications", error.ToString());
    }

    [Fact]
    public async Task ConfigureHubRejectsTheSameHubAndAdapterApplication()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "configure-hub",
                "--subscription-id", "00000000-0000-0000-0000-000000000000",
                "--resource-group", "rg",
                "--service-name", "apim",
                "--backend-url", "https://adapter.example",
                "--tenant-id", "tenant",
                "--hub-client-id", "11111111-1111-1111-1111-111111111111",
                "--adapter-client-id", "11111111-1111-1111-1111-111111111111"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must be different applications", error.ToString());
    }

    [Fact]
    public async Task ConfigureHubDoesNotAcceptAClientSecret()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "configure-hub",
                "--subscription-id", "00000000-0000-0000-0000-000000000000",
                "--resource-group", "rg",
                "--service-name", "apim",
                "--backend-url", "https://adapter.example",
                "--tenant-id", "tenant",
                "--hub-client-id", "11111111-1111-1111-1111-111111111111",
                "--adapter-client-id", "22222222-2222-2222-2222-222222222222",
                "--client-secret", "observable-secret"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown option '--client-secret'", error.ToString());
        Assert.DoesNotContain("observable-secret", error.ToString());
    }

    [Fact]
    public async Task ConfigureHubRequiresEverySwitchableBackendOption()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "configure-hub",
                "--subscription-id", "00000000-0000-0000-0000-000000000000",
                "--resource-group", "rg",
                "--service-name", "apim",
                "--app-service-backend-url", "https://handler.azurewebsites.net",
                "--dev-tunnel-backend-url", "https://handler-5099.euw.devtunnels.ms",
                "--tenant-id", "tenant",
                "--hub-client-id", "11111111-1111-1111-1111-111111111111",
                "--adapter-client-id", "22222222-2222-2222-2222-222222222222"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("require", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--backend-mode", error.ToString());
    }

    [Fact]
    public async Task ConfigureHubRejectsANonDevTunnelBackend()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "configure-hub",
                "--subscription-id", "00000000-0000-0000-0000-000000000000",
                "--resource-group", "rg",
                "--service-name", "apim",
                "--app-service-backend-url", "https://handler.azurewebsites.net",
                "--dev-tunnel-backend-url", "https://not-a-tunnel.example",
                "--backend-mode", "devtunnel",
                "--tenant-id", "tenant",
                "--hub-client-id", "11111111-1111-1111-1111-111111111111",
                "--adapter-client-id", "22222222-2222-2222-2222-222222222222"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("devtunnels.ms", error.ToString());
    }

    [Fact]
    public async Task SetHubBackendRejectsAnUnknownMode()
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            [
                "set-hub-backend",
                "--subscription-id", "00000000-0000-0000-0000-000000000000",
                "--resource-group", "rg",
                "--service-name", "apim",
                "--backend-mode", "random"
            ],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("appservice", error.ToString());
        Assert.Contains("devtunnel", error.ToString());
    }

    [Fact]
    public void HubExchangePolicyIsolatesTheCacheByCallerAndTargetsTheAdapterScope()
    {
        var policy = HubCommands.BuildExchangePolicy(
            "tenant-id",
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            identityClientId: null,
            runtime: true);

        Assert.Contains("api://11111111-1111-1111-1111-111111111111", policy);
        Assert.Contains(
            "api://22222222-2222-2222-2222-222222222222/access_as_user", policy);
        Assert.Contains("requested_token_use=on_behalf_of", policy);
        // A cache key without the caller identity would return one user's token to another.
        Assert.Contains("jwt.Claims.GetValueOrDefault(\"oid\", \"unknown\")", policy);
        Assert.Contains("caching-type=\"internal\"", policy);
        // No secret may appear in a gateway policy.
        Assert.DoesNotContain("client_secret", policy);
        Assert.Contains("api://AzureADTokenExchange", policy);
        Assert.DoesNotContain("__IDENTITY_CLIENT_ID__", policy);
        Assert.DoesNotContain("__REQUEST_VALIDATION__", policy);
    }

    [Fact]
    public void HubExchangePolicyUsesTheSuppliedUserAssignedIdentity()
    {
        var policy = HubCommands.BuildExchangePolicy(
            "tenant-id",
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            identityClientId: "33333333-3333-3333-3333-333333333333",
            runtime: false);

        Assert.Contains("client-id=\"33333333-3333-3333-3333-333333333333\"", policy);
    }

    [Fact]
    public void SwitchableHubPolicyUsesNamedValuesAndTunnelProtection()
    {
        var policy = HubCommands.BuildSwitchableApiPolicy(
            "copilot-studio-hub",
            ["http://localhost:5173"]);

        Assert.Contains("{{copilot-studio-hub-backend-mode}}", policy);
        Assert.Contains("{{copilot-studio-hub-app-service-backend-url}}", policy);
        Assert.Contains("{{copilot-studio-hub-dev-tunnel-backend-url}}", policy);
        Assert.Contains("X-Tunnel-Skip-AntiPhishing-Page", policy);
        Assert.Contains("<origin>http://localhost:5173</origin>", policy);
        Assert.DoesNotContain("__BACKEND_HEADERS__", policy);
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

    [Theory]
    [InlineData("bad id")]
    [InlineData("-bad")]
    [InlineData("bad-")]
    [InlineData(".bad")]
    [InlineData("bad.")]
    [InlineData("bad..euw")]
    [InlineData("xy")]
    public async Task StartTunnelRejectsInvalidPersistentTunnelIds(string tunnelId)
    {
        var (application, _, error) = CreateApplication();

        var exitCode = await application.RunAsync(
            ["start-tunnel", "--port", "5099", "--tunnel-id", tunnelId],
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("--tunnel-id", error.ToString());
    }

    [Theory]
    [InlineData("""{"ports":[{"portNumber":5099,"protocol":"http"}]}""", true)]
    [InlineData("""{"ports":[{"portNumber":5173,"protocol":"http"}]}""", false)]
    [InlineData("""{"ports":[]}""", false)]
    public void PersistentTunnelPortListIsParsed(string json, bool expected)
    {
        Assert.Equal(expected, RuntimeCommands.ContainsTunnelPort(json, 5099));
    }

    [Fact]
    public void InvalidPersistentTunnelPortListIsRejected()
    {
        var exception = Assert.Throws<CliException>(
            () => RuntimeCommands.ContainsTunnelPort("""{"unexpected":[]}""", 5099));

        Assert.Contains("invalid port-list", exception.Message);
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
