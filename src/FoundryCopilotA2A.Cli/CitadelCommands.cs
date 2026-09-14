using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace FoundryCopilotA2A.Cli;

internal sealed record CitadelConfiguration(
    string SubscriptionId,
    string ResourceGroup,
    string ServiceName,
    string ApiId,
    string ApiPath,
    string BackendUrl,
    string TenantId,
    string ApiClientId,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AgentIds,
    bool Replace);

internal sealed record CitadelApi(string Id, string Path, string BackendUrl, string? AgentId);

internal sealed record CitadelOperation(
    string Id,
    string DisplayName,
    string Method,
    string Path,
    bool RequiresToken = false,
    string? Parameter = null);

internal static class CitadelCommands
{
    internal const string OwnershipMarker =
        "Managed by FoundryCopilotA2A.Cli configure-citadel.";
    private const string ArmEndpoint = "https://management.azure.com";
    private const string ApiVersion = "2024-05-01";
    private const string ClosedPolicy =
        """
        <policies>
          <inbound>
            <return-response>
              <set-status code="503" reason="Gateway configuration in progress" />
            </return-response>
          </inbound>
          <backend />
          <outbound />
          <on-error />
        </policies>
        """;

    public static async Task<int> ConfigureAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "subscription-id", "resource-group", "service-name", "api-id", "api-path",
            "backend-url", "tenant-id", "api-client-id", "allowed-origin", "allowed-origins",
            "agent-ids", "replace", "help");

        var configuration = ParseConfiguration(arguments);
        if (string.IsNullOrEmpty(configuration.SubscriptionId))
        {
            var account = await context.Processes.CaptureAsync(
                "az", ["account", "show", "--query", "id", "--output", "tsv"],
                cancellationToken);
            configuration = configuration with
            {
                SubscriptionId = account.StandardOutput.Trim()
            };
        }

        if (!Guid.TryParse(configuration.SubscriptionId, out _))
        {
            throw new CliException("An explicit or active Azure subscription ID is required.");
        }

        var token = await context.Processes.CaptureAsync(
            "az",
            [
                "account", "get-access-token", "--subscription", configuration.SubscriptionId,
                "--resource", ArmEndpoint, "--query", "accessToken", "--output", "tsv"
            ],
            cancellationToken);
        var accessToken = token.StandardOutput.Trim();
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new CliException("Azure CLI did not return an ARM access token.");
        }

        context.Out.WriteLine(
            $"Configuring API '{configuration.ApiId}' in '{configuration.ServiceName}'. " +
            "Only APIs owned by this command are changed; they remain closed if configuration fails.");
        var baseUrl = await ConfigureGatewayAsync(
            context.HttpClient, accessToken, configuration, cancellationToken);
        context.Out.WriteLine($"Citadel API base URL: {baseUrl}");
        context.Out.WriteLine($"GatewayBaseUrl={baseUrl}");
        context.Out.WriteLine($"VITE_GATEWAY_BASE_URL={baseUrl}");
        context.Out.WriteLine(
            "The browser still requests api://<backend-client-id>/access_as_user. " +
            "No subscription key, client secret, or Power Platform token belongs in the frontend.");
        return 0;
    }

    internal static CitadelConfiguration ParseConfiguration(CommandArguments arguments)
    {
        var apiId = ValidateIdentifier(
            arguments.Optional("api-id", "copilot-studio-local")!, "api-id", 80);
        var apiPath = arguments.Optional("api-path", apiId)!.Trim('/');
        if (string.IsNullOrEmpty(apiPath) ||
            apiPath.Split('/').Any(segment => !IsIdentifier(segment)))
        {
            throw new CliException("--api-path must contain nonempty URL segments using letters, digits, '-' or '_'.");
        }

        var backend = arguments.AbsoluteHttpUri("backend-url");
        if (backend.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(backend.UserInfo) ||
            !string.IsNullOrEmpty(backend.Query) ||
            !string.IsNullOrEmpty(backend.Fragment))
        {
            throw new CliException("--backend-url must be HTTPS without credentials, a query, or a fragment.");
        }

        var origins = ReadAllowedOrigins(arguments);
        var tenantId = arguments.Require("tenant-id");
        var apiClientId = arguments.Require("api-client-id");
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(apiClientId, out _))
        {
            throw new CliException("--tenant-id and --api-client-id must be GUIDs.");
        }

        var agents = arguments.Optional("agent-ids", "")!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => ValidateIdentifier(id, "agent-ids", 80 - apiId.Length - 1))
            .ToArray();

        return new CitadelConfiguration(
            arguments.Optional("subscription-id", "")!,
            arguments.Require("resource-group"),
            arguments.Require("service-name"),
            apiId, apiPath, backend.AbsoluteUri.TrimEnd('/'),
            tenantId, apiClientId, origins, agents, arguments.Flag("replace"));
    }

    internal static string ReadOrigin(CommandArguments arguments)
    {
        return ParseOrigin(
            arguments.Optional("allowed-origin", "http://localhost:5173")!,
            "allowed-origin");
    }

    internal static IReadOnlyList<string> ReadAllowedOrigins(CommandArguments arguments)
    {
        if (arguments.Has("allowed-origin") && arguments.Has("allowed-origins"))
        {
            throw new CliException("Choose either --allowed-origin or --allowed-origins.");
        }

        var optionName = arguments.Has("allowed-origins") ? "allowed-origins" : "allowed-origin";
        var configured = arguments.Optional(optionName, "http://localhost:5173")!;
        var origins = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ParseOrigin(value, optionName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (origins.Length == 0)
        {
            throw new CliException($"--{optionName} must contain at least one origin.");
        }

        return origins;
    }

    private static string ParseOrigin(string value, string optionName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps))
        {
            throw new CliException(
                $"--{optionName} must contain exact HTTP or HTTPS origins without paths.");
        }
        if (origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new CliException(
                $"--{optionName} must contain exact HTTP or HTTPS origins without paths.");
        }

        return origin.GetLeftPart(UriPartial.Authority);
    }

    internal static async Task<string> ConfigureGatewayAsync(
        HttpClient client,
        string accessToken,
        CitadelConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var serviceUrl =
            $"{ArmEndpoint}/subscriptions/{Uri.EscapeDataString(configuration.SubscriptionId)}" +
            $"/resourceGroups/{Uri.EscapeDataString(configuration.ResourceGroup)}" +
            "/providers/Microsoft.ApiManagement/service/" +
            Uri.EscapeDataString(configuration.ServiceName);
        using var service = await GetAsync(
            client, accessToken, serviceUrl, allowMissing: false, cancellationToken);
        var gatewayUrl = service!.RootElement.GetProperty("properties")
            .GetProperty("gatewayUrl").GetString();
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gateway) ||
            gateway.Scheme != Uri.UriSchemeHttps)
        {
            throw new CliException("The existing APIM service has no HTTPS gateway URL.");
        }

        var apis = BuildApis(configuration);
        var existingApis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Check every target before mutating any of them. Never repoint the existing hosted API.
        foreach (var api in apis)
        {
            using var existing = await GetAsync(
                client, accessToken, $"{serviceUrl}/apis/{api.Id}",
                allowMissing: true, cancellationToken);
            if (existing is null)
            {
                continue;
            }

            var properties = existing.RootElement.GetProperty("properties");
            if (!configuration.Replace)
            {
                throw new CliException(
                    $"API '{api.Id}' already exists. Use --replace to update this command's APIs.");
            }
            if (!properties.TryGetProperty("description", out var description) ||
                description.GetString() != OwnershipMarker)
            {
                throw new CliException(
                    $"API '{api.Id}' is not owned by configure-citadel. Choose a separate --api-id.");
            }
            if (properties.GetProperty("path").GetString() != api.Path)
            {
                throw new CliException(
                    $"API '{api.Id}' has a different path. Use its existing --api-path or a new --api-id.");
            }

            existingApis.Add(api.Id);
        }

        var apiPolicy = BuildApiPolicy(configuration);
        foreach (var api in apis)
        {
            var apiUrl = $"{serviceUrl}/apis/{api.Id}";
            if (existingApis.Contains(api.Id))
            {
                await PutPolicyAsync(
                    client, accessToken, apiUrl, ClosedPolicy, cancellationToken);
            }

            await PutAsync(client, accessToken, apiUrl, new
            {
                properties = new
                {
                    displayName = api.AgentId is null
                        ? "Copilot Studio A2A - local development"
                        : $"Copilot Studio A2A - {api.AgentId}",
                    description = OwnershipMarker,
                    path = api.Path,
                    protocols = new[] { "https" },
                    serviceUrl = api.BackendUrl,
                    subscriptionRequired = false,
                    type = "http"
                }
            }, cancellationToken);

            if (!existingApis.Contains(api.Id))
            {
                await PutPolicyAsync(
                    client, accessToken, apiUrl, ClosedPolicy, cancellationToken);
            }

            foreach (var operation in BuildOperations(api.AgentId is not null))
            {
                var operationUrl = $"{apiUrl}/operations/{operation.Id}";
                await PutAsync(client, accessToken, operationUrl, new
                {
                    properties = new
                    {
                        displayName = operation.DisplayName,
                        method = operation.Method,
                        urlTemplate = operation.Path,
                        request = new
                        {
                            representations = operation.Method == "POST"
                                ? new[] { new { contentType = "application/json" } }
                                : []
                        },
                        templateParameters = operation.Parameter is null
                            ? []
                            : new[] { new { name = operation.Parameter, type = "string", required = true } },
                        responses = Array.Empty<object>()
                    }
                }, cancellationToken);
                if (operation.RequiresToken)
                {
                    await PutPolicyAsync(
                        client, accessToken, operationUrl,
                        BuildDelegatedPolicy(configuration, operation.Method == "POST"),
                        cancellationToken);
                }
                else if (operation.Method == "GET" &&
                         operation.Id.EndsWith("agent-card", StringComparison.Ordinal))
                {
                    await PutPolicyAsync(
                        client, accessToken, operationUrl,
                        BuildDiscoveryPolicy(), cancellationToken);
                }
            }

            // Only open the surface once every protected operation has its JWT policy.
            await PutPolicyAsync(client, accessToken, apiUrl, apiPolicy, cancellationToken);
        }

        return $"{gateway.AbsoluteUri.TrimEnd('/')}/{configuration.ApiPath}";
    }

    internal static IReadOnlyList<CitadelApi> BuildApis(CitadelConfiguration configuration) =>
    [
        new(configuration.ApiId, configuration.ApiPath, configuration.BackendUrl, null),
        .. configuration.AgentIds.Select(agentId => new CitadelApi(
            $"{configuration.ApiId}-{agentId}",
            $"{configuration.ApiPath}/a2a-agents/{agentId}",
            $"{configuration.BackendUrl}/a2a-agents/{agentId}",
            agentId))
    ];

    internal static IReadOnlyList<CitadelOperation> BuildOperations(bool specialist) =>
        specialist
            ?
            [
                new("get-agent-card", "Get specialist agent card", "GET", "/.well-known/agent-card.json"),
                new("get-runtime-agent-card", "Get runtime-relative agent card", "GET", "/a2a/.well-known/agent-card.json"),
                new("get-legacy-agent-card", "Get legacy specialist agent card", "GET", "/.well-known/agent.json"),
                new("get-runtime-legacy-agent-card", "Get legacy runtime-relative agent card", "GET", "/a2a/.well-known/agent.json"),
                new("invoke-a2a-runtime", "Invoke specialist A2A runtime", "POST", "/a2a", true)
            ]
            :
            [
                new("get-agent-card", "Get agent card", "GET", "/.well-known/agent-card.json"),
                new("get-agent-catalog", "Get agent catalog", "GET", "/api/agents"),
                new("get-caller-trace", "Get caller-scoped trace", "GET", "/api/traces/{traceId}", true, "traceId"),
                new("invoke-a2a-runtime", "Invoke A2A runtime", "POST", "/a2a/copilot-studio", true)
            ];

    internal static string BuildApiPolicy(CitadelConfiguration configuration)
    {
        var backendHost = new Uri(configuration.BackendUrl).Host;
        var tunnelHeader = backendHost.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase)
            ? new XElement("set-header",
                new XAttribute("name", "X-Tunnel-Skip-AntiPhishing-Page"),
                new XAttribute("exists-action", "override"),
                new XElement("value", "true")).ToString()
            : "";
        return ReadPolicy("citadel-api.xml")
            .Replace(
                "__ALLOWED_ORIGINS__",
                string.Join(
                    Environment.NewLine,
                    configuration.AllowedOrigins.Select(origin =>
                        new XElement("origin", origin).ToString())))
            .Replace("__BACKEND_HEADERS__", tunnelHeader);
    }

    internal static string BuildDelegatedPolicy(CitadelConfiguration configuration, bool runtime) =>
        ReadPolicy("citadel-delegated-operation.xml")
            .Replace("__ENTRA_LOGIN_ENDPOINT__", "https://login.microsoftonline.com/")
            .Replace("{{entra-tenant-id}}", configuration.TenantId)
            .Replace("{{adapter-api-audience}}", $"api://{configuration.ApiClientId}")
            .Replace("{{adapter-api-bare-audience}}", configuration.ApiClientId)
            .Replace("{{adapter-delegated-scope}}", "access_as_user")
            .Replace("__RATE_LIMIT_CALLS__", runtime ? "60" : "600")
            .Replace("__REQUEST_VALIDATION__", runtime ? ReadPolicy("citadel-validate-json.xml") : "");

    internal static string BuildDiscoveryPolicy() => ReadPolicy("citadel-discovery-operation.xml");

    private static string ReadPolicy(string name)
    {
        using var stream = typeof(CitadelCommands).Assembly.GetManifestResourceStream($"Citadel.{name}")
            ?? throw new CliException($"The CLI is missing its embedded Citadel policy '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Task PutPolicyAsync(
        HttpClient client,
        string token,
        string parentUrl,
        string policy,
        CancellationToken cancellationToken) =>
        PutAsync(client, token, $"{parentUrl}/policies/policy",
            new { properties = new { format = "rawxml", value = policy } }, cancellationToken);

    private static async Task PutAsync(
        HttpClient client,
        string token,
        string url,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = CreateArmRequest(HttpMethod.Put, url, token);
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument?> GetAsync(
        HttpClient client,
        string token,
        string url,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        using var request = CreateArmRequest(HttpMethod.Get, url, token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static HttpRequestMessage CreateArmRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, $"{url}?api-version={ApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new CliException(
            $"APIM management returned HTTP {(int)response.StatusCode}: {error}");
    }

    private static string ValidateIdentifier(string value, string option, int maximumLength)
    {
        if (!IsIdentifier(value) || value.Length > maximumLength)
        {
            throw new CliException(
                $"--{option} must use letters, digits, '-' or '_' and fit the APIM API ID limit.");
        }

        return value;
    }

    private static bool IsIdentifier(string value) =>
        value.Length > 0 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
