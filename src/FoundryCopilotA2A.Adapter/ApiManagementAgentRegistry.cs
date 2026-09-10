using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter;

public sealed record ResolvedApiManagementAgent(
    string Id,
    string DisplayName,
    string ApiId,
    Uri Endpoint);

public sealed class ApiManagementAgentRegistry(
    TokenCredential credential,
    IHttpClientFactory httpClientFactory,
    IOptions<ApiManagementDiscoveryOptions> options,
    TimeProvider timeProvider,
    ILogger<ApiManagementAgentRegistry> logger)
{
    private const string ArmEndpoint = "https://management.azure.com";
    private const string ApiVersion = "2024-05-01";
    private static readonly TokenRequestContext ArmTokenRequest =
        new(["https://management.azure.com/.default"]);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, ResolvedApiManagementAgent> _agents =
        new Dictionary<string, ResolvedApiManagementAgent>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _expiresAt;

    public bool Enabled => options.Value.Enabled;

    public async Task<IReadOnlyList<AgentDescriptor>> GetAgentsAsync(
        CancellationToken cancellationToken)
    {
        var agents = await GetResolvedAgentsAsync(cancellationToken);
        return agents.Values
            .Select(agent => new AgentDescriptor(
                agent.Id,
                agent.DisplayName,
                AgentProvider.ApiManagement,
                true,
                null,
                []))
            .OrderBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ResolvedApiManagementAgent?> TryResolveAsync(
        string? agentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        var agents = await GetResolvedAgentsAsync(cancellationToken);
        return agents.TryGetValue(agentId.Trim(), out var agent) ? agent : null;
    }

    private async Task<IReadOnlyDictionary<string, ResolvedApiManagementAgent>> GetResolvedAgentsAsync(
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return _agents;
        }

        var now = timeProvider.GetUtcNow();
        if (now < _expiresAt)
        {
            return _agents;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (now < _expiresAt)
            {
                return _agents;
            }

            var discovered = await DiscoverAsync(cancellationToken);
            _agents = discovered.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
            _expiresAt = now.AddSeconds(options.Value.RefreshSeconds);
            logger.LogInformation(
                "Discovered {AgentCount} A2A agent APIs in API Management service {ServiceName}.",
                _agents.Count,
                options.Value.ServiceName);
            return _agents;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<ResolvedApiManagementAgent>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        options.Value.Validate();
        var token = await credential.GetTokenAsync(ArmTokenRequest, cancellationToken);
        var serviceUrl =
            $"{ArmEndpoint}/subscriptions/{Uri.EscapeDataString(options.Value.SubscriptionId)}" +
            $"/resourceGroups/{Uri.EscapeDataString(options.Value.ResourceGroup)}" +
            "/providers/Microsoft.ApiManagement/service/" +
            Uri.EscapeDataString(options.Value.ServiceName);
        var gatewayUrl = await GetGatewayUrlAsync(serviceUrl, token.Token, cancellationToken);
        var apis = await ListApisAsync(serviceUrl, token.Token, cancellationToken);
        var requestedIds = options.Value.ApiIds
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = apis
            .Where(api => api.IsCurrent && (requestedIds.Count == 0 || requestedIds.Contains(api.Id)))
            .ToArray();
        if (candidates.Length > options.Value.MaxApis)
        {
            throw new InvalidOperationException(
                $"APIM discovery matched {candidates.Length} APIs, exceeding MaxApis " +
                $"{options.Value.MaxApis}. Configure ApiIds or raise the explicit limit.");
        }

        if (requestedIds.Count > 0)
        {
            var missing = requestedIds
                .Where(id => candidates.All(api => !string.Equals(api.Id, id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"APIM API IDs were not found: {string.Join(", ", missing)}.");
            }
        }

        var discovered = new List<ResolvedApiManagementAgent>();
        foreach (var api in candidates)
        {
            var agent = await TryReadAgentAsync(
                gatewayUrl,
                api,
                explicitlyRequested: requestedIds.Count > 0,
                cancellationToken);
            if (agent is not null)
            {
                discovered.Add(agent);
            }
        }

        var duplicate = discovered
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"APIM discovery produced duplicate application agent ID '{duplicate.Key}'.");
        }

        return discovered;
    }

    private async Task<Uri> GetGatewayUrlAsync(
        string serviceUrl,
        string token,
        CancellationToken cancellationToken)
    {
        using var document = await GetArmDocumentAsync(
            $"{serviceUrl}?api-version={ApiVersion}", token, cancellationToken);
        if (!document.RootElement.TryGetProperty("properties", out var properties) ||
            !properties.TryGetProperty("gatewayUrl", out var gatewayValue) ||
            !Uri.TryCreate(gatewayValue.GetString(), UriKind.Absolute, out var gateway) ||
            gateway.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "The configured APIM service did not return an HTTPS gateway URL.");
        }

        return gateway;
    }

    private async Task<IReadOnlyList<ApimApi>> ListApisAsync(
        string serviceUrl,
        string token,
        CancellationToken cancellationToken)
    {
        var result = new List<ApimApi>();
        string? nextUrl = $"{serviceUrl}/apis?api-version={ApiVersion}";
        while (nextUrl is not null)
        {
            using var document = await GetArmDocumentAsync(nextUrl, token, cancellationToken);
            if (!document.RootElement.TryGetProperty("value", out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("APIM returned an invalid API list.");
            }

            foreach (var value in values.EnumerateArray())
            {
                if (!TryReadApi(value, out var api))
                {
                    continue;
                }
                result.Add(api);
            }

            nextUrl = document.RootElement.TryGetProperty("nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
            if (nextUrl is not null &&
                (!Uri.TryCreate(nextUrl, UriKind.Absolute, out var nextUri) ||
                 nextUri.Scheme != Uri.UriSchemeHttps ||
                 !string.Equals(nextUri.Host, "management.azure.com", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("APIM returned an unsafe API-list continuation URL.");
            }
        }

        return result;
    }

    private async Task<JsonDocument> GetArmDocumentAsync(
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClientFactory
            .CreateClient("apim-management")
            .SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"APIM management request failed with HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "APIM management returned invalid JSON.", exception);
        }
    }

    private async Task<ResolvedApiManagementAgent?> TryReadAgentAsync(
        Uri gateway,
        ApimApi api,
        bool explicitlyRequested,
        CancellationToken cancellationToken)
    {
        if (api.SubscriptionRequired)
        {
            if (explicitlyRequested)
            {
                throw new InvalidOperationException(
                    $"APIM API '{api.Id}' requires a subscription key. " +
                    "Use a delegated Entra policy so the adapter does not store APIM secrets.");
            }
            return null;
        }

        var apiBaseUrl = new Uri(
            $"{gateway.AbsoluteUri.TrimEnd('/')}/{api.Path.Trim('/')}".TrimEnd('/'));
        var cardUrl = new Uri($"{apiBaseUrl.AbsoluteUri}/.well-known/agent-card.json");
        using var request = new HttpRequestMessage(HttpMethod.Get, cardUrl);
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await httpClientFactory
            .CreateClient("apim-discovery")
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (explicitlyRequested)
            {
                throw new InvalidOperationException(
                    $"APIM API '{api.Id}' did not expose a public A2A agent card " +
                    $"(HTTP {(int)response.StatusCode}).");
            }
            return null;
        }

        JsonDocument card;
        try
        {
            card = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            if (explicitlyRequested)
            {
                throw new InvalidOperationException(
                    $"APIM API '{api.Id}' returned an invalid A2A agent card.", exception);
            }
            return null;
        }

        using (card)
        {
            var endpoint = ReadRuntimeEndpoint(card.RootElement);
            if (endpoint is null ||
                endpoint.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(endpoint.Host, gateway.Host, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment) ||
                !IsWithinApiPath(endpoint, apiBaseUrl))
            {
                if (explicitlyRequested)
                {
                    throw new InvalidOperationException(
                        $"APIM API '{api.Id}' agent card must advertise an HTTPS runtime on the APIM gateway host.");
                }
                return null;
            }

            var displayName =
                ReadString(card.RootElement, "name") ??
                ReadString(card.RootElement, "displayName") ??
                api.DisplayName;
            return new ResolvedApiManagementAgent(
                CreateAgentId(api.Id),
                displayName,
                api.Id,
                endpoint);
        }
    }

    private static bool TryReadApi(JsonElement value, out ApimApi api)
    {
        api = default!;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("name", out var nameValue) ||
            !value.TryGetProperty("properties", out var properties))
        {
            return false;
        }

        var name = nameValue.GetString()?.Split(';', 2)[0];
        var path = ReadString(properties, "path");
        var displayName = ReadString(properties, "displayName");
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var isCurrent = !properties.TryGetProperty("isCurrent", out var current) ||
                        current.ValueKind != JsonValueKind.False;
        var subscriptionRequired =
            properties.TryGetProperty("subscriptionRequired", out var required) &&
            required.ValueKind == JsonValueKind.True;
        api = new ApimApi(name, displayName, path, isCurrent, subscriptionRequired);
        return true;
    }

    private static Uri? ReadRuntimeEndpoint(JsonElement card)
    {
        var url = ReadString(card, "url");
        if (Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            return endpoint;
        }

        if (!card.TryGetProperty("supportedInterfaces", out var interfaces) ||
            interfaces.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in interfaces.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                Uri.TryCreate(ReadString(item, "url"), UriKind.Absolute, out endpoint))
            {
                return endpoint;
            }
        }

        return null;
    }

    private static bool IsWithinApiPath(Uri endpoint, Uri apiBaseUrl)
    {
        var apiPath = apiBaseUrl.AbsolutePath.TrimEnd('/');
        var endpointPath = endpoint.AbsolutePath.TrimEnd('/');
        return string.Equals(endpointPath, apiPath, StringComparison.OrdinalIgnoreCase) ||
               endpointPath.StartsWith($"{apiPath}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var result) &&
        result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static string CreateAgentId(string apiId)
    {
        var safe = new string(apiId
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-')
            .ToArray())
            .Trim('-');
        if (safe.Length == 0)
        {
            throw new InvalidOperationException(
                $"APIM API ID '{apiId}' cannot be represented as an application agent ID.");
        }
        return $"apim-{safe}";
    }

    private sealed record ApimApi(
        string Id,
        string DisplayName,
        string Path,
        bool IsCurrent,
        bool SubscriptionRequired);
}
