using System.Diagnostics;
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
    private static readonly IReadOnlyDictionary<string, ResolvedApiManagementAgent> NoAgents =
        new Dictionary<string, ResolvedApiManagementAgent>(StringComparer.OrdinalIgnoreCase);
    private volatile DiscoverySnapshot? _snapshot;

    private sealed record DiscoverySnapshot(
        IReadOnlyDictionary<string, ResolvedApiManagementAgent> Agents,
        DateTimeOffset RefreshedAt);

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

    /// <summary>
    /// Refreshes the snapshot outside the request path. A failure keeps the last successful
    /// snapshot, which requests keep using until it is older than <c>MaxStaleSeconds</c>.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshSnapshotAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, ResolvedApiManagementAgent>> GetResolvedAgentsAsync(
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("apim.discovery.resolve");
        var resolveStarted = Stopwatch.GetTimestamp();
        var result = "disabled";
        try
        {
            if (!Enabled)
            {
                return NoAgents;
            }

            if (TryUseSnapshot(activity, out var snapshotResult, out var agents))
            {
                result = snapshotResult;
                return agents;
            }

            // Only a missing or over-age snapshot blocks a request; the hosted refresher keeps
            // the snapshot current otherwise.
            result = _snapshot is null ? "miss" : "expired";
            activity?.SetTag("apim.discovery.cache.hit", false);
            var lockWaitStarted = Stopwatch.GetTimestamp();
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                AdapterTelemetry.RecordApiManagementDiscoveryLockWait(
                    activity,
                    Stopwatch.GetElapsedTime(lockWaitStarted));
                if (TryUseSnapshot(activity, out _, out agents))
                {
                    result = "filled_by_peer";
                    return agents;
                }

                result = "refreshed";
                return await RefreshSnapshotAsync(cancellationToken);
            }
            finally
            {
                _refreshLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            result = "canceled";
            throw;
        }
        catch (Exception exception)
        {
            result = "error";
            AdapterTelemetry.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            AdapterTelemetry.RecordApiManagementDiscoveryResolve(
                activity,
                Stopwatch.GetElapsedTime(resolveStarted),
                result);
        }
    }

    private bool TryUseSnapshot(
        Activity? activity,
        out string result,
        out IReadOnlyDictionary<string, ResolvedApiManagementAgent> agents)
    {
        var snapshot = _snapshot;
        agents = snapshot?.Agents ?? NoAgents;
        result = "miss";
        if (snapshot is null)
        {
            return false;
        }

        var age = timeProvider.GetUtcNow() - snapshot.RefreshedAt;
        activity?.SetTag("apim.discovery.snapshot.age_ms", age.TotalMilliseconds);
        if (age >= TimeSpan.FromSeconds(options.Value.MaxStaleSeconds))
        {
            return false;
        }

        result = age < TimeSpan.FromSeconds(options.Value.RefreshSeconds) ? "hit" : "stale";
        activity?.SetTag("apim.discovery.cache.hit", true);
        return true;
    }

    private async Task<IReadOnlyDictionary<string, ResolvedApiManagementAgent>> RefreshSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var discovered = await DiscoverAsync(cancellationToken);
        var agents = discovered.ToDictionary(
            agent => agent.Id,
            StringComparer.OrdinalIgnoreCase);
        _snapshot = new DiscoverySnapshot(agents, timeProvider.GetUtcNow());
        logger.LogInformation(
            "Discovered {AgentCount} A2A agent APIs in API Management service {ServiceName}.",
            agents.Count,
            options.Value.ServiceName);
        return agents;
    }

    private async Task<IReadOnlyList<ResolvedApiManagementAgent>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("apim.discovery.refresh");
        var refreshStarted = Stopwatch.GetTimestamp();
        var candidateCount = 0;
        var discoveredCount = 0;
        var refreshCompleted = false;
        try
        {
            options.Value.Validate();

            var token = await AcquireArmTokenAsync(cancellationToken);

            var serviceUrl =
                $"{ArmEndpoint}/subscriptions/{Uri.EscapeDataString(options.Value.SubscriptionId)}" +
                $"/resourceGroups/{Uri.EscapeDataString(options.Value.ResourceGroup)}" +
                "/providers/Microsoft.ApiManagement/service/" +
                Uri.EscapeDataString(options.Value.ServiceName);
            var (gatewayUrl, apis) = await ReadManagementAsync(serviceUrl, token.Token, cancellationToken);

            var requestedIds = options.Value.ApiIds
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = apis
                .Where(api =>
                    api.IsCurrent &&
                    (requestedIds.Count == 0 || requestedIds.Contains(api.Id)))
                .ToArray();
            candidateCount = candidates.Length;
            activity?.SetTag("apim.discovery.requested.count", requestedIds.Count);
            if (candidates.Length > options.Value.MaxApis)
            {
                throw new InvalidOperationException(
                    $"APIM discovery matched {candidates.Length} APIs, exceeding MaxApis " +
                    $"{options.Value.MaxApis}. Configure ApiIds or raise the explicit limit.");
            }

            if (requestedIds.Count > 0)
            {
                var missing = requestedIds
                    .Where(id => candidates.All(api =>
                        !string.Equals(api.Id, id, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (missing.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"APIM API IDs were not found: {string.Join(", ", missing)}.");
                }
            }

            var discovered = await ReadAgentCardsAsync(
                gatewayUrl, candidates, explicitlyRequested: requestedIds.Count > 0, cancellationToken);
            discoveredCount = discovered.Count;

            var duplicate = discovered
                .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException(
                    $"APIM discovery produced duplicate application agent ID '{duplicate.Key}'.");
            }

            refreshCompleted = true;
            activity?.SetStatus(ActivityStatusCode.Ok);
            return discovered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            AdapterTelemetry.RecordApiManagementDiscoveryRefresh(
                activity,
                Stopwatch.GetElapsedTime(refreshStarted),
                candidateCount,
                discoveredCount,
                refreshCompleted);
        }
    }

    private async Task<IReadOnlyList<ResolvedApiManagementAgent>> ReadAgentCardsAsync(
        Uri gatewayUrl,
        ApimApi[] candidates,
        bool explicitlyRequested,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("apim.discovery.read_agent_cards");
        var started = Stopwatch.GetTimestamp();
        var completed = false;
        var discovered = new List<ResolvedApiManagementAgent>();
        try
        {
            // Index results by candidate so snapshot order never depends on response timing.
            var results = new ResolvedApiManagementAgent?[candidates.Length];
            activity?.SetTag("apim.discovery.card.concurrency", options.Value.MaxConcurrentCardReads);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, candidates.Length),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Value.MaxConcurrentCardReads,
                    CancellationToken = cancellationToken
                },
                async (index, token) =>
                {
                    results[index] = await TryReadAgentAsync(
                        gatewayUrl, candidates[index], explicitlyRequested, token);
                });
            discovered.AddRange(results.OfType<ResolvedApiManagementAgent>());
            completed = true;
            activity?.SetStatus(ActivityStatusCode.Ok);
            return discovered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            activity?.SetTag("apim.discovery.candidate.count", candidates.Length);
            activity?.SetTag("apim.discovery.agent.count", discovered.Count);
            activity?.SetTag("apim.discovery.skipped.count", candidates.Length - discovered.Count);
            AdapterTelemetry.RecordApiManagementDiscoveryStage(
                activity, Stopwatch.GetElapsedTime(started), "agent_cards", completed);
        }
    }

    private async Task<AccessToken> AcquireArmTokenAsync(CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("apim.discovery.acquire_arm_token");
        var started = Stopwatch.GetTimestamp();
        var completed = false;
        try
        {
            var token = await credential.GetTokenAsync(ArmTokenRequest, cancellationToken);
            completed = true;
            activity?.SetStatus(ActivityStatusCode.Ok);
            return token;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            AdapterTelemetry.RecordApiManagementDiscoveryStage(
                activity, Stopwatch.GetElapsedTime(started), "credential", completed);
        }
    }

    private async Task<(Uri GatewayUrl, IReadOnlyList<ApimApi> Apis)> ReadManagementAsync(
        string serviceUrl,
        string token,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("apim.discovery.read_management");
        var started = Stopwatch.GetTimestamp();
        var completed = false;
        try
        {
            var gatewayUrl = await GetGatewayUrlAsync(serviceUrl, token, cancellationToken);
            var apis = await ListApisAsync(serviceUrl, token, cancellationToken);
            completed = true;
            activity?.SetTag("apim.discovery.api.count", apis.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return (gatewayUrl, apis);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            AdapterTelemetry.RecordApiManagementDiscoveryStage(
                activity, Stopwatch.GetElapsedTime(started), "management", completed);
        }
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
