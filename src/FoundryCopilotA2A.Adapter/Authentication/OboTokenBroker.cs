using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Adapter;

public interface IOboTokenBroker
{
    Task<string> AcquireAsync(
        string scope,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed class OboTokenBroker : IOboTokenBroker
{
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";
    private readonly CopilotStudioOptions _options;
    private readonly string? _expectedAudience;
    private readonly Lazy<IConfidentialClientApplication> _application;

    public OboTokenBroker(
        IOptions<CopilotStudioOptions> options,
        IConfiguration configuration)
    {
        _options = options.Value;
        _expectedAudience = configuration["Authentication:Audience"];

        // Built once. A plain "??=" on a singleton races under concurrent first requests and
        // produces several applications, each with its own token cache.
        _application = new Lazy<IConfidentialClientApplication>(
            BuildApplication,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private IConfidentialClientApplication BuildApplication()
    {
        var builder = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _options.TenantId);

        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            return builder
                .WithClientSecret(_options.ClientSecret)
                .Build();
        }

        var credential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(_options.ManagedIdentityClientId));
        var tokenRequest = new TokenRequestContext([TokenExchangeScope]);

        return builder
            .WithClientAssertion(async options =>
                (await credential.GetTokenAsync(
                    tokenRequest,
                    options.CancellationToken).ConfigureAwait(false)).Token)
            .Build();
    }

    public async Task<string> AcquireAsync(
        string scope,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var traceActivity = AdapterTelemetry.StartActivity(
            "entra.obo.acquire_token",
            System.Diagnostics.ActivityKind.Client);
        traceActivity?.SetTag("auth.flow", "on_behalf_of");
        traceActivity?.SetTag("auth.scope", scope);
        if (string.IsNullOrWhiteSpace(metadata.BearerToken))
        {
            throw new UnauthorizedAccessException(
                "A delegated bearer token is required for the Copilot Studio OBO flow.");
        }

        // Only exchange a token that was actually issued for this adapter.
        var expectedAudience = _expectedAudience;
        if (!string.IsNullOrWhiteSpace(expectedAudience))
        {
            var audience = TokenInspector.ReadAudience(metadata.BearerToken);
            if (audience is not null && !AudienceMatches(audience, expectedAudience))
            {
                throw new UnauthorizedAccessException(
                    "The caller token was not issued for this adapter and will not be exchanged.");
            }
        }

        // An app-only caller (for example a Foundry project managed identity) has no user to act
        // on behalf of. Entra rejects OBO for such tokens with AADSTS7000114, so fall back to the
        // client-credentials flow and call the downstream service as this application instead.
        if (TokenInspector.IsAppOnly(metadata.BearerToken))
        {
            traceActivity?.SetTag("auth.flow", "client_credentials");
            var appOnlyResult = await _application.Value
                .AcquireTokenForClient([ResolveDefaultScope(scope)])
                .ExecuteAsync(cancellationToken);

            traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return appOnlyResult.AccessToken;
        }

        var result = await _application.Value
            .AcquireTokenOnBehalfOf([scope], new UserAssertion(metadata.BearerToken))
            .ExecuteAsync(cancellationToken);

        traceActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return result.AccessToken;
    }

    /// <summary>
    /// The client-credentials flow only accepts resource-wide "/.default" scopes, never the
    /// individual delegated permissions used by the on-behalf-of flow.
    /// </summary>
    public static string ResolveDefaultScope(string scope)
    {
        if (scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
        {
            return scope;
        }

        var separator = scope.LastIndexOf('/');
        return separator <= 0
            ? scope
            : $"{scope[..separator]}/.default";
    }

    private static bool AudienceMatches(string audience, string expected) =>
        string.Equals(audience, expected, StringComparison.OrdinalIgnoreCase)
        // Entra may present the audience as either "api://<id>" or the bare client id.
        || string.Equals($"api://{audience}", expected, StringComparison.OrdinalIgnoreCase)
        || string.Equals(audience, expected.Replace("api://", string.Empty), StringComparison.OrdinalIgnoreCase);
}
