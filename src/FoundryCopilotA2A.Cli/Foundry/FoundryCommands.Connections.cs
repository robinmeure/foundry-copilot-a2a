using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoundryCopilotA2A.Cli;

internal static partial class FoundryCommands
{
    private static string? ReadOAuthClientSecret(
        CommandArguments arguments,
        ChainAuthenticationMode mode,
        bool reuseConnection,
        string? clientId)
    {
        if (reuseConnection || mode != ChainAuthenticationMode.OAuth)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new CliException(
                "Option '--oauth-client-id' is required when '--auth-mode oauth' is used.");
        }

        var variable = arguments.Optional("oauth-client-secret-env", DefaultOAuthSecretEnvironmentVariable)!;
        ValidateEnvironmentVariableName(variable);
        var secret = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new CliException(
                $"Environment variable '{variable}' must contain the OAuth " +
                "client secret. Secrets are not accepted on the command line.");
        }

        return secret;
    }

    private static async Task<string> AcquireArmTokenAsync(
        CliContext context,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var result = await context.Processes.CaptureAsync(
            "azd",
            [
                "auth", "token",
                "--tenant-id", tenantId,
                "--scope", "https://management.azure.com/.default",
                "--no-prompt"
            ],
            cancellationToken,
            new Dictionary<string, string?> { ["AZURE_DEV_USER_AGENT"] = "microsoft_foundry_skill" });
        return ParseAzdAccessToken(result.StandardOutput);
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
        FoundryConnectionAddress address,
        string target,
        string audience,
        ChainAuthenticationMode authenticationMode,
        string tenantId,
        string? oauthClientId,
        string? oauthClientSecret,
        CancellationToken cancellationToken)
    {
        var connectionName = address.Name;
        var url = address.ManagementUrl("2025-04-01-preview");
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
}
