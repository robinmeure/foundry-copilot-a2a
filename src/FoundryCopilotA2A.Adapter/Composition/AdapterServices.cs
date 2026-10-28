using A2A;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;


namespace FoundryCopilotA2A.Adapter;

internal static class AdapterServices
{
    public static (AdapterOptions Adapter, AuthenticationOptions Authentication) AddAdapterServices(
        this WebApplicationBuilder builder)
    {
        var adapterOptions = builder.Configuration
            .GetSection(AdapterOptions.SectionName)
            .Get<AdapterOptions>() ?? new AdapterOptions();
        var authenticationOptions = builder.Configuration
            .GetSection(AuthenticationOptions.SectionName)
            .Get<AuthenticationOptions>() ?? new AuthenticationOptions();
        var apiManagementDiscoveryOptions = builder.Configuration
            .GetSection(ApiManagementDiscoveryOptions.SectionName)
            .Get<ApiManagementDiscoveryOptions>() ?? new ApiManagementDiscoveryOptions();

        ValidateAdapterConfiguration(adapterOptions, authenticationOptions);
        apiManagementDiscoveryOptions.Validate();

        builder.Services.AddHttpContextAccessor();
        var traceStore = new SanitizedTraceStore(builder.Configuration);
        builder.Services.AddSingleton(traceStore);
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(AdapterTelemetry.MeterName))
            .WithTracing(tracing => tracing
                .AddSource(AdapterTelemetry.ActivitySourceName)
                .AddSource(GenAiTelemetry.ActivitySourceName)
                .AddProcessor(new SanitizedTraceProcessor(traceStore)));
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("agent-card", policy => policy
                .AllowAnyOrigin()
                .WithMethods(HttpMethods.Get)
                .WithHeaders("Content-Type", "A2A-Version"));
            options.AddPolicy("spa", policy =>
            {
                policy
                    .WithOrigins(adapterOptions.AllowedOrigins)
                    .WithMethods(HttpMethods.Get, HttpMethods.Post)
                    .WithHeaders(
                        "Authorization",
                        "Content-Type",
                        "A2A-Version",
                        AgentPlanUpdates.Header,
                        AgentPlanUpdates.LegacyHeader,
                        AdapterConstants.AgentHeaderName,
                        AdapterConstants.ChainTargetHeaderName)
                    .WithExposedHeaders(AdapterConstants.TraceHeaderName,
                        AgentPlanUpdates.Header, AgentPlanUpdates.LegacyHeader);
            });
        });

        // ServiceDefaults applies the standard resilience handler and instruments outbound requests.
        builder.Services.AddTransient<CopilotStudioTraceHandler>();
        builder.Services.AddHttpClient("copilot-studio")
            .AddHttpMessageHandler<CopilotStudioTraceHandler>();

        builder.Services.Configure<CopilotStudioOptions>(
            builder.Configuration.GetSection(CopilotStudioOptions.SectionName));
        builder.Services.Configure<FoundryOptions>(
            builder.Configuration.GetSection(FoundryOptions.SectionName));
        builder.Services.AddSingleton(
            Microsoft.Extensions.Options.Options.Create(apiManagementDiscoveryOptions));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<A2ARequestMetadataAccessor>();
        builder.Services.AddSingleton<CopilotStudioResponseMetadataAccessor>();
        builder.Services.AddSingleton<AgentIsolationKeyContext>();
        builder.Services.AddSingleton<AgentCatalog>();
        builder.Services.AddSingleton<ApiManagementAgentRegistry>();
        builder.Services.AddHostedService<ApiManagementDiscoveryService>();
        builder.Services.AddSingleton<CopilotConversationStore>();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<CopilotConversationHandles>();
        builder.Services.AddSingleton<IdempotencyStore>();
        builder.Services.AddSingleton<OboTokenBroker>();
        builder.Services.AddSingleton<IOboTokenBroker>(
            services => services.GetRequiredService<OboTokenBroker>());

        if (adapterOptions.UseMockBackend)
        {
            builder.Services.AddSingleton<ICopilotStudioInvoker, MockCopilotStudioInvoker>();
        }
        else
        {
            ValidateRealBackendConfiguration(builder.Configuration, authenticationOptions);
            builder.Services.AddSingleton<SdkCopilotStudioInvoker>();
            if (adapterOptions.EnableFailureMock)
            {
                builder.Services.AddSingleton<MockCopilotStudioInvoker>();
                builder.Services.AddSingleton<ICopilotStudioInvoker, FailureMockRoutingInvoker>();
            }
            else
            {
                builder.Services.AddSingleton<ICopilotStudioInvoker>(
                    services => services.GetRequiredService<SdkCopilotStudioInvoker>());
            }
        }

        // A chained Foundry call runs an LLM turn, an outbound A2A call into this adapter, and a
        // Copilot Studio turn before it answers, so it routinely takes 20s+. The default resilience
        // handler allows 10s per attempt and retries, and every retry re-runs the whole chain, which
        // invokes Copilot Studio again. Give the call room to finish and do not retry it.
#pragma warning disable EXTEXP0001
        // RemoveAllResilienceHandlers is marked experimental but is the only supported way to opt a
        // single client out of the default handler applied by ServiceDefaults.
        var foundryHttpClient = builder.Services.AddHttpClient("foundry-a2a", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(adapterOptions.FoundryRequestTimeoutSeconds);
        });
        foundryHttpClient.RemoveAllResilienceHandlers();
        builder.Services.AddHttpClient("apim-management", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(apiManagementDiscoveryOptions.RequestTimeoutSeconds);
        });
        builder.Services.AddHttpClient("apim-discovery", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(apiManagementDiscoveryOptions.RequestTimeoutSeconds);
        });
        var apiManagementA2AClient = builder.Services.AddHttpClient("apim-a2a", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(adapterOptions.FoundryRequestTimeoutSeconds);
        });
        apiManagementA2AClient.RemoveAllResilienceHandlers();
        // A native Copilot Studio orchestrator can also invoke tools before an HTTP error is observed.
        // Retrying its turn or restarting its conversation could execute the specialist twice.
        var copilotOrchestratorClient = builder.Services
            .AddHttpClient("copilot-studio-orchestrator", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(adapterOptions.RequestTimeoutSeconds);
            })
            .AddHttpMessageHandler<CopilotStudioTraceHandler>();
        copilotOrchestratorClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"];
        TokenCredential foundryCredential = builder.Environment.IsDevelopment()
            ? new AzureCliCredential()
            : new ManagedIdentityCredential(
                string.IsNullOrWhiteSpace(managedIdentityClientId)
                    ? ManagedIdentityId.SystemAssigned
                    : ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
        builder.Services.AddSingleton(foundryCredential);
        builder.Services.AddSingleton<FoundryA2AInvoker>();
        builder.Services.AddSingleton<ApiManagementA2AInvoker>();
        builder.Services.AddSingleton<IAgentInvoker, RoutingAgentInvoker>();
        builder.Services.AddSingleton<CopilotStudioAdapterChatClient>();

        if (authenticationOptions.Enabled)
        {
            ValidateAuthenticationConfiguration(authenticationOptions);
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = authenticationOptions.Authority;
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        // Entra issues v1.0 tokens from sts.windows.net and v2.0 tokens from
                        // login.microsoftonline.com/<tenant>/v2.0. Both are legitimate for the same
                        // tenant and which one arrives depends on the calling client, not on this API.
                        // Enumerate them explicitly instead of relaxing issuer validation.
                        ValidIssuers = authenticationOptions.ResolveValidIssuers(),
                        // Entra presents the audience as "api://<client-id>" or the bare client id.
                        ValidAudiences = authenticationOptions.ResolveValidAudiences()
                    };
                });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<AgentIsolationKeyProvider, RequestScopedAgentIsolationKeyProvider>();
        }

        var hostedAgent = builder.AddAIAgent(
            AdapterConstants.AgentName,
            (services, _) => services
                .GetRequiredService<CopilotStudioAdapterChatClient>()
                .AsAIAgent(
                    instructions: "Delegate the user's request to the configured Copilot Studio agent.",
                    name: AdapterConstants.AgentName,
                    description: "A2A facade for a Copilot Studio specialist agent."));

        builder.Services.AddSingleton<PlanExtensionContext>();
        builder.Services.AddKeyedSingleton<IAgentHandler>(
            AdapterConstants.AgentName, PlanStreamingAgentHandler.Create);

        // The keyed handler preserves task/session isolation and separates plan status from artifacts.
        hostedAgent.AddA2AServer();
        return (adapterOptions, authenticationOptions);
    }

    static void ValidateRealBackendConfiguration(
        IConfiguration configuration,
        AuthenticationOptions authenticationOptions)
    {
        if (!authenticationOptions.Enabled)
        {
            throw new InvalidOperationException(
                "Authentication:Enabled must be true when Adapter:Backend is CopilotStudio.");
        }

        var options = configuration
            .GetSection(CopilotStudioOptions.SectionName)
            .Get<CopilotStudioOptions>();

        if (options is null)
        {
            throw new InvalidOperationException(
                "The CopilotStudio configuration section is required when Adapter:Backend is CopilotStudio.");
        }

        options.Validate();
    }

    static void ValidateAuthenticationConfiguration(AuthenticationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Authority) ||
            string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException(
                "Authentication Authority and Audience are required when authentication is enabled.");
        }
    }

    static void ValidateAdapterConfiguration(
        AdapterOptions options,
        AuthenticationOptions authenticationOptions)
    {
        if (!string.Equals(options.Backend, "Mock", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Backend, "CopilotStudio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Adapter:Backend must be either Mock or CopilotStudio.");
        }

        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "Adapter:PublicBaseUrl must be an absolute URL.");
        }

        if (options.IdempotencyTtlMinutes <= 0 ||
            options.ConversationTtlMinutes <= 0 ||
            options.ConversationHandleLifetimeMinutes <= 0 ||
            options.RequestTimeoutSeconds <= 0 ||
            options.MaxCacheEntries <= 0)
        {
            throw new InvalidOperationException(
                "Adapter TTL, timeout, and cache-size values must be greater than zero.");
        }

        if (options.MaxReplayTurns is < 0 or > AdapterConstants.MaxHistoryTurns ||
            options.MaxReplayCharacters < 0)
        {
            throw new InvalidOperationException(
                $"Adapter:MaxReplayTurns must be between 0 and {AdapterConstants.MaxHistoryTurns}, " +
                "and Adapter:MaxReplayCharacters must not be negative.");
        }

        foreach (var origin in options.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.AbsolutePath != "/")
            {
                throw new InvalidOperationException(
                    "Every Adapter:AllowedOrigins entry must be an HTTP or HTTPS origin without a path.");
            }
        }

        // Refuse to start unsecured by accident. Both the idempotency cache and the conversation
        // store partition on the caller identity, which does not exist without authentication.
        if (!authenticationOptions.Enabled && !options.AllowAnonymousDevelopmentMode)
        {
            throw new InvalidOperationException(
                "Authentication is disabled. Set Adapter:AllowAnonymousDevelopmentMode to true to " +
                "acknowledge that all callers share one identity partition. Never do this outside development.");
        }
    }
}
