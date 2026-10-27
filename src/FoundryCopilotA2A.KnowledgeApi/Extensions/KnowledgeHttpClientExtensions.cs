namespace FoundryCopilotA2A.KnowledgeApi.Extensions;

/// <summary>Configures upstream clients without sensitive request logging or automatic retries.</summary>
public static class KnowledgeHttpClientExtensions
{
    public static IServiceCollection AddKnowledgeHttpClients(this IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient("knowledge-a2a", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();

        services.AddHttpClient("knowledge", client => client.Timeout = TimeSpan.FromMinutes(5))
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();

        // SharePoint download URLs carry temporary access; redirects must not leak it to another host.
        services.AddHttpClient("download", client => client.Timeout = TimeSpan.FromMinutes(2))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        return services;
    }
}
