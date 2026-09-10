namespace FoundryCopilotA2A.Adapter;

public sealed record ConnectivityDiagnostics(
    string Backend,
    bool AuthenticationEnabled,
    string? PublicBaseUrl,
    bool IsDevTunnel,
    string? ConfigurationError)
{
    public static ConnectivityDiagnostics Create(
        AdapterOptions adapter,
        AuthenticationOptions authentication)
    {
        if (!Uri.TryCreate(adapter.PublicBaseUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            endpoint.OriginalString.Contains('?') ||
            endpoint.OriginalString.Contains('#'))
        {
            return new(
                adapter.UseMockBackend ? "Mock" : "CopilotStudio",
                authentication.Enabled, null, false,
                "Adapter:PublicBaseUrl is not a safe HTTP(S) base URL. Its value has been withheld.");
        }

        return new(
            adapter.UseMockBackend ? "Mock" : "CopilotStudio",
            authentication.Enabled,
            endpoint.AbsoluteUri.TrimEnd('/'),
            endpoint.Scheme == Uri.UriSchemeHttps &&
                endpoint.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase),
            null);
    }
}
