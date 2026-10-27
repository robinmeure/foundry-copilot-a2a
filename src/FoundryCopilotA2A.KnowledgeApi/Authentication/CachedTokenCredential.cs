using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;

namespace FoundryCopilotA2A.KnowledgeApi.Authentication;

/// <summary>
/// Caches Azure access tokens in memory per scope and refreshes them proactively before expiration.
/// Also warms up the token cache once during application startup in live Azure mode.
/// </summary>
public sealed class CachedTokenCredential : TokenCredential, IDisposable
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly string[] WarmupScopes =
    [
        "https://cognitiveservices.azure.com/.default",
        "https://search.azure.com/.default"
    ];

    private readonly TokenCredential innerCredential;
    private readonly ConcurrentDictionary<string, AccessToken> tokenCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    public CachedTokenCredential(TokenCredential innerCredential)
    {
        this.innerCredential = innerCredential;
    }

    public static CachedTokenCredential CreateDefault(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            var devCredential = new ChainedTokenCredential(
                new AzureCliCredential(),
                new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ExcludeManagedIdentityCredential = true,
                    ExcludeWorkloadIdentityCredential = true
                }));

            return new CachedTokenCredential(devCredential);
        }

        var managedIdentityClientId = configuration["AZURE_CLIENT_ID"];
        TokenCredential prodCredential = string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = managedIdentityClientId
            });

        return new CachedTokenCredential(prodCredential);
    }

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var scopeKey = BuildScopeKey(requestContext.Scopes);
        if (tokenCache.TryGetValue(scopeKey, out var cachedToken) && IsTokenFresh(cachedToken))
        {
            return cachedToken;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (tokenCache.TryGetValue(scopeKey, out cachedToken) && IsTokenFresh(cachedToken))
            {
                return cachedToken;
            }

            var refreshedToken = await innerCredential.GetTokenAsync(requestContext, cancellationToken);
            tokenCache[scopeKey] = refreshedToken;
            return refreshedToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task WarmUpAsync(ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var scope in WarmupScopes)
        {
            try
            {
                var token = await GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
                logger.LogInformation(
                    "Warmed up Azure access token for scope {Scope} (expires {ExpiresOn:O})",
                    scope,
                    token.ExpiresOn);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Token warm-up for scope {Scope} did not succeed at startup; will retry on demand.",
                    scope);
            }
        }
    }

    public void Dispose()
    {
        refreshLock.Dispose();
    }

    private static string BuildScopeKey(string[] scopes) =>
        string.Join(" ", scopes);

    private static bool IsTokenFresh(AccessToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return token.ExpiresOn - now > RefreshBuffer;
    }
}
