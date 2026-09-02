var builder = DistributedApplication.CreateBuilder(args);

var adapter = builder
    .AddProject<Projects.FoundryCopilotA2A_Adapter>("adapter", launchProfileName: "http")
    .WithEndpoint("http", endpoint => endpoint.Port = 5099)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Adapter__EnableFailureMock", "true")
    .WithEnvironment("Adapter__AllowedOrigins__0", "http://localhost:5173");

var adapterPublicBaseUrl = builder.Configuration["AdapterPublicBaseUrl"];
if (string.IsNullOrWhiteSpace(adapterPublicBaseUrl))
{
    adapter.WithEnvironment("Adapter__PublicBaseUrl", adapter.GetEndpoint("http"));
}
else
{
    adapter.WithEnvironment("Adapter__PublicBaseUrl", adapterPublicBaseUrl);
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

    // Accepts a comma-separated list so one Foundry agent can offer several Copilot Studio
    // specialists as chain targets.
    var foundryChainTargetAgent = builder.Configuration["FoundryChainTargetAgent"];
    if (!string.IsNullOrWhiteSpace(foundryChainTargetAgent))
    {
        var chainTargets = foundryChainTargetAgent
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < chainTargets.Length; index++)
        {
            adapter.WithEnvironment(
                $"Foundry__Agents__web_research__ChainTargets__{index}",
                chainTargets[index]);
        }
    }
}

if (string.Equals(
    builder.Configuration["AdapterBackend"],
    "CopilotStudio",
    StringComparison.OrdinalIgnoreCase))
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
        .WithEnvironment("CopilotStudio__DefaultAgent", "reverser-classic")
        .WithEnvironment("CopilotStudio__Agents__tweede-kamer__DisplayName", "Tweede Kamer")
        .WithEnvironment("CopilotStudio__Agents__tweede-kamer__Harness", "GitHubCopilot")
        .WithEnvironment(
            "CopilotStudio__Agents__tweede-kamer__DirectConnectUrl",
            tweedeKamerDirectConnectUrl)
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-classic__DisplayName",
            "Reverser Classic")
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-classic__DirectConnectUrl",
            reverserClassicDirectConnectUrl)
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-new__DisplayName",
            "Reverser New")
        .WithEnvironment("CopilotStudio__Agents__reverser-new__Harness", "GitHubCopilot")
        .WithEnvironment(
            "CopilotStudio__Agents__reverser-new__DirectConnectUrl",
            reverserNewDirectConnectUrl)
        // Standard-harness sibling of tweede-kamer, so it needs no Harness override and is
        // reachable through the Microsoft 365 Agents SDK client.
        .WithEnvironment(
            "CopilotStudio__Agents__tweede-kamer-classic__DisplayName",
            "Tweede Kamer Classic")
        .WithEnvironment(
            "CopilotStudio__Agents__tweede-kamer-classic__DirectConnectUrl",
            tweedeKamerClassicDirectConnectUrl)
        .WithEnvironment(
            "CopilotStudio__Agents__orchestrator__DisplayName",
            "Orchestrator")
        .WithEnvironment(
            "CopilotStudio__Agents__orchestrator__DirectConnectUrl",
            orchestratorDirectConnectUrl);
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
    .WithEndpoint("http", endpoint => endpoint.Port = 5173)
    .WithEnvironment("VITE_ADAPTER_BASE_URL", adapter.GetEndpoint("http"))
    .WithReference(adapter)
    .WaitFor(adapter)
    .WithExternalHttpEndpoints();

builder.Build().Run();
