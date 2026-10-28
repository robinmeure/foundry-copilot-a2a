namespace FoundryCopilotA2A.Adapter;

public sealed class ApiManagementDiscoveryOptions
{
    public const string SectionName = "ApiManagementDiscovery";

    public bool Enabled { get; set; }

    public string SubscriptionId { get; set; } = string.Empty;

    public string ResourceGroup { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Optional APIM API IDs to discover. When empty, all current APIs are checked for a public
    /// A2A agent card.
    /// </summary>
    public string[] ApiIds { get; set; } = [];

    /// <summary>
    /// Interval at which the background service refreshes the discovery snapshot. Requests are
    /// routed from the snapshot and never wait for a scheduled refresh.
    /// </summary>
    public int RefreshSeconds { get; set; } = 300;

    /// <summary>
    /// Longest age of the last successful snapshot that requests may still use when background
    /// refreshes fail. Beyond it, a request refreshes synchronously and surfaces any error.
    /// </summary>
    public int MaxStaleSeconds { get; set; } = 900;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int MaxApis { get; set; } = 100;

    /// <summary>Maximum number of agent cards read from the gateway in parallel.</summary>
    public int MaxConcurrentCardReads { get; set; } = 4;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Guid.TryParse(SubscriptionId, out _))
        {
            throw new InvalidOperationException(
                "ApiManagementDiscovery:SubscriptionId must be an Azure subscription GUID.");
        }

        if (string.IsNullOrWhiteSpace(ResourceGroup) || string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException(
                "ApiManagementDiscovery:ResourceGroup and ServiceName are required when discovery is enabled.");
        }

        if (RefreshSeconds <= 0 || RequestTimeoutSeconds <= 0 || MaxApis <= 0)
        {
            throw new InvalidOperationException(
                "ApiManagementDiscovery refresh, request timeout, and API limit must be positive.");
        }

        if (MaxStaleSeconds < RefreshSeconds)
        {
            throw new InvalidOperationException(
                "ApiManagementDiscovery:MaxStaleSeconds must be at least RefreshSeconds.");
        }

        if (MaxConcurrentCardReads is < 1 or > 16)
        {
            throw new InvalidOperationException(
                "ApiManagementDiscovery:MaxConcurrentCardReads must be between 1 and 16.");
        }

        if (ApiIds.Any(id => string.IsNullOrWhiteSpace(id)))
        {
            throw new InvalidOperationException(
                "ApiManagementDiscovery:ApiIds cannot contain empty values.");
        }
    }
}
