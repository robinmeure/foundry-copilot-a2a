using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter;

/// <summary>
/// Keeps the API Management discovery snapshot current so requests route from memory instead of
/// paying for ARM and agent-card round trips when the cache expires.
/// </summary>
public sealed class ApiManagementDiscoveryService(
    ApiManagementAgentRegistry registry,
    IOptions<ApiManagementDiscoveryOptions> options,
    TimeProvider timeProvider,
    ILogger<ApiManagementDiscoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!registry.Enabled)
        {
            return;
        }

        var refreshInterval = TimeSpan.FromSeconds(options.Value.RefreshSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = refreshInterval;
            try
            {
                await registry.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The last successful snapshot keeps serving requests until MaxStaleSeconds;
                // after that a request refreshes synchronously and reports the failure.
                logger.LogWarning(
                    exception,
                    "Background API Management discovery refresh failed for service {ServiceName}.",
                    options.Value.ServiceName);
                delay = refreshInterval < MaxRetryDelay ? refreshInterval : MaxRetryDelay;
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
