using System.Diagnostics;
using A2A;
using A2A.AspNetCore;
using A2A.V0_3Compat;
using Azure.Core;
using Azure.Identity;
using FoundryCopilotA2A.Adapter;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var adapterOptions = builder.Configuration
    .GetSection(AdapterOptions.SectionName)
    .Get<AdapterOptions>() ?? new AdapterOptions();
var authenticationOptions = builder.Configuration
    .GetSection(AuthenticationOptions.SectionName)
    .Get<AuthenticationOptions>() ?? new AuthenticationOptions();

ValidateAdapterConfiguration(adapterOptions, authenticationOptions);

builder.Services.AddHttpContextAccessor();
var traceStore = new SanitizedTraceStore(
    Microsoft.Extensions.Options.Options.Create(adapterOptions));
builder.Services.AddSingleton(traceStore);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AdapterTelemetry.ActivitySourceName)
        .AddSource(GenAiTelemetry.ActivitySourceName)
        .AddProcessor(new SanitizedTraceProcessor(traceStore)));
builder.Services.AddCors(options =>
{
    options.AddPolicy("spa", policy =>
    {
        policy
            .WithOrigins(adapterOptions.AllowedOrigins)
            .WithMethods(HttpMethods.Get, HttpMethods.Post)
            .WithHeaders(
                "Authorization",
                "Content-Type",
                "A2A-Version",
                AdapterConstants.AgentHeaderName,
                AdapterConstants.ChainTargetHeaderName)
            .WithExposedHeaders(AdapterConstants.TraceHeaderName);
    });
});

// ServiceDefaults applies the standard resilience handler and instruments outbound requests.
builder.Services.AddTransient<CopilotStudioTraceHandler>();
builder.Services.AddHttpClient("copilot-studio")
    .AddHttpMessageHandler<CopilotStudioTraceHandler>();

builder.Services.Configure<AdapterOptions>(
    builder.Configuration.GetSection(AdapterOptions.SectionName));
builder.Services.Configure<AuthenticationOptions>(
    builder.Configuration.GetSection(AuthenticationOptions.SectionName));
builder.Services.Configure<CopilotStudioOptions>(
    builder.Configuration.GetSection(CopilotStudioOptions.SectionName));
builder.Services.Configure<FoundryOptions>(
    builder.Configuration.GetSection(FoundryOptions.SectionName));
builder.Services.AddSingleton<A2ARequestMetadataAccessor>();
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<CopilotConversationStore>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<OboTokenBroker>();

if (adapterOptions.UseMockBackend)
{
    builder.Services.AddSingleton<ICopilotStudioInvoker, MockCopilotStudioInvoker>();
}
else
{
    ValidateRealBackendConfiguration(builder.Configuration, authenticationOptions);
    builder.Services.AddSingleton<ICopilotStudioInvoker, SdkCopilotStudioInvoker>();
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
    builder.Services.UseClaimsBasedAgentIsolation(new() { ClaimType = "oid" });
}

var hostedAgent = builder.AddAIAgent(
    AdapterConstants.AgentName,
    (services, _) => services
        .GetRequiredService<CopilotStudioAdapterChatClient>()
        .AsAIAgent(
            instructions: "Delegate the user's request to the configured Copilot Studio agent.",
            name: AdapterConstants.AgentName,
            description: "A2A facade for a Copilot Studio specialist agent."));

hostedAgent.AddA2AServer();

var app = builder.Build();

app.UseCors("spa");

if (authenticationOptions.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Must run after authentication so the caller's claims are available to the metadata accessor.
app.UseMiddleware<A2ARequestContextMiddleware>();
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        A2ARequestContextMiddleware.TryResolveRuntimeAgent(
            context.Request.Path,
            out _) &&
        Activity.Current is { } activity)
    {
        var metadata = context.RequestServices
            .GetRequiredService<A2ARequestMetadataAccessor>()
            .Current;
        traceStore.Register(activity.TraceId, metadata.UserId, metadata.AgentId);
        context.Response.Headers[AdapterConstants.TraceHeaderName] = activity.TraceId.ToHexString();
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet(
    AdapterConstants.AgentsPath,
    (AgentCatalog catalog) => Results.Ok(new
    {
        defaultAgentId = catalog.DefaultAgentId,
        agents = catalog.Agents
    }));
var traceEndpoint = app.MapGet(
    $"{AdapterConstants.TracesPath}/{{traceId}}",
    (
        string traceId,
        HttpContext context,
        SanitizedTraceStore store,
        A2ARequestMetadataAccessor metadataAccessor) =>
    {
        if (traceId.Length != 32 || !traceId.All(Uri.IsHexDigit))
        {
            return Results.NotFound();
        }

        return store.TryGet(traceId, metadataAccessor.ResolveUserId(context), out var trace)
            ? Results.Ok(trace)
            : Results.NotFound();
    });
if (authenticationOptions.Enabled)
{
    traceEndpoint.RequireAuthorization();
}

var a2aServer = app.Services.GetRequiredKeyedService<A2AServer>(AdapterConstants.AgentName);
var runtimeEndpoint = app.MapA2AWithV03Compat(a2aServer, AdapterConstants.RuntimePath);
if (authenticationOptions.Enabled)
{
    runtimeEndpoint.RequireAuthorization();
}

var publicBaseUrl = adapterOptions.PublicBaseUrl.TrimEnd('/');
var chainAgents = app.Services.GetRequiredService<AgentCatalog>().Agents
    .Where(agent =>
        agent.ProviderKind == FoundryCopilotA2A.Adapter.AgentProvider.CopilotStudio &&
        agent.Supported)
    .ToArray();
foreach (var chainAgent in chainAgents)
{
    var chainRuntimePath = AdapterConstants.ChainAgentRuntimePath(chainAgent.Id);
    var chainRuntimeEndpoint = app.MapA2AWithV03Compat(a2aServer, chainRuntimePath);
    if (authenticationOptions.Enabled)
    {
        chainRuntimeEndpoint.RequireAuthorization();
    }
}

IResult BuildChainAgentCard(string agentId, AgentCatalog catalog)
{
    AgentDescriptor agent;
    try
    {
        agent = catalog.ResolveAgent(agentId);
    }
    catch (AdapterRequestException)
    {
        return Results.NotFound();
    }

    if (agent.ProviderKind != FoundryCopilotA2A.Adapter.AgentProvider.CopilotStudio)
    {
        return Results.NotFound();
    }

    var chainRuntimeUrl =
        $"{publicBaseUrl}{AdapterConstants.ChainAgentRuntimePath(agent.Id)}";

    // When the runtime is protected, the card must advertise how to authenticate. A card that
    // omits securitySchemes tells a remote caller the endpoint is anonymous, so it never attaches
    // a credential and never prompts the user to consent.
    object? securitySchemes = null;
    object[]? security = null;
    if (authenticationOptions.Enabled &&
        !string.IsNullOrWhiteSpace(authenticationOptions.Authority) &&
        !string.IsNullOrWhiteSpace(authenticationOptions.Audience))
    {
        // Authority is the OIDC issuer form (".../{tenant}/v2.0"), but the authorize/token
        // endpoints hang off the tenant root, so strip the version segment before composing them.
        var authority = authenticationOptions.Authority.TrimEnd('/');
        if (authority.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            authority = authority[..^"/v2.0".Length];
        }

        var scope = $"{authenticationOptions.Audience.TrimEnd('/')}/access_as_user";
        securitySchemes = new Dictionary<string, object>
        {
            ["entra"] = new
            {
                type = "oauth2",
                description = "Microsoft Entra ID delegated access to the adapter API.",
                flows = new
                {
                    authorizationCode = new
                    {
                        authorizationUrl = $"{authority}/oauth2/v2.0/authorize",
                        tokenUrl = $"{authority}/oauth2/v2.0/token",
                        refreshUrl = $"{authority}/oauth2/v2.0/token",
                        scopes = new Dictionary<string, string>
                        {
                            [scope] = "Invoke the adapter on behalf of the signed-in user."
                        }
                    }
                }
            }
        };
        security = [new Dictionary<string, string[]> { ["entra"] = [scope] }];
    }

    return Results.Ok(new
    {
        name = agent.DisplayName,
        description = "Copilot Studio specialist exposed through the local A2A adapter.",
        url = chainRuntimeUrl,
        version = "0.1.0",
        // Mirror the shape the A2A library emits for the root card. The card format version stays
        // 0.3.0 while supportedInterfaces advertises the 1.0 binding; setting this to 1.0 does not
        // upgrade the card and drops it out of the shape remote callers expect.
        protocolVersion = "0.3.0",
        capabilities = new
        {
            streaming = true,
            pushNotifications = false,
            stateTransitionHistory = false,
            extensions = Array.Empty<object>()
        },
        securitySchemes,
        security,
        defaultInputModes = new[] { "text/plain" },
        defaultOutputModes = new[] { "text/plain" },
        skills = new[]
        {
            new
            {
                id = $"copilot-studio-{agent.Id}",
                name = agent.DisplayName,
                description = $"Delegate a request to {agent.DisplayName}.",
                tags = new[] { "copilot-studio", "specialist" },
                examples = new[] { $"Ask {agent.DisplayName} to handle this request." }
            }
        },
        supportsAuthenticatedExtendedCard = false,
        additionalInterfaces = Array.Empty<object>(),
        preferredTransport = "JSONRPC",
        supportedInterfaces = new[]
        {
            new
            {
                url = chainRuntimeUrl,
                protocolBinding = "JSONRPC",
                protocolVersion = "1.0"
            }
        }
    });
}

// Serve the chain card at both conventional discovery locations. Remote callers resolve either
// the sibling path or "<target>/.well-known/agent-card.json"; serving only the sibling made the
// target-relative probe 404 and fall back to the root card, which points at the generic router
// instead of the chain-bound runtime.
app.MapGet(
    $"{AdapterConstants.ChainAgentsPath}/{{agentId}}/.well-known/agent-card.json",
    (string agentId, AgentCatalog catalog) => BuildChainAgentCard(agentId, catalog));
app.MapGet(
    $"{AdapterConstants.ChainAgentsPath}/{{agentId}}/a2a/.well-known/agent-card.json",
    (string agentId, AgentCatalog catalog) => BuildChainAgentCard(agentId, catalog));

var runtimeUrl = $"{publicBaseUrl}{AdapterConstants.RuntimePath}";
var agentCard = new AgentCard
{
    Name = "Specialist Agent Router",
    Description = "Delegates requests to the selected Copilot Studio or Foundry agent.",
    Version = "0.1.0",
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Capabilities = new AgentCapabilities
    {
        Streaming = true,
        PushNotifications = false
    },
    SupportedInterfaces =
    [
        new AgentInterface
        {
            Url = runtimeUrl,
            ProtocolBinding = "JSONRPC",
            ProtocolVersion = "1.0"
        }
    ],
    Skills =
    [
        new A2A.AgentSkill
        {
            Id = "specialist-agent",
            Name = "Specialist agent",
            Description = "Uses the selected Copilot Studio or Foundry agent to answer a request.",
            Tags = ["copilot-studio", "foundry", "specialist"],
            Examples = ["Ask the selected specialist agent to handle this request."]
        }
    ]
};
app.MapAgentCardGetWithV03Compat(() => Task.FromResult(agentCard));

app.Run();

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
        options.RequestTimeoutSeconds <= 0 ||
        options.MaxCacheEntries <= 0)
    {
        throw new InvalidOperationException(
            "Adapter TTL, timeout, and cache-size values must be greater than zero.");
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

public partial class Program;
