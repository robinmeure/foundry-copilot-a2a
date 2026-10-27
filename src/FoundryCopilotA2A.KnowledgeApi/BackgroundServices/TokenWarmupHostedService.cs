using Azure.Core;
using FoundryCopilotA2A.KnowledgeApi.Authentication;

namespace FoundryCopilotA2A.KnowledgeApi.BackgroundServices;

/// <summary>Primes live-mode credentials; failures are logged and retried on demand rather than blocking startup.</summary>
public sealed class TokenWarmupHostedService(
    IConfiguration configuration,
    TokenCredential credential,
    ILogger<TokenWarmupHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration["Knowledge:Backend"] == "Mock")
        {
            return;
        }

        var openAiEndpoint = configuration["OpenAI:Endpoint"];
        if (Uri.TryCreate(openAiEndpoint, UriKind.Absolute, out var uri) &&
            uri.Host.StartsWith("example.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (credential is CachedTokenCredential cachedCredential)
        {
            await cachedCredential.WarmUpAsync(logger, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
