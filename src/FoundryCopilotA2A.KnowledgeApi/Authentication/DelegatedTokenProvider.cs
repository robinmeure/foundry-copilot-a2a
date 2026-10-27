using System.Security.Claims;
using System.Text;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.KnowledgeApi.Authentication;

public sealed class DelegatedTokenProvider
{
    private readonly IConfidentialClientApplication? confidentialClient;

    internal DelegatedTokenProvider(IConfidentialClientApplication confidentialClient) =>
        this.confidentialClient = confidentialClient;

    public DelegatedTokenProvider(IConfiguration config)
    {
        if (config["Knowledge:Backend"] == "Mock")
        {
            return;
        }

        var clientSecret = config["Authentication:ClientSecret"];
        if (string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException(
                "Configure Authentication:ClientSecret in a secret store for delegated OBO.");
        }

        confidentialClient = ConfidentialClientApplicationBuilder
            .Create(config["Authentication:ClientId"])
            .WithAuthority($"https://login.microsoftonline.com/{config["Authentication:TenantId"]}")
            .WithClientSecret(clientSecret)
            .Build();
    }

    public async Task<string> GetTokenAsync(
        HttpContext context,
        string[] scopes,
        CancellationToken cancellationToken)
    {
        var authorizationHeader = context.Request.Headers.Authorization.ToString();
        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(401, "missing_token", "A delegated API token is required.");
        }

        if (confidentialClient is null)
        {
            throw new InvalidOperationException("Delegated OBO is unavailable in mock mode.");
        }

        try
        {
            var userAssertion = new UserAssertion(authorizationHeader["Bearer ".Length..]);
            var result = await confidentialClient
                .AcquireTokenOnBehalfOf(scopes, userAssertion)
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException exception)
        {
            if (!string.IsNullOrEmpty(exception.Claims))
            {
                var encodedClaims = Convert.ToBase64String(Encoding.UTF8.GetBytes(exception.Claims));
                ResponseSignals.Set(context, ResponseSignals.Challenge,
                    $"Bearer claims=\"{encodedClaims}\", error=\"insufficient_claims\"");
            }

            var graph = scopes.All(scope => scope.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase));
            throw new ApiException(
                401,
                graph ? "graph_consent_required" : "delegated_consent_required",
                graph
                    ? "Graph consent or renewed sign-in is required for the selected workload."
                    : "Delegated consent or renewed sign-in is required for the selected workload.");
        }
    }

    public async Task<string> GetSameAudienceTokenAsync(HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authentication = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        var principal = authentication.Principal;
        var audience = confidentialClient?.AppConfig.ClientId;
        var token = authentication.Properties?.GetTokenValue("access_token");
        if (!authentication.Succeeded || principal?.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(token) ||
            !string.Equals(principal.FindFirstValue("aud"), audience, StringComparison.OrdinalIgnoreCase) ||
            principal.FindAll("aud").Count() != 1 ||
            principal.FindFirstValue("scp")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("access_as_user", StringComparer.Ordinal) != true ||
            string.IsNullOrWhiteSpace(principal.FindFirstValue("tid")) ||
            string.IsNullOrWhiteSpace(principal.FindFirstValue("oid")) ||
            principal.FindFirstValue("tid") != context.User.FindFirstValue("tid") ||
            principal.FindFirstValue("oid") != context.User.FindFirstValue("oid"))
        {
            throw new ApiException(401, "invalid_shared_api_token",
                "A validated delegated token for this API audience is required for shared-registration A2A access.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return token;
    }
}
