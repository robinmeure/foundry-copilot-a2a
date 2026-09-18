using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace FoundryCopilotA2A.Cli;

/// <summary>
/// Publishes the API Hub gateway API. The hub owns its own audience and exchanges the caller's
/// hub token for an adapter-audience token, so a browser token is never valid against the
/// adapter directly.
/// </summary>
internal static class HubCommands
{
    private const string ArmEndpoint = "https://management.azure.com";
    private const string ApiVersion = "2024-05-01";
    private const string OwnershipMarker =
        "Managed by foundry-copilot-a2a configure-hub. Exchanges hub tokens for adapter tokens.";

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
            "backend-url", "tenant-id", "hub-client-id", "adapter-client-id",
            "identity-client-id", "allowed-origins", "replace", "help");

        var subscriptionId = arguments.Require("subscription-id");
        if (!Guid.TryParse(subscriptionId, out _))
        {
            throw new CliException("Option '--subscription-id' must be a subscription GUID.");
        }

        var resourceGroup = arguments.Require("resource-group");
        var serviceName = arguments.Require("service-name");
        var apiId = arguments.Optional("api-id", "copilot-studio-hub")!;
        var apiPath = (arguments.Optional("api-path", "hub")!).Trim('/');
        if (apiPath.Length == 0)
        {
            throw new CliException("Option '--api-path' cannot be empty.");
        }

        var backendUrl = arguments.AbsoluteHttpUri("backend-url").AbsoluteUri.TrimEnd('/');
        var tenantId = arguments.Require("tenant-id");
        var hubClientId = RequireGuid(arguments, "hub-client-id");
        var adapterClientId = RequireGuid(arguments, "adapter-client-id");
        if (string.Equals(hubClientId, adapterClientId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                "The hub and adapter must be different applications. The hub exchanges a " +
                "hub-audience token for an adapter-audience token.");
        }

        var identityClientId = arguments.Optional("identity-client-id");
        if (identityClientId is not null && !Guid.TryParse(identityClientId, out _))
        {
            throw new CliException(
                "Option '--identity-client-id' must be the user-assigned identity's client ID. " +
                "Omit it to use the gateway's system-assigned identity.");
        }

        var allowedOrigins = (arguments.Optional("allowed-origins") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var parsed) &&
                (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                    ? parsed.AbsoluteUri.TrimEnd('/')
                    : throw new CliException(
                        $"Option '--allowed-origins' contains '{origin}', which is not an absolute HTTP or HTTPS origin."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var replace = arguments.Flag("replace");

        var accessToken = await AcquireArmTokenAsync(context, cancellationToken);
        var client = context.HttpClient;
        var serviceUrl =
            $"{ArmEndpoint}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
            $"/providers/Microsoft.ApiManagement/service/{serviceName}";

        using (var service = await GetAsync(
            client, accessToken, $"{serviceUrl}?api-version={ApiVersion}",
            allowMissing: true, cancellationToken))
        {
            if (service is null)
            {
                throw new CliException(
                    $"API Management service '{serviceName}' was not found in '{resourceGroup}'.");
            }

            var identity = service.RootElement.GetProperty("identity");
            var identityType = identity.TryGetProperty("type", out var type)
                ? type.GetString() ?? ""
                : "";
            if (identityType.Length == 0 || identityType == "None")
            {
                throw new CliException(
                    "The API Management service has no managed identity. The hub exchange " +
                    "authenticates through a federated identity credential and cannot use a secret.");
            }
        }

        var apiUrl = $"{serviceUrl}/apis/{apiId}";
        using (var existing = await GetAsync(
            client, accessToken, $"{apiUrl}?api-version={ApiVersion}",
            allowMissing: true, cancellationToken))
        {
            if (existing is not null && !replace)
            {
                throw new CliException(
                    $"API '{apiId}' already exists. Re-run with --replace to update it, or " +
                    "choose a different --api-id. Nothing was changed.");
            }

            if (existing is not null)
            {
                // Close the API before rewriting it so no request is served by a partially
                // updated policy set.
                await PutPolicyAsync(
                    client, accessToken, apiUrl, ClosedPolicy, cancellationToken);
            }
        }

        context.Out.WriteLine($"Publishing hub API '{apiId}' at /{apiPath}...");
        await PutAsync(client, accessToken, $"{apiUrl}?api-version={ApiVersion}", new
        {
            properties = new
            {
                displayName = "Copilot Studio A2A - API Hub",
                description = OwnershipMarker,
                path = apiPath,
                protocols = new[] { "https" },
                serviceUrl = backendUrl,
                subscriptionRequired = false,
                type = "http"
            }
        }, cancellationToken);

        await PutPolicyAsync(client, accessToken, apiUrl, ClosedPolicy, cancellationToken);

        foreach (var operation in BuildOperations())
        {
            var operationUrl = $"{apiUrl}/operations/{operation.Id}";
            await PutAsync(client, accessToken, $"{operationUrl}?api-version={ApiVersion}", new
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

            var policy = operation.Delegated
                ? BuildExchangePolicy(
                    tenantId, hubClientId, adapterClientId, identityClientId, operation.Runtime)
                : CitadelCommands.BuildDiscoveryPolicy();
            await PutPolicyAsync(client, accessToken, operationUrl, policy, cancellationToken);
        }

        await PutPolicyAsync(
            client,
            accessToken,
            apiUrl,
            BuildApiPolicy(backendUrl, allowedOrigins),
            cancellationToken);

        var gatewayUrl = await ResolveGatewayUrlAsync(
            client, accessToken, serviceUrl, cancellationToken);

        context.Out.WriteLine();
        context.Out.WriteLine("API Hub published.");
        context.Out.WriteLine($"Hub base URL:     {gatewayUrl}/{apiPath}");
        context.Out.WriteLine($"Caller scope:     api://{hubClientId}/access_as_user");
        context.Out.WriteLine($"Exchanged for:    api://{adapterClientId}/access_as_user");
        context.Out.WriteLine($"Backend:          {backendUrl}");
        context.Out.WriteLine(
            $"Gateway identity: {(identityClientId is null ? "system-assigned" : identityClientId)}");
        context.Out.WriteLine(
            "Callers must now request the hub scope. The adapter still performs its own OBO " +
            "exchange for the downstream provider.");
        return 0;
    }

    internal static string BuildExchangePolicy(
        string tenantId,
        string hubClientId,
        string adapterClientId,
        string? identityClientId,
        bool runtime) =>
        ReadPolicy("citadel-hub-exchange.xml")
            .Replace("__ENTRA_LOGIN_ENDPOINT__", "https://login.microsoftonline.com/")
            .Replace("{{entra-tenant-id}}", tenantId)
            .Replace("{{hub-api-audience}}", $"api://{hubClientId}")
            .Replace("{{hub-api-bare-audience}}", hubClientId)
            .Replace("{{hub-delegated-scope}}", "access_as_user")
            .Replace("{{hub-client-id}}", hubClientId)
            .Replace("{{adapter-delegated-scope}}", $"api://{adapterClientId}/access_as_user")
            .Replace(
                "__IDENTITY_CLIENT_ID__",
                identityClientId is null ? "" : $" client-id=\"{identityClientId}\"")
            .Replace("__RATE_LIMIT_CALLS__", runtime ? "60" : "600")
            .Replace(
                "__REQUEST_VALIDATION__",
                runtime ? ReadPolicy("citadel-validate-json.xml") : "");

    internal static string BuildApiPolicy(string backendUrl, IReadOnlyList<string> allowedOrigins)
    {
        var backendHost = new Uri(backendUrl).Host;
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
                    allowedOrigins.Select(origin => new XElement("origin", origin).ToString())))
            .Replace("__BACKEND_HEADERS__", tunnelHeader);
    }

    internal static IReadOnlyList<HubOperation> BuildOperations() =>
    [
        new("get-agent-card", "Get agent card", "GET", "/.well-known/agent-card.json", false, false),
        new("get-legacy-agent-card", "Get legacy agent card", "GET", "/.well-known/agent.json", false, false),
        new("get-specialist-agent-card", "Get specialist agent card", "GET",
            "/a2a-agents/{agentId}/.well-known/agent-card.json", false, false, "agentId"),
        new("get-agent-catalog", "Get agent catalog", "GET", "/api/agents", true, false),
        new("get-caller-trace", "Get caller-scoped trace", "GET", "/api/traces/{traceId}", true, false, "traceId"),
        new("invoke-a2a-runtime", "Invoke A2A runtime", "POST", "/a2a/copilot-studio", true, true),
        new("invoke-specialist-runtime", "Invoke specialist A2A runtime", "POST",
            "/a2a-agents/{agentId}/a2a", true, true, "agentId")
    ];

    private static string ReadPolicy(string name)
    {
        using var stream = typeof(HubCommands).Assembly
            .GetManifestResourceStream($"Citadel.{name}")
            ?? throw new CliException($"The CLI is missing its embedded policy '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<string> ResolveGatewayUrlAsync(
        HttpClient client,
        string token,
        string serviceUrl,
        CancellationToken cancellationToken)
    {
        using var service = await GetAsync(
            client, token, $"{serviceUrl}?api-version={ApiVersion}",
            allowMissing: false, cancellationToken);
        var gatewayUrl = service!.RootElement
            .GetProperty("properties")
            .GetProperty("gatewayUrl")
            .GetString();
        return !string.IsNullOrWhiteSpace(gatewayUrl)
            ? gatewayUrl.TrimEnd('/')
            : throw new CliException("The API Management service has no HTTPS gateway URL.");
    }

    private static string RequireGuid(CommandArguments arguments, string name)
    {
        var value = arguments.Require(name);
        return Guid.TryParse(value, out _)
            ? value
            : throw new CliException($"Option '--{name}' must be an application (client) ID.");
    }

    private static async Task<string> AcquireArmTokenAsync(
        CliContext context,
        CancellationToken cancellationToken)
    {
        var result = await context.Processes.CaptureAsync(
            "az",
            ["account", "get-access-token", "--resource", ArmEndpoint, "--query", "accessToken", "--output", "tsv"],
            cancellationToken);
        var token = result.StandardOutput.Trim();
        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new CliException("Run 'az login' before configuring the hub.");
    }

    private static Task PutPolicyAsync(
        HttpClient client,
        string token,
        string parentUrl,
        string policy,
        CancellationToken cancellationToken) =>
        PutAsync(client, token, $"{parentUrl}/policies/policy?api-version={ApiVersion}",
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
        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static HttpRequestMessage CreateArmRequest(
        HttpMethod method,
        string url,
        string token)
    {
        var request = new HttpRequestMessage(method, url);
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

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new CliException(
            $"Azure Resource Manager returned HTTP {(int)response.StatusCode}. {body}");
    }
}

internal sealed record HubOperation(
    string Id,
    string DisplayName,
    string Method,
    string Path,
    bool Delegated,
    bool Runtime,
    string? Parameter = null);
