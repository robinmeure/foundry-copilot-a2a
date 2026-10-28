using A2A;
using A2A.V0_3Compat;
using Microsoft.Extensions.AI;


namespace FoundryCopilotA2A.Adapter;

internal static class AdapterEndpoints
{
    public static void MapAdapterEndpoints(
        this WebApplication app,
        AdapterOptions adapterOptions,
        AuthenticationOptions authenticationOptions)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        var connectivityEndpoint = app.MapGet("/api/connectivity", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(ConnectivityDiagnostics.Create(adapterOptions, authenticationOptions));
        });
        if (authenticationOptions.Enabled)
        {
            connectivityEndpoint.RequireAuthorization();
        }
        app.MapGet(
            AdapterConstants.AgentsPath,
            async (
                AgentCatalog catalog,
                ApiManagementAgentRegistry apiManagementAgents,
                CancellationToken cancellationToken) =>
            {
                var discovered = await apiManagementAgents.GetAgentsAsync(cancellationToken);
                var agents = catalog.Agents.Concat(discovered).ToArray();
                var duplicate = agents
                    .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate is not null)
                {
                    throw new InvalidOperationException(
                        $"Agent ID '{duplicate.Key}' is configured locally and discovered from APIM.");
                }

                return Results.Ok(new
                {
                    defaultAgentId = catalog.DefaultAgentId,
                    agents
                });
            });
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

        var chainAgents = app.Services.GetRequiredService<AgentCatalog>().Agents
            .Where(agent =>
                agent.ProviderKind is FoundryCopilotA2A.Adapter.AgentProvider.CopilotStudio or
                    FoundryCopilotA2A.Adapter.AgentProvider.Foundry &&
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
    }
}
