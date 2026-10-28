using System.Diagnostics;
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
        using var activity = AdapterTelemetry.StartActivity("apim.a2a.invoke");
        var invocationStarted = Stopwatch.GetTimestamp();
        var invocationCompleted = false;
        string? apiId = null;
        Activity? genAiActivity = null;
        try
        {
            ResolvedApiManagementAgent agent;
            try
            {
                agent = await registry.TryResolveAsync(metadata.AgentId, cancellationToken) ??
                        throw new AdapterRequestException(
                            $"API Management agent '{metadata.AgentId}' is not configured.");
            }
            catch (Exception exception)
            {
                AdapterTelemetry.RecordFailure(activity, exception);
                throw;
            }

            apiId = agent.ApiId;
            activity?.SetTag("apim.api.id", agent.ApiId);
            activity?.SetTag("a2a.agent.id", agent.Id);
            genAiActivity = GenAiTelemetry.StartInvokeAgent(
                "azure.apim",
                agent.DisplayName,
                agent.Id,
                metadata.ContextId);
            await using var updates = InvokeAsync(
                    agent,
                    ConversationTranscript.Prepend(prompt, metadata.History),
                    metadata,
                    activity,
                    cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await updates.MoveNextAsync();
                }
                catch (OperationCanceledException exception)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    var timeout = new AdapterRequestException(
                        "API Management A2A did not finish within the configured timeout.",
                        exception);
                    AdapterTelemetry.RecordFailure(activity, timeout);
                    GenAiTelemetry.RecordFailure(genAiActivity, timeout);
                    throw timeout;
                }
                catch (Exception exception)
                {
                    AdapterTelemetry.RecordFailure(activity, exception);
                    GenAiTelemetry.RecordFailure(genAiActivity, exception);
                    throw;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return updates.Current;
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            genAiActivity?.SetStatus(ActivityStatusCode.Ok);
            invocationCompleted = true;
        }
        finally
        {
            genAiActivity?.Dispose();
            AdapterTelemetry.RecordApiManagementA2AInvocation(
                activity,
                Stopwatch.GetElapsedTime(invocationStarted),
                apiId,
                invocationCompleted);
        }
    }

    private async IAsyncEnumerable<CopilotInvocationUpdate> InvokeAsync(
        ResolvedApiManagementAgent agent,
        string effectivePrompt,
        A2ARequestMetadata metadata,
        Activity? activity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "SendStreamingMessage",
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

        var client = httpClientFactory.CreateClient("apim-a2a");
        // HttpClient.Timeout stops at the response headers when streaming, so the same budget is
        // applied to reading the event stream as well.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(client.Timeout);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, agent.Endpoint);
        if (!string.IsNullOrWhiteSpace(metadata.BearerToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", metadata.BearerToken);
        }
        request.Headers.Add("A2A-Version", "1.0");
        request.Headers.Add(AgentPlanUpdates.Header, AgentPlanUpdates.ExtensionUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.5));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var sendStarted = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        activity?.SetTag("http.response.status_code", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            throw new AdapterRequestException(
                $"API Management A2A returned HTTP {(int)response.StatusCode}.");
        }

        var streaming = string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);
        activity?.SetTag("apim.a2a.streaming", streaming);
        if (!streaming)
        {
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(responseBody);
            ThrowIfError(document.RootElement);
            var answer = A2AResponseText.Read(document.RootElement);
            if (string.IsNullOrWhiteSpace(answer.Text))
            {
                throw new AdapterRequestException(
                    "API Management A2A returned no text response.");
            }

            yield return new CopilotInvocationUpdate(answer.Text, metadata.ContextId, requestId)
            {
                Citations = answer.Citations,
                Cards = answer.Cards
            };
            yield break;
        }

        var accumulator = new A2AStreamAccumulator();
        var firstAnswerRecorded = false;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await foreach (var data in ServerSentEvents.ReadDataAsync(stream, timeout.Token))
            {
                using var document = JsonDocument.Parse(data);
                ThrowIfError(document.RootElement);
                foreach (var delta in accumulator.Consume(document.RootElement))
                {
                    if (!firstAnswerRecorded && !delta.IsInformative && delta.Text.Length > 0)
                    {
                        firstAnswerRecorded = true;
                        activity?.AddEvent(new ActivityEvent(
                            "apim.a2a.stream.first_answer",
                            tags: new ActivityTagsCollection
                            {
                                {
                                    "apim.a2a.stream.first_answer_ms",
                                    Stopwatch.GetElapsedTime(sendStarted).TotalMilliseconds
                                }
                            }));
                    }

                    yield return new CopilotInvocationUpdate(
                        delta.Text,
                        metadata.ContextId,
                        requestId,
                        delta.IsInformative)
                    {
                        Citations = delta.Citations,
                        Cards = delta.Cards,
                        PlanUpdate = delta.PlanUpdate,
                        ReplacesAnswer = delta.ReplacesAnswer
                    };
                }
            }
        }
        finally
        {
            activity?.SetTag("apim.a2a.stream.event.count", accumulator.EventCount);
            activity?.SetTag("apim.a2a.stream.delta.count", accumulator.DeltaCount);
            activity?.SetTag("apim.a2a.stream.final.suppressed", accumulator.SuppressedCount);
            activity?.SetTag("apim.a2a.stream.final.replaced", accumulator.ReplacedCount);
        }

        if (!accumulator.HasAnswer)
        {
            throw new AdapterRequestException("API Management A2A returned no text response.");
        }
    }

    private static void ThrowIfError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object &&
                          error.TryGetProperty("message", out var errorMessage)
                ? errorMessage.GetString()
                : null;
            throw new AdapterRequestException(
                $"API Management A2A failed: {message ?? "unknown JSON-RPC error"}.");
        }
    }
}
