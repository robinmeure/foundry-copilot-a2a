using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FoundryCopilotA2A.Cli;

internal static class FoundryCommands
{
    private const string ChainInstructionStart = "<!-- foundry-copilot-a2a:";
    private const string ChainInstructionEnd = "<!-- /foundry-copilot-a2a -->";
    private const string DefaultOAuthSecretEnvironmentVariable =
        "FOUNDRY_A2A_OAUTH_CLIENT_SECRET";
    private static readonly Regex ConnectionNamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{2,32}$",
        RegexOptions.CultureInvariant);

    public static async Task<int> EnableA2AAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "agent-url",
            "description",
            "skill-id",
            "skill-name",
            "skill-description",
            "card-version",
            "replace-card",
            "smoke-prompt",
            "help");

        var address = FoundryAgentAddress.Parse(
            arguments.AbsoluteHttpUri("agent-url"));
        var description = arguments.Require("description");
        var skillId = arguments.Require("skill-id");
        var skillName = arguments.Require("skill-name");
        var skillDescription = arguments.Require("skill-description");
        var cardVersion = arguments.Optional("card-version", "1.0")!;
        ValidateCardValue("skill-id", skillId, allowSpaces: false);
        ValidateCardValue("card-version", cardVersion, allowSpaces: false);

        var agentUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}?api-version=v1";
        using var existingAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Get,
            agentUrl,
            body: null,
            cancellationToken);

        if (HasAgentCard(existingAgent.RootElement) && !arguments.Flag("replace-card"))
        {
            throw new CliException(
                "The agent already has an agent card. Review it, then rerun with " +
                "--replace-card to confirm replacement.");
        }

        var patchBody = JsonSerializer.Serialize(new
        {
            agent_card = new
            {
                description,
                version = cardVersion,
                skills = new[]
                {
                    new
                    {
                        id = skillId,
                        name = skillName,
                        description = skillDescription
                    }
                }
            },
            agent_endpoint = new
            {
                protocols = new[] { "responses", "a2a" }
            }
        });

        using var updatedAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Patch,
            agentUrl,
            patchBody,
            cancellationToken);

        var cardUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            $"/endpoint/protocols/a2a/agentCard/v{cardVersion}";
        using var card = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Get,
            cardUrl,
            body: null,
            cancellationToken);

        ValidatePublishedCard(card.RootElement, description, skillId);

        var smokePrompt = arguments.Optional("smoke-prompt");
        if (smokePrompt is not null)
        {
            await SmokeTestAsync(context, address, smokePrompt, cancellationToken);
        }

        context.Out.WriteLine($"Enabled incoming A2A for '{address.AgentName}'.");
        context.Out.WriteLine($"Agent card: {cardUrl}");
        return 0;
    }

    public static async Task<int> ConfigureChainAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "agent-url",
            "adapter-url",
            "audience",
            "tenant-id",
            "subscription-id",
            "resource-group",
            "account-name",
            "project-name",
            "target-agent-id",
            "target-agent-name",
            "connection-name",
            "auth-mode",
            "oauth-client-id",
            "oauth-client-secret-env",
            "reuse-connection",
            "prepare-connection",
            "replace-connection-name",
            "smoke-prompt",
            "help");

        var address = FoundryAgentAddress.Parse(arguments.AbsoluteHttpUri("agent-url"));
        var adapterUrl = arguments
            .AbsoluteHttpUri("adapter-url")
            .AbsoluteUri.TrimEnd('/');
        var audience = arguments.Require("audience");
        if (!Uri.TryCreate(audience, UriKind.Absolute, out _))
        {
            throw new CliException("Option '--audience' must be an absolute Entra audience URI.");
        }

        var resourceGroup = arguments.Require("resource-group");
        var tenantId = arguments.Require("tenant-id");
        var subscriptionId = arguments.Require("subscription-id");
        var accountName = arguments.Require("account-name");
        var projectName = arguments.Require("project-name");
        var targetAgentId = arguments.Require("target-agent-id");
        var targetAgentName = arguments.Require("target-agent-name");
        ValidateCardValue("target-agent-id", targetAgentId, allowSpaces: false);
        var connectionName = arguments.Optional(
            "connection-name",
            BuildDefaultConnectionName(targetAgentId))!;
        ValidateConnectionName(connectionName);
        var replacedConnectionName = arguments.Optional("replace-connection-name");
        if (replacedConnectionName is not null)
        {
            ValidateConnectionName(replacedConnectionName);
            if (string.Equals(connectionName, replacedConnectionName, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException("--replace-connection-name must name a different connection.");
            }
        }
        var reuseConnection = arguments.Flag("reuse-connection");
        var prepareConnection = arguments.Flag("prepare-connection");
        if (prepareConnection &&
            (reuseConnection || replacedConnectionName is not null || arguments.Has("smoke-prompt")))
        {
            throw new CliException(
                "--prepare-connection creates a new connection without changing an agent. " +
                "Do not combine it with --reuse-connection, --replace-connection-name, or --smoke-prompt.");
        }
        var authMode = ParseAuthenticationMode(arguments.Optional("auth-mode", "oauth")!);
        var oauthClientId = arguments.Optional("oauth-client-id");
        var oauthClientSecret = default(string);
        if (!reuseConnection && authMode == ChainAuthenticationMode.OAuth)
        {
            if (string.IsNullOrWhiteSpace(oauthClientId))
            {
                throw new CliException(
                    "Option '--oauth-client-id' is required when '--auth-mode oauth' is used.");
            }

            var secretEnvironmentVariable = arguments.Optional(
                "oauth-client-secret-env",
                DefaultOAuthSecretEnvironmentVariable)!;
            ValidateEnvironmentVariableName(secretEnvironmentVariable);
            oauthClientSecret = Environment.GetEnvironmentVariable(secretEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(oauthClientSecret))
            {
                throw new CliException(
                    $"Environment variable '{secretEnvironmentVariable}' must contain the OAuth " +
                    "client secret. Secrets are not accepted on the command line.");
            }
        }

        var chainBaseUrl = BuildChainBaseUrl(adapterUrl, targetAgentId);
        var chainTargetUrl = $"{chainBaseUrl}a2a";
        await ValidateChainAgentCardAsync(
            context,
            chainBaseUrl,
            targetAgentName,
            cancellationToken);

        var armTokenResult = await context.Processes.CaptureAsync(
            "azd",
            [
                "auth", "token",
                "--tenant-id", tenantId,
                "--scope", "https://management.azure.com/.default",
                "--no-prompt"
            ],
            cancellationToken,
            new Dictionary<string, string?>
            {
                ["AZURE_DEV_USER_AGENT"] = "microsoft_foundry_skill"
            });
        var armToken = ParseAzdAccessToken(armTokenResult.StandardOutput);

        if (!reuseConnection)
        {
            var operation = authMode == ChainAuthenticationMode.OAuth
                ? "Creating"
                : "Creating or updating";
            context.Out.WriteLine(
                $"{operation} authenticated Foundry connection '{connectionName}'...");
            if (authMode == ChainAuthenticationMode.UserEntraToken)
            {
                await ValidateUserTokenAcquisitionAsync(
                    context,
                    tenantId,
                    audience,
                    cancellationToken);
            }

            var redirectUrl = await PutRemoteA2AConnectionAsync(
                context.HttpClient,
                armToken,
                subscriptionId,
                resourceGroup,
                accountName,
                projectName,
                connectionName,
                chainTargetUrl,
                audience,
                authMode,
                tenantId,
                oauthClientId,
                oauthClientSecret,
                cancellationToken);
            if (redirectUrl is not null)
            {
                context.Out.WriteLine($"OAuth redirect URL: {redirectUrl}");
                context.Out.WriteLine(
                    "Add this URL as a Web redirect URI on the OAuth app registration before " +
                    "authorizing the A2A tool in the Foundry playground.");
            }

            if (authMode == ChainAuthenticationMode.OAuth)
            {
                var provisionedConnectionId =
                    $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/" +
                    $"Microsoft.CognitiveServices/accounts/{accountName}/projects/{projectName}/" +
                    $"connections/{connectionName}";
                if (!await HasNativeOAuthConnectionMetadataAsync(
                        context.HttpClient,
                        armToken,
                        provisionedConnectionId,
                        cancellationToken))
                {
                    throw new CliException(ConnectorGatewayMissingMessage);
                }
            }
        }
        else
        {
            context.Out.WriteLine(
                $"Reusing existing Foundry connection '{connectionName}' without updating credentials.");
        }

        var connectionId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/" +
            $"Microsoft.CognitiveServices/accounts/{accountName}/projects/{projectName}/" +
            $"connections/{connectionName}";
        await ValidateReusableConnectionAsync(
            context.HttpClient,
            armToken,
            connectionId,
            chainTargetUrl,
            audience,
            authMode,
            cancellationToken);
        if (prepareConnection)
        {
            context.Out.WriteLine($"Prepared Foundry connection '{connectionName}' for {chainTargetUrl}.");
            context.Out.WriteLine(
                "No agent definition was changed. Register the returned OAuth callback on the existing " +
                "backend before attaching with --reuse-connection. Native user consent and an observed " +
                "specialist callback are still required.");
            return 0;
        }
        var replacedConnectionId = replacedConnectionName is null
            ? null
            : $"{connectionId[..(connectionId.LastIndexOf('/') + 1)]}{replacedConnectionName}";
        if (replacedConnectionId is not null)
        {
            await ValidateReplacementTargetAsync(
                context.HttpClient, armToken, replacedConnectionId, targetAgentId, cancellationToken);
        }
        var agentUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            "?api-version=v1";
        using var existingAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Get,
            agentUrl,
            body: null,
            cancellationToken);
        var latest = GetLatestVersion(existingAgent.RootElement);
        var existingConnectionIds = await ListProjectConnectionIdsAsync(
            context.HttpClient,
            armToken,
            subscriptionId,
            resourceGroup,
            accountName,
            projectName,
            cancellationToken);
        var definition = BuildChainDefinition(
            latest.GetProperty("definition"),
            connectionId,
            targetAgentId,
            targetAgentName,
            existingConnectionIds,
            targetUrl: chainTargetUrl,
            replacedConnectionId: replacedConnectionId);
        var body = new JsonObject
        {
            ["description"] = latest.TryGetProperty("description", out var description)
                ? description.GetString() ?? string.Empty
                : string.Empty,
            ["definition"] = definition
        };
        if (latest.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            body["metadata"] = JsonNode.Parse(metadata.GetRawText());
        }

        using var updatedAgent = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Post,
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            "/versions?api-version=v1",
            body.ToJsonString(),
            cancellationToken);
        ValidateConfiguredChain(updatedAgent.RootElement, connectionId);

        var smokePrompt = arguments.Optional("smoke-prompt");
        if (smokePrompt is not null)
        {
            await SmokeTestAsync(
                context,
                address,
                $"Delegate this request to {targetAgentName} through A2A: {smokePrompt}",
                cancellationToken);
        }

        var version = updatedAgent.RootElement.TryGetProperty("version", out var versionElement)
            ? versionElement.ToString()
            : "(unknown)";
        context.Out.WriteLine(
            $"Configured '{address.AgentName}' version {version} to call '{targetAgentName}' via A2A.");
        context.Out.WriteLine($"Remote A2A target: {chainTargetUrl}");
        if (replacedConnectionName is not null)
        {
            context.Out.WriteLine(
                $"Replaced this agent's '{replacedConnectionName}' tool reference; " +
                "the shared project connection was not deleted.");
        }
        return 0;
    }

    internal static async Task ValidateReusableConnectionAsync(
        HttpClient client,
        string accessToken,
        string connectionId,
        string expectedTarget,
        string audience,
        ChainAuthenticationMode authenticationMode,
        CancellationToken cancellationToken)
    {
        using var connection = await GetConnectionAsync(client, accessToken, connectionId, cancellationToken);
        var properties = connection.RootElement.GetProperty("properties");
        if (!properties.TryGetProperty("target", out var target) ||
            !SameEndpoint(target.GetString(), expectedTarget))
        {
            throw new CliException(
                $"Connection '{connectionId}' does not target '{expectedTarget}'. " +
                "Create a new connection against the gateway specialist runtime; " +
                "reusing a connection does not retarget it. OAuth connections must not be " +
                "retargeted in place.");
        }

        var expectedAuthType = authenticationMode switch
        {
            ChainAuthenticationMode.OAuth => "OAuth2",
            ChainAuthenticationMode.UserEntraToken => "UserEntraToken",
            ChainAuthenticationMode.ProjectManagedIdentity => "ProjectManagedIdentity",
            _ => throw new CliException("Unsupported Foundry connection authentication mode.")
        };
        if (!properties.TryGetProperty("category", out var category) ||
            category.GetString() != "RemoteA2A" ||
            !properties.TryGetProperty("authType", out var authType) ||
            authType.GetString() != expectedAuthType)
        {
            throw new CliException(
                $"Connection '{connectionId}' must be a RemoteA2A connection using {expectedAuthType}.");
        }

        if (authenticationMode == ChainAuthenticationMode.OAuth)
        {
            if (!HasNativeOAuthConnectionMetadata(connection.RootElement.GetRawText()))
            {
                throw new CliException(ConnectorGatewayMissingMessage);
            }

            var scope = $"{audience.TrimEnd('/')}/access_as_user";
            if (!properties.TryGetProperty("scopes", out var scopes) ||
                scopes.ValueKind != JsonValueKind.Array ||
                !scopes.EnumerateArray().Any(item => item.GetString() == scope))
            {
                throw new CliException(
                    $"Connection '{connectionId}' does not request the delegated scope '{scope}'.");
            }
        }
        else if (!properties.TryGetProperty("audience", out var actualAudience) ||
                 !string.Equals(actualAudience.GetString(), audience, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"Connection '{connectionId}' uses a different Entra audience.");
        }
    }

    private static async Task ValidateReplacementTargetAsync(
        HttpClient client,
        string accessToken,
        string connectionId,
        string targetAgentId,
        CancellationToken cancellationToken)
    {
        using var connection = await GetConnectionAsync(client, accessToken, connectionId, cancellationToken);
        var properties = connection.RootElement.GetProperty("properties");
        if (!properties.TryGetProperty("target", out var target) ||
            !Uri.TryCreate(target.GetString(), UriKind.Absolute, out var endpoint) ||
            !endpoint.AbsolutePath.TrimEnd('/').EndsWith(
                $"/a2a-agents/{Uri.EscapeDataString(targetAgentId)}/a2a", StringComparison.Ordinal))
        {
            throw new CliException(
                $"Connection '{connectionId}' is not bound to specialist '{targetAgentId}'; " +
                "refusing to remove an unrelated tool.");
        }
    }

    private static async Task<JsonDocument> GetConnectionAsync(
        HttpClient client,
        string accessToken,
        string connectionId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://management.azure.com{connectionId}?api-version=2025-06-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(
                $"Could not read Foundry connection '{connectionId}': HTTP {(int)response.StatusCode}. " +
                "No agent definition was changed.");
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static bool SameEndpoint(string? actual, string expected) =>
        Uri.TryCreate(actual, UriKind.Absolute, out var actualUri) &&
        Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri) &&
        string.IsNullOrEmpty(actualUri.UserInfo) &&
        string.IsNullOrEmpty(actualUri.Query) &&
        string.IsNullOrEmpty(actualUri.Fragment) &&
        Uri.Compare(actualUri, expectedUri, UriComponents.SchemeAndServer,
            UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0 &&
        string.Equals(actualUri.AbsolutePath.TrimEnd('/'), expectedUri.AbsolutePath.TrimEnd('/'),
            StringComparison.Ordinal);

    internal static string BuildChainBaseUrl(string adapterUrl, string targetAgentId) =>
        $"{adapterUrl.TrimEnd('/')}/a2a-agents/{Uri.EscapeDataString(targetAgentId)}/";

    internal static string BuildDefaultConnectionName(string targetAgentId)
    {
        var normalized = Regex.Replace(
            targetAgentId.ToLowerInvariant(),
            "[^a-z0-9_-]+",
            "-").Trim('-', '_');
        if (normalized.Length == 0)
        {
            normalized = "agent";
        }

        const string prefix = "a2a-";
        if (prefix.Length + normalized.Length <= 33)
        {
            return $"{prefix}{normalized}";
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(targetAgentId)))[..6].ToLowerInvariant();
        var available = 33 - prefix.Length - hash.Length - 1;
        return $"{prefix}{normalized[..available]}-{hash}";
    }

    internal static async Task<string?> PutRemoteA2AConnectionAsync(
        HttpClient httpClient,
        string accessToken,
        string subscriptionId,
        string resourceGroup,
        string accountName,
        string projectName,
        string connectionName,
        string target,
        string audience,
        ChainAuthenticationMode authenticationMode,
        string tenantId,
        string? oauthClientId,
        string? oauthClientSecret,
        CancellationToken cancellationToken)
    {
        const string apiVersion = "2025-04-01-preview";
        static string Segment(string value) => Uri.EscapeDataString(value);

        var url =
            $"https://management.azure.com/subscriptions/{Segment(subscriptionId)}/" +
            $"resourceGroups/{Segment(resourceGroup)}/providers/Microsoft.CognitiveServices/" +
            $"accounts/{Segment(accountName)}/projects/{Segment(projectName)}/connections/" +
            $"{Segment(connectionName)}?api-version={apiVersion}";
        if (authenticationMode == ChainAuthenticationMode.OAuth)
        {
            using var lookup = new HttpRequestMessage(HttpMethod.Get, url);
            lookup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var existing = await httpClient.SendAsync(lookup, cancellationToken);
            if (existing.IsSuccessStatusCode)
            {
                throw new CliException(
                    $"Foundry connection '{connectionName}' already exists. OAuth updates are not " +
                    "supported; use --reuse-connection or a new name. No connection was overwritten.");
            }
            if (existing.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw new CliException(
                    $"Could not check whether Foundry connection '{connectionName}' exists: " +
                    $"HTTP {(int)existing.StatusCode}. No connection was written.");
            }
        }
        object properties = authenticationMode switch
        {
            ChainAuthenticationMode.UserEntraToken => new
            {
                authType = "UserEntraToken",
                category = "RemoteA2A",
                target,
                audience
            },
            ChainAuthenticationMode.OAuth => new
            {
                authType = "OAuth2",
                group = "ServicesAndApps",
                category = "RemoteA2A",
                target,
                isSharedToAll = true,
                sharedUserList = Array.Empty<string>(),
                authorizationUrl =
                    $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize",
                tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
                refreshUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
                scopes = new[]
                {
                    $"{audience.TrimEnd('/')}/access_as_user",
                    "offline_access"
                },
                credentials = new
                {
                    clientId = oauthClientId,
                    clientSecret = oauthClientSecret
                },
                metadata = new { ApiType = "Azure" }
            },
            ChainAuthenticationMode.ProjectManagedIdentity => new
            {
                authType = "ProjectManagedIdentity",
                group = "ServicesAndApps",
                category = "RemoteA2A",
                target,
                audience,
                isSharedToAll = true,
                credentials = new { },
                metadata = new
                {
                    ApiType = "Azure",
                    type = "custom_A2A",
                    AgentCardPath = "/.well-known/agent-card.json"
                }
            },
            _ => throw new CliException("Unsupported Foundry chain authentication mode.")
        };
        var body = JsonSerializer.Serialize(new { properties });

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new CliException(
                $"Foundry connection ARM request returned HTTP " +
                $"{(int)response.StatusCode}: {responseBody}");
        }

        if (authenticationMode != ChainAuthenticationMode.OAuth)
        {
            return null;
        }

        var successBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseConnectionRedirectUrl(successBody);
    }

    /// <summary>
    /// Accept documented API metadata as well as the portal's native A2A marker. Neither
    /// is proof of a working connector or user consent.
    /// </summary>
    internal static async Task<bool> HasNativeOAuthConnectionMetadataAsync(
        HttpClient httpClient,
        string accessToken,
        string connectionId,
        CancellationToken cancellationToken)
    {
        using var connection = await GetConnectionAsync(
            httpClient, accessToken, connectionId, cancellationToken);
        return HasNativeOAuthConnectionMetadata(connection.RootElement.GetRawText());
    }

    internal static bool HasNativeOAuthConnectionMetadata(string connectionBody)
    {
        using var document = JsonDocument.Parse(connectionBody);
        return document.RootElement.TryGetProperty("properties", out var properties) &&
               properties.TryGetProperty("metadata", out var metadata) &&
               metadata.ValueKind == JsonValueKind.Object &&
               ((metadata.TryGetProperty("type", out var type) &&
                 string.Equals(type.GetString(), "custom_A2A", StringComparison.OrdinalIgnoreCase)) ||
                (metadata.TryGetProperty("ApiType", out var apiType) &&
                 string.Equals(apiType.GetString(), "Azure", StringComparison.OrdinalIgnoreCase)));
    }

    internal const string ConnectorGatewayMissingMessage =
        "This OAuth connection lacks the documented Azure or native custom_A2A metadata. " +
        "A connection record alone does not prove connector readiness. Create a new native " +
        "connection in the Foundry portal (Build > Tools > Connect a tool > Agent2agent (A2A), " +
        "'Connect via endpoint', OAuth Identity Passthrough) against the same target. " +
        "Add its actual generated redirect URL as an additional Web redirect URI on the existing " +
        "backend registration, then re-run with '--reuse-connection'. Preserve shared old " +
        "connections. Successful user authorization and a real callback are still required.";

    internal static string? ParseConnectionRedirectUrl(string successBody)
    {
        using var document = JsonDocument.Parse(successBody);
        return document.RootElement
            .GetProperty("properties")
            .TryGetProperty("redirectUrl", out var redirectUrl)
            ? redirectUrl.GetString()
            : null;
    }

    internal static string UserTokenFailureMessage(string detail) =>
        detail.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase)
            ? "Azure Developer CLI is not authorized for the adapter's access_as_user scope. " +
              "Preauthorize Azure CLI application 04b07795-8ddb-461a-bbee-02f9e1bf7b46 on " +
              "the adapter app registration, or use the default '--auth-mode oauth'."
            : "Could not acquire an adapter-audience delegated token. " +
              $"Use '--auth-mode oauth' for a custom API. Details: {detail}";

    internal static string ParseAzdAccessToken(string output)
    {
        var trimmed = output.Trim();
        if (!trimmed.StartsWith('{'))
        {
            if (trimmed.StartsWith("eyJ", StringComparison.Ordinal) &&
                trimmed.Count(character => character == '.') == 2)
            {
                return trimmed;
            }

            throw new CliException("Azure Developer CLI returned an invalid access token.");
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.TryGetProperty("token", out var token) &&
                !string.IsNullOrWhiteSpace(token.GetString()))
            {
                return token.GetString()!;
            }
        }
        catch (JsonException exception)
        {
            throw new CliException(
                $"Azure Developer CLI returned an invalid token response: {exception.Message}");
        }

        throw new CliException("Azure Developer CLI returned an empty access token.");
    }

    internal static async Task<HashSet<string>> ListProjectConnectionIdsAsync(
        HttpClient httpClient,
        string accessToken,
        string subscriptionId,
        string resourceGroup,
        string accountName,
        string projectName,
        CancellationToken cancellationToken)
    {
        static string Segment(string value) => Uri.EscapeDataString(value);
        string? url =
            $"https://management.azure.com/subscriptions/{Segment(subscriptionId)}/" +
            $"resourceGroups/{Segment(resourceGroup)}/providers/Microsoft.CognitiveServices/" +
            $"accounts/{Segment(accountName)}/projects/{Segment(projectName)}/connections" +
            "?api-version=2025-06-01";
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collectionUri = new Uri(url);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (url is not null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var pageUri) ||
                !SameEndpoint(pageUri.GetLeftPart(UriPartial.Path), collectionUri.GetLeftPart(UriPartial.Path)) ||
                !string.IsNullOrEmpty(pageUri.UserInfo) ||
                !string.IsNullOrEmpty(pageUri.Fragment) ||
                !visited.Add(url))
            {
                throw new CliException("Foundry returned an invalid connection-list continuation URL.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new CliException(
                    $"Could not list Foundry connections: HTTP {(int)response.StatusCode}. " +
                    "Refusing to prune tools without a complete connection inventory.");
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                throw new CliException("Foundry returned an invalid connection inventory; no tools were pruned.");
            }

            foreach (var connection in value.EnumerateArray())
            {
                if (!connection.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString()))
                {
                    throw new CliException("Foundry returned a connection without an ID; no tools were pruned.");
                }
                ids.Add(id.GetString()!);
            }

            url = document.RootElement.TryGetProperty("nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }

        return ids;
    }

    internal static JsonObject BuildChainDefinition(
        JsonElement existingDefinition,
        string connectionId,
        string targetAgentId,
        string targetAgentName,
        IReadOnlySet<string>? existingConnectionIds = null,
        string? targetUrl = null,
        string? replacedConnectionId = null)
    {
        var definition = JsonNode.Parse(existingDefinition.GetRawText()) as JsonObject
            ?? throw new CliException("The latest Foundry agent version has no prompt definition.");
        if (definition["kind"]?.GetValue<string>() != "prompt")
        {
            throw new CliException("Only Foundry prompt agents can be configured for this chain.");
        }

        var tools = definition["tools"] as JsonArray ?? [];
        if (definition["tools"] is not JsonArray)
        {
            definition["tools"] = tools;
        }

        // Drop A2A tools whose connection no longer exists. Preserving them leaves the agent with
        // several indistinguishable A2A tools for the same target, so the model picks between them
        // nondeterministically and can select a deleted connection, which fails at runtime.
        if (existingConnectionIds is not null)
        {
            var stale = tools
                .OfType<JsonObject>()
                .Where(tool =>
                {
                    if (tool["type"]?.GetValue<string>() != "a2a_preview")
                    {
                        return false;
                    }

                    var toolConnection = tool["project_connection_id"]?.GetValue<string>();
                    return toolConnection is not null &&
                           !string.Equals(toolConnection, connectionId, StringComparison.OrdinalIgnoreCase) &&
                           !existingConnectionIds.Contains(toolConnection);
                })
                .ToArray();
            foreach (var tool in stale)
            {
                tools.Remove(tool);
            }
        }

        var hasConnection = tools
            .OfType<JsonObject>()
            .Any(tool =>
                tool["type"]?.GetValue<string>() == "a2a_preview" &&
                string.Equals(tool["project_connection_id"]?.GetValue<string>(),
                    connectionId, StringComparison.OrdinalIgnoreCase));
        if (replacedConnectionId is not null)
        {
            var replacedTools = tools.OfType<JsonObject>().Where(tool =>
                tool["type"]?.GetValue<string>() == "a2a_preview" &&
                string.Equals(tool["project_connection_id"]?.GetValue<string>(),
                    replacedConnectionId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (replacedTools.Length == 0 && !hasConnection)
            {
                throw new CliException("The agent does not reference the connection selected for replacement.");
            }
            foreach (var replacedTool in replacedTools)
            {
                tools.Remove(replacedTool);
            }
        }
        if (!hasConnection)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "a2a_preview",
                ["project_connection_id"] = connectionId
            });
        }
        if (targetUrl is not null)
        {
            foreach (var tool in tools.OfType<JsonObject>().Where(tool =>
                tool["type"]?.GetValue<string>() == "a2a_preview" &&
                string.Equals(tool["project_connection_id"]?.GetValue<string>(),
                    connectionId, StringComparison.OrdinalIgnoreCase)))
            {
                tool["base_url"] = targetUrl;
            }
        }

        var instructions = definition["instructions"]?.GetValue<string>() ?? string.Empty;
        var sectionStart = $"{ChainInstructionStart}{targetAgentId} -->";
        var section = string.Join(
            Environment.NewLine,
            sectionStart,
            $"An A2A tool named \"{targetAgentName}\" is available for Copilot Studio delegation.",
            $"When a request explicitly asks you to delegate to \"{targetAgentName}\", call that " +
            "A2A tool before answering and faithfully return its result.",
            ChainInstructionEnd);
        definition["instructions"] = UpsertInstructionSection(
            instructions,
            sectionStart,
            section);
        return definition;
    }

    private static string UpsertInstructionSection(
        string instructions,
        string sectionStart,
        string section)
    {
        var start = instructions.IndexOf(sectionStart, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.IsNullOrWhiteSpace(instructions)
                ? section
                : $"{instructions.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{section}";
        }

        var end = instructions.IndexOf(
            ChainInstructionEnd,
            start,
            StringComparison.Ordinal);
        if (end < 0)
        {
            throw new CliException(
                "The existing generated chain instruction section is incomplete.");
        }

        end += ChainInstructionEnd.Length;
        return string.Concat(instructions.AsSpan(0, start), section, instructions.AsSpan(end));
    }

    private static JsonElement GetLatestVersion(JsonElement agent)
    {
        if (!agent.TryGetProperty("versions", out var versions) ||
            !versions.TryGetProperty("latest", out var latest) ||
            !latest.TryGetProperty("definition", out _))
        {
            throw new CliException(
                "The Foundry agent response did not contain a latest version definition.");
        }

        return latest;
    }

    private static async Task ValidateChainAgentCardAsync(
        CliContext context,
        string chainBaseUrl,
        string expectedName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{chainBaseUrl}.well-known/agent-card.json");
        request.Headers.TryAddWithoutValidation("X-Tunnel-Skip-AntiPhishing-Page", "true");
        using var response = await context.HttpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(
                $"Chain agent card returned HTTP {(int)response.StatusCode}: {responseBody}");
        }

        using var card = JsonDocument.Parse(responseBody);
        if (!card.RootElement.TryGetProperty("name", out var name) ||
            name.GetString() != expectedName)
        {
            throw new CliException(
                $"The chain agent card at '{chainBaseUrl}' did not describe '{expectedName}'.");
        }
        var runtimeUrl = $"{chainBaseUrl}a2a";
        if (!AgentCardContract.AdvertisesRuntime(
                card.RootElement, url => SameEndpoint(url, runtimeUrl)))
        {
            throw new CliException(
                "The specialist card advertises a different runtime. Set Adapter:PublicBaseUrl " +
                "to the same public gateway base before configuring a native connection.");
        }
    }

    private static async Task ValidateUserTokenAcquisitionAsync(
        CliContext context,
        string tenantId,
        string audience,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.Processes.CaptureAsync(
                "azd",
                [
                    "auth", "token",
                    "--tenant-id", tenantId,
                    "--scope", $"{audience.TrimEnd('/')}/access_as_user",
                    "--no-prompt"
                ],
                cancellationToken,
                new Dictionary<string, string?>
                {
                    ["AZURE_DEV_USER_AGENT"] = "microsoft_foundry_skill"
                });
        }
        catch (ExternalCommandException exception)
        {
            throw new CliException(UserTokenFailureMessage(exception.Message));
        }
    }

    private static ChainAuthenticationMode ParseAuthenticationMode(string value) =>
        value.ToLowerInvariant() switch
        {
            "oauth" => ChainAuthenticationMode.OAuth,
            "user-entra-token" => ChainAuthenticationMode.UserEntraToken,
            "project-managed-identity" => ChainAuthenticationMode.ProjectManagedIdentity,
            _ => throw new CliException(
                "Option '--auth-mode' must be 'oauth', 'user-entra-token', or " +
                "'project-managed-identity'.")
        };

    private static void ValidateConnectionName(string value)
    {
        if (!ConnectionNamePattern.IsMatch(value))
        {
            throw new CliException(
                "Option '--connection-name' must be 3-33 characters and contain only letters, " +
                "numbers, hyphens, or underscores, starting with a letter or number.");
        }
    }

    private static void ValidateEnvironmentVariableName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('=') ||
            value.Any(char.IsWhiteSpace) ||
            value.Any(char.IsControl))
        {
            throw new CliException(
                "Option '--oauth-client-secret-env' is not a valid environment variable name.");
        }
    }

    private static void ValidateConfiguredChain(JsonElement agentVersion, string connectionId)
    {
        if (!agentVersion.TryGetProperty("definition", out var definition) ||
            !definition.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array ||
            !tools.EnumerateArray().Any(tool =>
                tool.TryGetProperty("type", out var type) &&
                type.GetString() == "a2a_preview" &&
                tool.TryGetProperty("project_connection_id", out var id) &&
                id.GetString() == connectionId))
        {
            throw new CliException(
                "Foundry created the agent version, but the A2A tool was not present.");
        }
    }

    private static async Task SmokeTestAsync(
        CliContext context,
        FoundryAgentAddress address,
        string prompt,
        CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = $"smoke-{suffix}",
            method = "SendMessage",
            @params = new
            {
                message = new
                {
                    role = "ROLE_USER",
                    parts = new[] { new { text = prompt } },
                    messageId = $"message-{suffix}",
                    contextId = $"context-{suffix}"
                }
            }
        });
        var runtimeUrl =
            $"{address.ProjectEndpoint}/agents/{Uri.EscapeDataString(address.AgentName)}" +
            "/endpoint/protocols/a2a";
        using var response = await FoundryRestClient.InvokeAsync(
            context,
            HttpMethod.Post,
            runtimeUrl,
            body,
            cancellationToken,
            new Dictionary<string, string> { ["A2A-Version"] = "1.0" });

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            throw new CliException($"Foundry A2A smoke test failed: {error.GetRawText()}");
        }

        if (!response.RootElement.TryGetProperty("result", out _))
        {
            throw new CliException(
                "Foundry A2A smoke test returned neither a result nor a JSON-RPC error.");
        }

        context.Out.WriteLine("Foundry A2A smoke test passed.");
    }

    private static bool HasAgentCard(JsonElement agent) =>
        agent.TryGetProperty("agent_card", out var card) &&
        card.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static void ValidatePublishedCard(
        JsonElement card,
        string expectedDescription,
        string expectedSkillId)
    {
        if (!card.TryGetProperty("description", out var description) ||
            description.GetString() != expectedDescription ||
            !card.TryGetProperty("skills", out var skills) ||
            skills.ValueKind != JsonValueKind.Array ||
            !skills.EnumerateArray().Any(
                skill => skill.TryGetProperty("id", out var id) &&
                         id.GetString() == expectedSkillId))
        {
            throw new CliException(
                "Foundry accepted the update, but the published A2A agent card did not match it.");
        }
    }

    private static void ValidateCardValue(
        string option,
        string value,
        bool allowSpaces)
    {
        if (value.Contains('/') ||
            value.Any(char.IsControl) ||
            (!allowSpaces && value.Any(char.IsWhiteSpace)))
        {
            throw new CliException($"Option '--{option}' contains unsupported characters.");
        }
    }
}

internal enum ChainAuthenticationMode
{
    OAuth,
    UserEntraToken,
    ProjectManagedIdentity
}

internal sealed record FoundryAgentAddress(string ProjectEndpoint, string AgentName)
{
    public static FoundryAgentAddress Parse(Uri agentUrl)
    {
        var marker = "/agents/";
        var markerIndex = agentUrl.AbsolutePath.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 1)
        {
            throw new CliException(
                "Option '--agent-url' must be a Foundry agent or agent protocol endpoint URL.");
        }

        var agentPath = agentUrl.AbsolutePath[(markerIndex + marker.Length)..];
        var separatorIndex = agentPath.IndexOf('/');
        var encodedAgentName = separatorIndex < 0
            ? agentPath
            : agentPath[..separatorIndex];
        var agentName = Uri.UnescapeDataString(encodedAgentName);
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new CliException(
                "Option '--agent-url' does not contain a Foundry agent name.");
        }

        var authority = agentUrl.GetLeftPart(UriPartial.Authority);
        var projectPath = agentUrl.AbsolutePath[..markerIndex].TrimEnd('/');
        return new FoundryAgentAddress(
            $"{authority}{projectPath}",
            agentName);
    }
}
