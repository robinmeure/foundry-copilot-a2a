namespace FoundryCopilotA2A.KnowledgeApi.Extensions;

/// <summary>Exposes only the configured browser origins, methods and authentication response headers.</summary>
public static class KnowledgeCorsExtensions
{
    public static IServiceCollection AddKnowledgeCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddCors(cors =>
        {
            cors.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(configuration.GetSection("Knowledge:AllowedOrigins").Get<string[]>() ?? [])
                    .WithMethods("GET", "POST", "DELETE")
                    .WithHeaders("Authorization", "Content-Type")
                    .WithExposedHeaders("WWW-Authenticate", "Retry-After");
            });
        });
        return services;
    }
}
