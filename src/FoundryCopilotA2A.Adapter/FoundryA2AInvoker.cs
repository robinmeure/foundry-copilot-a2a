using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace FoundryCopilotA2A.Adapter;

public interface IAgentInvoker
{
    Task<CopilotInvocationResult> InvokeAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed class RoutingAgentInvoker(
    AgentCatalog catalog,
    ICopilotStudioInvoker copilotStudioInvoker,
    FoundryA2AInvoker foundryInvoker) : IAgentInvoker
{
    public Task<CopilotInvocationResult> InvokeAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken) =>
        catalog.ResolveAgent(metadata.AgentId).ProviderKind switch
        {
            AgentProvider.CopilotStudio => copilotStudioInvoker.InvokeAsync(
                prompt, metadata, cancellationToken),
            AgentProvider.Foundry => foundryInvoker.InvokeAsync(
                prompt, metadata, cancellationToken),
            _ => throw new AdapterRequestException(
                $"Agent '{metadata.AgentId}' has an unsupported provider.")
        };
}

public sealed class FoundryA2AInvoker(
    AgentCatalog catalog,
    TokenCredential credential,
    IHttpClientFactory httpClientFactory) : IAgentInvoker
{
    private static readonly TokenRequestContext TokenRequest =
        new(["https://ai.azure.com/.default"]);

    public async Task<CopilotInvocationResult> InvokeAsync(
        string prompt,
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("foundry.a2a.invoke");
        activity?.SetTag("foundry.agent.id", metadata.AgentId);
        activity?.SetTag("a2a.history.turns", metadata.History.Count);
        var agent = catalog.ResolveFoundryAgent(metadata.AgentId);
        var effectivePrompt = prompt;
        AgentDescriptor? chainTarget = null;
        if (metadata.ChainTargetAgentId is not null)
        {
            var target = catalog.ResolveChainTarget(
                metadata.AgentId,
                metadata.ChainTargetAgentId);
            chainTarget = target;
            activity?.SetTag("a2a.chain.enabled", true);
            activity?.SetTag("a2a.chain.target_agent", target.Id);
            effectivePrompt =
                $"Delegate the request below to the configured A2A tool named " +
                $"\"{target.DisplayName}\". You must call that A2A tool before answering, " +
                $"then return its result clearly.\n\nUser request:\n{prompt}";
        }

        // The Foundry A2A endpoint is invoked one turn at a time, so the caller's prior turns have
        // to travel with the prompt for the agent to keep context.
        effectivePrompt = ConversationTranscript.Prepend(effectivePrompt, metadata.History);

        // A chained request is a two-agent workflow, so wrap the remote call in a workflow span
        // and nest the delegated tool underneath it. Direct calls emit only the agent span.
        using var workflowActivity = chainTarget is null
            ? null
            : GenAiTelemetry.StartChainWorkflow(agent.Id, chainTarget.Id);
        using var genAiActivity = GenAiTelemetry.StartInvokeAgent(
            GenAiTelemetry.Providers.AzureAiInference,
            agent.DisplayName,
            agent.Id,
            metadata.ContextId);
        using var toolActivity = chainTarget is null
            ? null
            : GenAiTelemetry.StartExecuteTool(
                chainTarget.DisplayName,
                "agent",
                metadata.ContextId,
                $"Copilot Studio specialist reached through the A2A adapter.");

        try
        {
            return await InvokeCoreAsync(
                agent,
                effectivePrompt,
                metadata,
                activity,
                cancellationToken);
        }
        catch (Exception exception)
        {
            GenAiTelemetry.RecordFailure(toolActivity, exception);
            GenAiTelemetry.RecordFailure(genAiActivity, exception);
            GenAiTelemetry.RecordFailure(workflowActivity, exception);
            throw;
        }
    }

    private async Task<CopilotInvocationResult> InvokeCoreAsync(
        ResolvedFoundryAgent agent,
        string effectivePrompt,
        A2ARequestMetadata metadata,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken)
    {        var token = await credential.GetTokenAsync(TokenRequest, cancellationToken);
        var requestId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "SendMessage",
            @params = new
            {
                message = new
                {
                    role = "ROLE_USER",
                    parts = new[] { new { text = effectivePrompt } },
                    messageId = metadata.MessageId,
                    contextId = metadata.ContextId
                }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, agent.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Add("A2A-Version", "1.0");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClientFactory
            .CreateClient("foundry-a2a")
            .SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AdapterRequestException(
                $"Foundry A2A returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var errorMessage)
                ? errorMessage.GetString()
                : null;
            throw new AdapterRequestException(
                $"Foundry A2A failed: {message ?? "unknown JSON-RPC error"}.");
        }

        var text = ExtractText(document.RootElement);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AdapterRequestException("Foundry A2A returned no text response.");
        }

        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return new CopilotInvocationResult(
            text,
            metadata.ContextId,
            requestId);
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result))
        {
            return string.Empty;
        }

        if (result.TryGetProperty("message", out var message) &&
            message.TryGetProperty("parts", out var messageParts))
        {
            return JoinPartText(messageParts);
        }

        if (result.TryGetProperty("parts", out var resultParts))
        {
            return JoinPartText(resultParts);
        }

        if (!result.TryGetProperty("task", out var task))
        {
            return string.Empty;
        }

        if (task.TryGetProperty("artifacts", out var artifacts) &&
            artifacts.ValueKind == JsonValueKind.Array)
        {
            var artifactText = string.Join(
                Environment.NewLine,
                artifacts.EnumerateArray()
                    .Where(artifact => artifact.TryGetProperty("parts", out _))
                    .Select(artifact => JoinPartText(artifact.GetProperty("parts")))
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            if (!string.IsNullOrWhiteSpace(artifactText))
            {
                return artifactText;
            }
        }

        return task.TryGetProperty("status", out var status) &&
               status.TryGetProperty("message", out var statusMessage) &&
               statusMessage.TryGetProperty("parts", out var statusParts)
            ? JoinPartText(statusParts)
            : string.Empty;
    }

    private static string JoinPartText(JsonElement parts) =>
        parts.ValueKind == JsonValueKind.Array
            ? string.Join(
                Environment.NewLine,
                parts.EnumerateArray()
                    .Where(part => part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text)))
            : string.Empty;
}
