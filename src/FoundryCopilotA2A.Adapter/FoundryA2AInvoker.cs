using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Adapter;

public sealed class FoundryA2AInvoker(
    AgentCatalog catalog,
    TokenCredential credential,
    IHttpClientFactory httpClientFactory,
    IOboTokenBroker tokenBroker,
    IOptions<AuthenticationOptions> authenticationOptions) : IAgentInvoker
{
    private static readonly TokenRequestContext TokenRequest =
        new(["https://ai.azure.com/.default"]);

    public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity("foundry.a2a.invoke");
        activity?.SetTag("foundry.agent.id", metadata.AgentId);
        activity?.SetTag("a2a.history.turns", metadata.History.Count);
        var agent = catalog.ResolveFoundryAgent(metadata.AgentId);
        // The Foundry A2A endpoint is invoked one turn at a time, so the caller's prior turns have
        // to travel with the prompt for the agent to keep context.
        var effectivePrompt = ConversationTranscript.Prepend(prompt, metadata.History);

        using var genAiActivity = GenAiTelemetry.StartInvokeAgent(
            GenAiTelemetry.Providers.AzureAiInference,
            agent.DisplayName,
            agent.Id,
            metadata.ContextId);
        CopilotInvocationResult result;
        try
        {
            result = await InvokeCoreAsync(
                agent,
                effectivePrompt,
                metadata,
                activity,
                cancellationToken);
        }
        catch (Exception exception)
        {
            GenAiTelemetry.RecordFailure(genAiActivity, exception);
            throw;
        }

        yield return new CopilotInvocationUpdate(
            result.Text,
            result.ConversationId,
            result.ResponseId)
        {
            Citations = result.Citations
        };
    }

    private async Task<CopilotInvocationResult> InvokeCoreAsync(
        ResolvedFoundryAgent agent,
        string effectivePrompt,
        A2ARequestMetadata metadata,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(metadata, cancellationToken);
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

        var answer = A2AResponseText.Read(document.RootElement);
        if (string.IsNullOrWhiteSpace(answer.Text))
        {
            throw new AdapterRequestException("Foundry A2A returned no text response.");
        }

        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return new CopilotInvocationResult(
            answer.Text,
            metadata.ContextId,
            requestId)
        {
            Citations = answer.Citations
        };
    }

    private async Task<string> AcquireTokenAsync(
        A2ARequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!authenticationOptions.Value.Enabled)
        {
            return (await credential.GetTokenAsync(TokenRequest, cancellationToken)).Token;
        }

        if (string.IsNullOrWhiteSpace(metadata.BearerToken) ||
            TokenInspector.IsAppOnly(metadata.BearerToken))
        {
            throw new UnauthorizedAccessException(
                "Foundry requires the caller's delegated user token. A shared developer or " +
                "managed identity cannot preserve per-user native A2A consent.");
        }

        try
        {
            return await tokenBroker.AcquireAsync(
                "https://ai.azure.com/.default", metadata, cancellationToken);
        }
        catch (MsalUiRequiredException exception)
        {
            throw new AdapterRequestException(
                "Delegated Foundry authorization requires consent or additional user interaction. " +
                "Configure delegated Foundry access on the existing backend registration and " +
                "grant the caller Foundry Agent Consumer access. No shared identity fallback was used.",
                exception);
        }
    }

}
