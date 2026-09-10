using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FoundryCopilotA2A.Adapter;

public interface IAgentInvoker
{
    IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed class RoutingAgentInvoker(
    AgentCatalog catalog,
    ApiManagementAgentRegistry apiManagementAgents,
    ICopilotStudioInvoker copilotStudioInvoker,
    FoundryA2AInvoker foundryInvoker,
    ApiManagementA2AInvoker apiManagementInvoker) : IAgentInvoker
{
    public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agent = catalog.TryResolveAgent(metadata.AgentId, out var configuredAgent)
            ? configuredAgent!
            : (await apiManagementAgents.TryResolveAsync(metadata.AgentId, cancellationToken) is { } apiAgent
                ? new AgentDescriptor(
                    apiAgent.Id,
                    apiAgent.DisplayName,
                    AgentProvider.ApiManagement,
                    true,
                    null,
                    [])
                : throw new AdapterRequestException(
                    $"Agent '{metadata.AgentId}' is not configured."));
        var target = metadata.ChainTargetAgentId is null
            ? null
            : catalog.ResolveChainTarget(agent.Id, metadata.ChainTargetAgentId);
        using var workflow = target is null
            ? null
            : GenAiTelemetry.StartChainWorkflow(agent.Id, target.Id);
        workflow?.SetTag("a2a.chain.enabled", true);
        workflow?.SetTag("a2a.chain.target_agent", target?.Id);

        // A selected target is a request to the native orchestrator, not evidence of a tool call.
        // Record execution only when a caller actually enters the target-specific A2A runtime.
        using var tool = metadata.IsAgentRoute
            ? GenAiTelemetry.StartExecuteTool(
                agent.DisplayName,
                "agent",
                metadata.ContextId,
                "Target-specific A2A invocation of a Copilot Studio specialist.")
            : null;
        tool?.SetTag("copilot_studio.agent.id", agent.Id);
        var effectivePrompt = target is null
            ? prompt
            : $"Delegate the request below to the configured A2A tool named " +
              $"\"{target.DisplayName}\". You must call that A2A tool before answering, " +
              $"then return its result clearly.\n\nUser request:\n{prompt}";
        var updates = agent.ProviderKind switch
        {
            AgentProvider.CopilotStudio => copilotStudioInvoker.StreamAsync(
                effectivePrompt, metadata, cancellationToken),
            AgentProvider.Foundry => foundryInvoker.StreamAsync(
                effectivePrompt, metadata, cancellationToken),
            AgentProvider.ApiManagement => apiManagementInvoker.StreamAsync(
                effectivePrompt, metadata, cancellationToken),
            _ => throw new AdapterRequestException(
                $"Agent '{agent.Id}' has an unsupported provider.")
        };

        await using var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (Exception exception)
            {
                GenAiTelemetry.RecordFailure(tool, exception);
                GenAiTelemetry.RecordFailure(workflow, exception);
                throw;
            }

            if (!hasNext)
            {
                tool?.SetStatus(ActivityStatusCode.Ok);
                workflow?.SetStatus(ActivityStatusCode.Ok);
                yield break;
            }

            yield return enumerator.Current;
        }
    }
}
