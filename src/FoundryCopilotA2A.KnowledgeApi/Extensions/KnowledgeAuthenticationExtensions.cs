using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FoundryCopilotA2A.KnowledgeApi.Extensions;

/// <summary>Registers JWT validation and the delegated permission required outside mock mode.</summary>
public static class KnowledgeAuthenticationExtensions
{
    public static IServiceCollection AddKnowledgeAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = true;
                options.Authority = $"https://login.microsoftonline.com/{configuration["Authentication:TenantId"]}/v2.0";
                options.Audience = configuration["Authentication:ClientId"];
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("delegated", policy =>
            {
                policy.RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.User.FindFirst("scp")?.Value
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Contains("access_as_user") == true);
            });
        });
        return services;
    }
}
