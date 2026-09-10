using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Adapter;

public sealed class ApiManagementA2AInvoker(
    ApiManagementAgentRegistry registry,
    IHttpClientFactory httpClientFactory) : IAgentInvoker
{
    public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
        string prompt,
        A2ARequestMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agent = await registry.TryResolveAsync(metadata.AgentId, cancellationToken) ??
                    throw new AdapterRequestException(
                        $"API Management agent '{metadata.AgentId}' is not configured.");
        var effectivePrompt = ConversationTranscript.Prepend(prompt, metadata.History);
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

        using var activity = AdapterTelemetry.StartActivity("apim.a2a.invoke");
        activity?.SetTag("apim.api.id", agent.ApiId);
        activity?.SetTag("a2a.agent.id", agent.Id);
        using var genAiActivity = GenAiTelemetry.StartInvokeAgent(
            "azure.apim",
            agent.DisplayName,
            agent.Id,
            metadata.ContextId);
        CopilotInvocationUpdate update;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, agent.Endpoint);
            if (!string.IsNullOrWhiteSpace(metadata.BearerToken))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", metadata.BearerToken);
            }
            request.Headers.Add("A2A-Version", "1.0");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await httpClientFactory
                .CreateClient("apim-a2a")
                .SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new AdapterRequestException(
                    $"API Management A2A returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString()
                    : null;
                throw new AdapterRequestException(
                    $"API Management A2A failed: {message ?? "unknown JSON-RPC error"}.");
            }

            var text = A2AResponseText.Extract(document.RootElement);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new AdapterRequestException(
                    "API Management A2A returned no text response.");
            }

            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            update = new CopilotInvocationUpdate(text, metadata.ContextId, requestId);
        }
        catch (Exception exception)
        {
            GenAiTelemetry.RecordFailure(genAiActivity, exception);
            throw;
        }

        yield return update;
    }
}

internal static class A2AResponseText
{
    internal static string Extract(JsonElement root)
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
