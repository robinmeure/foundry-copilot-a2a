using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;

namespace FoundryCopilotA2A.KnowledgeApi.Extensions;

/// <summary>Composes configuration, HTTP contracts and knowledge modules without changing the request pipeline.</summary>
public static class KnowledgeApiExtensions
{
    public static void AddKnowledgeApi(this WebApplicationBuilder builder)
    {
        // Register shared defaults first so named clients can deliberately disable retries and logging.
        builder.AddServiceDefaults();

        builder.Services.AddKnowledgeServices();
        builder.Services.AddKnowledgeAuthentication(builder.Configuration);
        builder.Services.AddKnowledgeHttpClients();
        builder.Services.AddKnowledgeCors(builder.Configuration);
        builder.Services.AddProblemDetails();

        builder.Services.AddControllers(mvc =>
            {
                mvc.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            })
            .ConfigureApiBehaviorOptions(behavior =>
            {
                behavior.SuppressModelStateInvalidFilter = true;
            })
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // Allow multipart headers in addition to the configured file payload.
        builder.Services.Configure<FormOptions>(form =>
            form.MultipartBodyLengthLimit = builder.Configuration.GetValue<int>("Knowledge:MaxFileBytes") + 65536);
        builder.WebHost.ConfigureKestrel(server =>
            server.Limits.MaxRequestBodySize = builder.Configuration.GetValue<int>("Knowledge:MaxFileBytes") + 65536);
    }
}
