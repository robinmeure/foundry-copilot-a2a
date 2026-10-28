using System.Globalization;
using Aspire.Hosting.DevTunnels;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();

var adapterPort = builder.Configuration.GetValue("AdapterPort", 5099);
var frontendPort = builder.Configuration.GetValue("FrontendPort", 5173);
var chatbotPort = builder.Configuration.GetValue("ChatbotPort", 5174);
var knowledgeChatPort = builder.Configuration.GetValue("KnowledgeChatPort", 5175);
var knowledgePort = builder.Configuration.GetValue("KnowledgePort", 5100);
var requestTimeoutSeconds = builder.Configuration.GetValue("AdapterRequestTimeoutSeconds", 120);
int[] ports = [adapterPort, frontendPort, chatbotPort, knowledgeChatPort, knowledgePort];
if (ports.Any(port => port is < 1 or > 65535) || ports.Distinct().Count() != ports.Length)
{
    throw new InvalidOperationException(
        "AdapterPort, FrontendPort, ChatbotPort, KnowledgeChatPort and KnowledgePort must be distinct ports between 1 and 65535.");
}
if (requestTimeoutSeconds <= 0)
{
    throw new InvalidOperationException("AdapterRequestTimeoutSeconds must be positive.");
}
var frontendOrigin = $"http://localhost:{frontendPort}";
var chatbotOrigin = $"http://localhost:{chatbotPort}";
var knowledgeChatOrigin = $"http://localhost:{knowledgeChatPort}";
string ResolveParameterDefault(string name, string fallback = "") =>
    builder.Configuration[$"Parameters:{name}"] is { Length: > 0 } configured
        ? configured
        : fallback;

var openAiEndpoint = builder.AddParameter("openai-endpoint", ResolveParameterDefault("openai-endpoint"), publishValueAsDefault: true);
var chatDeployment = builder.AddParameter("chat-deployment", ResolveParameterDefault("chat-deployment", "gpt-6.1-sol"), publishValueAsDefault: true);
var embeddingDeployment = builder.AddParameter("embedding-deployment", ResolveParameterDefault("embedding-deployment", "text-embedding-3-large"), publishValueAsDefault: true);
var searchEndpoint = builder.AddParameter("search-endpoint", ResolveParameterDefault("search-endpoint"), publishValueAsDefault: true);
var searchIndex = builder.AddParameter("search-index", ResolveParameterDefault("search-index", "asrlocalrag"), publishValueAsDefault: true);
var speechEndpoint = builder.AddParameter("speech-endpoint", ResolveParameterDefault("speech-endpoint"), publishValueAsDefault: true);
var speechVoice = builder.AddParameter("speech-voice", ResolveParameterDefault("speech-voice", "nl-NL-FennaNeural"), publishValueAsDefault: true);
var documentEndpoint = builder.AddParameter("document-intelligence-endpoint", ResolveParameterDefault("document-intelligence-endpoint"), publishValueAsDefault: true);
var knowledgeBackend = builder.Configuration["KnowledgeBackend"] ?? "Mock";
var knowledge = builder.AddProject<Projects.FoundryCopilotA2A_KnowledgeApi>("knowledge", launchProfileName: "http")
    .WithEndpoint("http", endpoint => endpoint.Port = knowledgePort)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Knowledge__Backend", knowledgeBackend)
    .WithEnvironment("Knowledge__AllowedOrigins__0", knowledgeChatOrigin)
    .WithEnvironment("OpenAI__Endpoint", openAiEndpoint)
    .WithEnvironment("OpenAI__ChatDeployment", chatDeployment)
    .WithEnvironment("OpenAI__EmbeddingDeployment", embeddingDeployment)
    .WithEnvironment("Search__Endpoint", searchEndpoint)
    .WithEnvironment("Search__Index", searchIndex)
    .WithEnvironment("Speech__Endpoint", speechEndpoint)
    .WithEnvironment("Speech__Voice", speechVoice)
    .WithEnvironment("DocumentIntelligence__Endpoint", documentEndpoint)
    .WithEnvironment("DocumentIntelligence__ModelId", builder.Configuration["DocumentIntelligenceModel"] ?? "prebuilt-read");
if (builder.Configuration.GetValue("KnowledgeKamervragen:Enabled", false))
{
    knowledge
        .WithEnvironment("Kamervragen__Enabled", "true")
        .WithEnvironment("Kamervragen__Endpoint", builder.Configuration["KnowledgeKamervragen:Endpoint"])
        .WithEnvironment("Kamervragen__Index", builder.Configuration["KnowledgeKamervragen:Index"] ?? "kamervragen-openai");
}
if (knowledgeBackend == "Azure")
{
    knowledge
        .WithEnvironment("Authentication__TenantId", builder.AddParameter("knowledge-tenant-id"))
        .WithEnvironment("Authentication__ClientId", builder.AddParameter("knowledge-client-id"))
        .WithEnvironment("Authentication__ClientSecret", builder.AddParameter("knowledge-client-secret", secret: true));
}
var useLiveBackend = string.Equals(
    builder.Configuration["AdapterBackend"], "CopilotStudio", StringComparison.OrdinalIgnoreCase);
var gatewayBaseUrl = builder.Configuration["GatewayBaseUrl"]?.Trim().TrimEnd('/');
if (!string.IsNullOrEmpty(gatewayBaseUrl) &&
    (!Uri.TryCreate(gatewayBaseUrl, UriKind.Absolute, out var gatewayUri) ||
     gatewayUri.Scheme != Uri.UriSchemeHttps ||
     !string.IsNullOrEmpty(gatewayUri.UserInfo) ||
     !string.IsNullOrEmpty(gatewayUri.Query) ||
     !string.IsNullOrEmpty(gatewayUri.Fragment)))
{
    throw new InvalidOperationException(
        "GatewayBaseUrl must be an HTTPS API base URL without credentials, a query, or a fragment.");
}

var adapter = builder
    .AddProject<Projects.FoundryCopilotA2A_Adapter>("adapter", launchProfileName: "http")
    .WithEndpoint("http", endpoint => endpoint.Port = adapterPort)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Adapter__EnableFailureMock", "true")
    .WithEnvironment(
        "Adapter__RequestTimeoutSeconds", requestTimeoutSeconds.ToString(CultureInfo.InvariantCulture))
    .WithEnvironment("Adapter__AllowedOrigins__0", frontendOrigin)
    .WithEnvironment("Adapter__AllowedOrigins__1", chatbotOrigin);

if (builder.Configuration.GetValue("KnowledgeA2A:Enabled", false))
{
    knowledge
        .WithEnvironment("A2A__Enabled", "true")
        .WithEnvironment("A2A__AdapterBaseUrl", adapter.GetEndpoint("http"))
        .WithEnvironment("A2A__AuthenticationMode", builder.Configuration["KnowledgeA2A:AuthenticationMode"] ?? "OnBehalfOf")
        .WithEnvironment("A2A__DelegatedScope", builder.Configuration["KnowledgeA2A:DelegatedScope"])
        .WaitFor(adapter);

    var knowledgeAgentIds = builder.Configuration.GetSection("KnowledgeA2A:AllowedAgentIds").Get<string[]>() ?? [];
    for (var index = 0; index < knowledgeAgentIds.Length; index++)
    {
        knowledge.WithEnvironment($"A2A__AllowedAgentIds__{index}", knowledgeAgentIds[index]);
    }
}

if (builder.Configuration.GetValue("ApiManagementDiscovery:Enabled", false))
{
    adapter
        .WithEnvironment("ApiManagementDiscovery__Enabled", "true")
        .WithEnvironment(
            "ApiManagementDiscovery__SubscriptionId",
            builder.Configuration["ApiManagementDiscovery:SubscriptionId"])
        .WithEnvironment(
            "ApiManagementDiscovery__ResourceGroup",
            builder.Configuration["ApiManagementDiscovery:ResourceGroup"])
        .WithEnvironment(
            "ApiManagementDiscovery__ServiceName",
            builder.Configuration["ApiManagementDiscovery:ServiceName"]);

    var apiIds = builder.Configuration
        .GetSection("ApiManagementDiscovery:ApiIds")
        .Get<string[]>() ?? [];
    for (var index = 0; index < apiIds.Length; index++)
    {
        adapter.WithEnvironment($"ApiManagementDiscovery__ApiIds__{index}", apiIds[index]);
    }
}

var adapterPublicBaseUrl = string.IsNullOrEmpty(gatewayBaseUrl)
    ? builder.Configuration["AdapterPublicBaseUrl"]
    : gatewayBaseUrl;
if (string.IsNullOrWhiteSpace(adapterPublicBaseUrl))
{
    adapter.WithEnvironment("Adapter__PublicBaseUrl", adapter.GetEndpoint("http"));
}
else
{
    adapter.WithEnvironment("Adapter__PublicBaseUrl", adapterPublicBaseUrl);
}

if (builder.Configuration.GetValue("DevTunnel:Enabled", false))
{
    var configuredTunnelId = builder.Configuration["DevTunnel:TunnelId"]?.Trim();
    var configuredRegion = builder.Configuration["DevTunnel:Region"]?.Trim();
    DevTunnelRegion? region = null;
    if (!string.IsNullOrEmpty(configuredRegion))
    {
        region = Enum.TryParse<DevTunnelRegion>(configuredRegion, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"DevTunnel:Region must be one of: {string.Join(", ", Enum.GetNames<DevTunnelRegion>())}.");
    }
    builder
        .AddDevTunnel(
            "adapter-dev-tunnel",
            tunnelId: string.IsNullOrEmpty(configuredTunnelId) ? null : configuredTunnelId,
            options: new DevTunnelOptions
            {
                Description = "Public development ingress for the Foundry Copilot A2A adapter",
                // Without a region the service picks the nearest cluster, which can create a
                // second tunnel with the same ID and a new URL that APIM does not point at.
                Region = region
            })
        .WithReference(adapter.GetEndpoint("http"), allowAnonymous: true)
        .WaitFor(adapter);
}

var foundryAgentEndpoint = builder.Configuration["FoundryAgentEndpoint"];
if (!string.IsNullOrWhiteSpace(foundryAgentEndpoint))
{
    adapter
        .WithEnvironment("Foundry__Agents__web_research__Id", "web-research")
        .WithEnvironment(
            "Foundry__Agents__web_research__DisplayName",
            builder.Configuration["FoundryAgentDisplayName"] ?? "Foundry Web Research")
        .WithEnvironment(
            "Foundry__Agents__web_research__Endpoint",
            foundryAgentEndpoint);

    ConfigureChainTargets("FoundryChainTargetAgent", "Foundry__Agents__web_research");
}

if (useLiveBackend)
{
    var tenantId = builder.AddParameter("copilot-studio-tenant-id");
    var clientId = builder.AddParameter("copilot-studio-client-id");
    var clientSecret = builder.AddParameter("copilot-studio-client-secret", secret: true);
    var tweedeKamerDirectConnectUrl =
        builder.AddParameter("copilot-studio-direct-connect-url", secret: true);
    var reverserClassicDirectConnectUrl =
        builder.AddParameter("copilot-studio-reverser-direct-connect-url", secret: true);
    var reverserNewDirectConnectUrl =
        builder.AddParameter("copilot-studio-reverser-new-direct-connect-url", secret: true);
    var tweedeKamerClassicDirectConnectUrl =
        builder.AddParameter("copilot-studio-tweede-kamer-classic-direct-connect-url", secret: true);
    var aiSearchVragenDirectConnectUrl =
        builder.AddParameter("copilot-studio-ai-search-vragen-direct-connect-url", secret: true);
    var orchestratorDirectConnectUrl =
        builder.AddParameter("copilot-studio-orchestrator-direct-connect-url", secret: true);
    var authority = builder.AddParameter("authentication-authority");
    var audience = builder.AddParameter("authentication-audience");

    adapter
        .WithEnvironment("Adapter__Backend", "CopilotStudio")
        .WithEnvironment("Authentication__Enabled", "true")
        .WithEnvironment("Authentication__Authority", authority)
        .WithEnvironment("Authentication__Audience", audience)
        .WithEnvironment("CopilotStudio__TenantId", tenantId)
        .WithEnvironment("CopilotStudio__ClientId", clientId)
        .WithEnvironment("CopilotStudio__ClientSecret", clientSecret)
        .WithEnvironment("CopilotStudio__DefaultAgent", "reverser-classic");

    adapter
        .WithEnvironment("CopilotStudio__Agents__tweede-kamer__DisplayName", "Tweede Kamer")
        .WithEnvironment("CopilotStudio__Agents__tweede-kamer__Harness", "GitHubCopilot")
        .WithEnvironment(
            "CopilotStudio__Agents__tweede-kamer__DirectConnectUrl",
            tweedeKamerDirectConnectUrl);

    adapter
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-classic__DisplayName",
            "Reverser Classic")
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-classic__DirectConnectUrl",
            reverserClassicDirectConnectUrl);

    adapter
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-new__DisplayName",
            "Reverser New")
        .WithEnvironment("CopilotStudio__Agents__reverser-new__Harness", "GitHubCopilot")
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-new__DirectConnectUrl",
            reverserNewDirectConnectUrl);

    // This sibling uses the standard SDK harness, so it needs no harness override.
    adapter
        .WithEnvironment(
            "CopilotStudio__Agents__tweede-kamer-classic__DisplayName",
            "Tweede Kamer Classic")
        .WithEnvironment(
            "CopilotStudio__Agents__tweede-kamer-classic__DirectConnectUrl",
            tweedeKamerClassicDirectConnectUrl);

    adapter
        .WithEnvironment(
            "CopilotStudio__Agents__ai-search-vragen__DisplayName",
            "AI Search Vragen")
        .WithEnvironment(
            "CopilotStudio__Agents__ai-search-vragen__DirectConnectUrl",
            aiSearchVragenDirectConnectUrl);

    adapter
        .WithEnvironment(
            "CopilotStudio__Agents__orchestrator__DisplayName",
            "Orchestrator")
        .WithEnvironment(
            "CopilotStudio__Agents__orchestrator__DirectConnectUrl",
            orchestratorDirectConnectUrl);
    ConfigureChainTargets("CopilotStudioChainTargetAgent", "CopilotStudio__Agents__orchestrator");
}
else
{
    adapter
        .WithEnvironment("Adapter__Backend", "Mock")
        .WithEnvironment("Adapter__AllowAnonymousDevelopmentMode", "true")
        .WithEnvironment("Authentication__Enabled", "false");
}

builder
    .AddViteApp("frontend", "../FoundryCopilotA2A.Web")
    .WithEndpoint("http", endpoint => endpoint.Port = frontendPort)
    .WithEnvironment("VITE_ADAPTER_BASE_URL", adapter.GetEndpoint("http"))
    .WithEnvironment("VITE_GATEWAY_BASE_URL", gatewayBaseUrl ?? "")
    .WithReference(adapter)
    .WaitFor(adapter)
    .WithExternalHttpEndpoints();

// Both chat apps share one npm workspace rooted at the repository. Install it once, then start
// each app independently so neither depends on the other's backend.
var chatWorkspace = builder
    .AddExecutable("chat-workspace-install", "npm", "../..", "install", "--no-audit", "--no-fund")
    .ExcludeFromManifest();

var chatbot = builder
    .AddViteApp("chatbot", "../FoundryCopilotA2A.Chatbot")
    .WithNpm(install: false)
    .WaitForCompletion(chatWorkspace)
    .WithEndpoint("http", endpoint => endpoint.Port = chatbotPort)
    .WithEnvironment("VITE_ADAPTER_BASE_URL", adapter.GetEndpoint("http"))
    .WithEnvironment(
        "VITE_ORCHESTRATOR_AGENT_ID",
        builder.Configuration["ChatbotOrchestratorAgentId"] ?? (useLiveBackend ? "orchestrator" : "mock"))
    .WithEnvironment("VITE_CHATBOT_AUTH_MODE", useLiveBackend ? "entra" : "anonymous")
    .WithReference(adapter)
    .WaitFor(adapter)
    .WithExternalHttpEndpoints();

var knowledgeChat = builder
    .AddViteApp("knowledge-chat", "../FoundryCopilotA2A.KnowledgeChat")
    .WithNpm(install: false)
    .WithEndpoint("http", endpoint => endpoint.Port = knowledgeChatPort)
    .WithEnvironment("VITE_KNOWLEDGE_BASE_URL", knowledge.GetEndpoint("http"))
    .WithEnvironment("VITE_KNOWLEDGE_CHAT_AUTH_MODE", knowledgeBackend == "Azure" ? "entra" : "anonymous")
    .WithReference(knowledge)
    .WaitFor(knowledge)
    .WaitForCompletion(chatWorkspace)
    .WithExternalHttpEndpoints();

// An empty value would override the app's .env.local, so only set it when configured.
if (builder.Configuration["Parameters:knowledge-client-id"] is { Length: > 0 } knowledgeClientId)
    knowledgeChat.WithEnvironment("VITE_KNOWLEDGE_API_CLIENT_ID", knowledgeClientId);

var chatbotGatewayBaseUrl = builder.Configuration["ChatbotGatewayBaseUrl"] ?? gatewayBaseUrl;
if (chatbotGatewayBaseUrl is not null)
{
    chatbot.WithEnvironment("VITE_GATEWAY_BASE_URL", chatbotGatewayBaseUrl);
}

foreach (var setting in new Dictionary<string, string>
{
    ["ChatbotOrchestratorName"] = "VITE_ORCHESTRATOR_NAME",
    ["ChatbotTenantId"] = "VITE_ENTRA_TENANT_ID",
    ["ChatbotSpaClientId"] = "VITE_ENTRA_CLIENT_ID",
    ["ChatbotApiClientId"] = "VITE_ADAPTER_API_CLIENT_ID",
    ["ChatbotSpeechInputEnabled"] = "VITE_SPEECH_INPUT_ENABLED"
})
{
    if (builder.Configuration[setting.Key] is { } value)
    {
        chatbot.WithEnvironment(setting.Value, value);
    }
}

// The Knowledge Chat may share the chatbot's tenant and SPA registration (with its own redirect URI).
foreach (var (key, fallback, variable) in new[]
{
    ("KnowledgeChatTenantId", "ChatbotTenantId", "VITE_ENTRA_TENANT_ID"),
    ("KnowledgeChatSpaClientId", "ChatbotSpaClientId", "VITE_ENTRA_CLIENT_ID"),
    ("KnowledgeChatSpeechInputEnabled", "ChatbotSpeechInputEnabled", "VITE_SPEECH_INPUT_ENABLED"),
})
{
    if ((builder.Configuration[key] ?? builder.Configuration[fallback]) is { } value)
    {
        knowledgeChat.WithEnvironment(variable, value);
    }
}

builder.Build().Run();

void ConfigureChainTargets(string setting, string agentOptionsPath)
{
    var configuredTargets = builder.Configuration[setting];
    if (string.IsNullOrWhiteSpace(configuredTargets))
    {
        return;
    }

    var targets = configuredTargets
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    for (var index = 0; index < targets.Length; index++)
    {
        adapter.WithEnvironment($"{agentOptionsPath}__ChainTargets__{index}", targets[index]);
    }
}
