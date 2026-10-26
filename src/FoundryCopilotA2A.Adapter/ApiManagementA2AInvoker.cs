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
                        Cards = delta.Cards
                    };
                }
            }
        }
        finally
        {
            activity?.SetTag("apim.a2a.stream.event.count", accumulator.EventCount);
            activity?.SetTag("apim.a2a.stream.delta.count", accumulator.DeltaCount);
            activity?.SetTag("apim.a2a.stream.final.suppressed", accumulator.SuppressedCount);
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

internal static class ServerSentEvents
{
    /// <summary>Yields the joined <c>data:</c> payload of each event as soon as it completes.</summary>
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (TakePayload(data) is { } payload)
                {
                    yield return payload;
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsSpan("data:".Length);
                if (value.Length > 0 && value[0] == ' ')
                {
                    value = value[1..];
                }
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(value);
            }
        }

        if (TakePayload(data) is { } trailing)
        {
            yield return trailing;
        }
    }

    private static string? TakePayload(StringBuilder data)
    {
        var payload = data.ToString().Trim();
        data.Clear();
        return payload.Length == 0 || payload is "[DONE]" or "end" ? null : payload;
    }
}

internal sealed record A2AStreamDelta(
    string Text, bool IsInformative, CitationBundle? Citations, AdaptiveCardBundle? Cards = null);

/// <summary>
/// Turns A2A streaming events into text deltas. Answer chunks normally arrive as appended
/// artifact updates; a replacing update, message, or final task that repeats text already sent
/// is reduced to its unseen suffix so the caller never receives the answer twice.
/// </summary>
internal sealed class A2AStreamAccumulator
{
    private const string MessageKey = "\u0000message";
    private const string StatusKey = "\u0000status";
    private static readonly HashSet<string> FailedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "TASK_STATE_FAILED",
        "TASK_STATE_REJECTED",
        "TASK_STATE_CANCELED",
        "TASK_STATE_CANCELLED",
        "failed",
        "rejected",
        "canceled",
        "cancelled"
    };

    private static readonly HashSet<string> CompletedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "TASK_STATE_COMPLETED",
        "completed"
    };

    private readonly Dictionary<string, StringBuilder> _sent = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public int EventCount { get; private set; }

    public int DeltaCount { get; private set; }

    public int SuppressedCount { get; private set; }

    public bool HasAnswer => _sent.Values.Any(text => text.Length > 0);

    public IEnumerable<A2AStreamDelta> Consume(JsonElement root)
    {
        EventCount++;
        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var isArtifactUpdate = result.TryGetProperty("artifactUpdate", out var artifactUpdate);
        if (!isArtifactUpdate && IsKind(result, "artifact-update"))
        {
            artifactUpdate = result;
            isArtifactUpdate = true;
        }

        if (isArtifactUpdate)
        {
            if (!artifactUpdate.TryGetProperty("artifact", out var artifact))
            {
                return [];
            }

            var append = artifactUpdate.TryGetProperty("append", out var appendValue) &&
                         appendValue.ValueKind == JsonValueKind.True;
            return Apply(ArtifactKey(artifact), Parts(artifact), append);
        }

        if (result.TryGetProperty("statusUpdate", out var statusUpdate) || IsKind(result, "status-update"))
        {
            var update = statusUpdate.ValueKind == JsonValueKind.Object ? statusUpdate : result;
            return ApplyStatus(update);
        }

        if (result.TryGetProperty("message", out var message) || IsKind(result, "message"))
        {
            var container = message.ValueKind == JsonValueKind.Object ? message : result;
            return Apply(MessageKey, Parts(container), append: false);
        }

        var task = result.TryGetProperty("task", out var taskValue) ? taskValue : result;
        var deltas = new List<A2AStreamDelta>();
        if (task.TryGetProperty("artifacts", out var artifacts) &&
            artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in artifacts.EnumerateArray())
            {
                deltas.AddRange(Apply(ArtifactKey(artifact), Parts(artifact), append: false));
            }
        }

        deltas.AddRange(ApplyStatus(task));
        return deltas;
    }

    private IEnumerable<A2AStreamDelta> ApplyStatus(JsonElement container)
    {
        if (!container.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("state", out var stateValue) ||
            stateValue.GetString() is not { } state)
        {
            return [];
        }

        var parts = status.TryGetProperty("message", out var statusMessage)
            ? Parts(statusMessage)
            : [];
        if (FailedStates.Contains(state))
        {
            var detail = JoinText(parts.Where(part => !IsInformative(part)));
            throw new AdapterRequestException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"API Management A2A task ended with state {state}."
                    : $"API Management A2A task ended with state {state}: {detail}");
        }

        // Status text is lifecycle chatter, except when a completed task carries its only answer
        // there.
        return CompletedStates.Contains(state) && !HasAnswer
            ? Apply(StatusKey, parts, append: false)
            : [];
    }

    private IEnumerable<A2AStreamDelta> Apply(string key, JsonElement[] parts, bool append)
    {
        var deltas = new List<A2AStreamDelta>();
        var informative = JoinText(parts.Where(IsInformative));
        if (informative.Length > 0)
        {
            deltas.Add(new A2AStreamDelta(informative, true, null));
        }

        CitationBundle? citations = null;
        AdaptiveCardBundle? cards = null;
        foreach (var part in parts.Where(part => !IsInformative(part)))
        {
            foreach (var name in new[] { "data", "metadata" })
            {
                if (part.TryGetProperty(name, out var container))
                {
                    citations = AgentCitations.Merge(citations, AgentCitations.ReadContainer(container));
                    cards = A2AResponseText.MergeCards(
                        cards, AgentAdaptiveCards.ReadContainerLenient(container));
                }
            }
        }

        var text = JoinText(parts.Where(part => !IsInformative(part)));
        var delta = text.Length == 0 ? string.Empty : Accept(key, text, append);
        if (delta.Length > 0)
        {
            DeltaCount++;
        }
        if (delta.Length > 0 || citations is not null || cards is not null)
        {
            deltas.Add(new A2AStreamDelta(delta, false, citations, cards));
        }

        return deltas;
    }

    private string Accept(string key, string text, bool append)
    {
        if (!_sent.TryGetValue(key, out var sent))
        {
            sent = new StringBuilder();
            _sent[key] = sent;
            _order.Add(key);
        }

        // A separate artifact or message is a new block of the same answer.
        var separator = sent.Length == 0 && _order.Any(other => other != key && _sent[other].Length > 0)
            ? Environment.NewLine
            : string.Empty;
        if (append || sent.Length == 0)
        {
            sent.Append(text);
            return separator + text;
        }

        var previous = sent.ToString();
        if (text.StartsWith(previous, StringComparison.Ordinal))
        {
            var suffix = text[previous.Length..];
            if (suffix.Length == 0)
            {
                SuppressedCount++;
            }
            sent.Append(suffix);
            return suffix;
        }

        // Text that is already sent cannot be retracted; keep the streamed version.
        SuppressedCount++;
        return string.Empty;
    }

    private static string ArtifactKey(JsonElement artifact) =>
        artifact.TryGetProperty("artifactId", out var id) && id.GetString() is { Length: > 0 } value
            ? value
            : string.Empty;

    private static bool IsKind(JsonElement result, string kind) =>
        result.TryGetProperty("kind", out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() == kind;

    private static JsonElement[] Parts(JsonElement container) =>
        container.ValueKind == JsonValueKind.Object &&
        container.TryGetProperty("parts", out var parts) &&
        parts.ValueKind == JsonValueKind.Array
            ? parts.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object).ToArray()
            : [];

    private static bool IsInformative(JsonElement part) =>
        part.TryGetProperty("metadata", out var metadata) &&
        metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty("isInformative", out var informative) &&
        informative.ValueKind == JsonValueKind.True;

    private static string JoinText(IEnumerable<JsonElement> parts) =>
        string.Concat(parts
            .Select(part => part.TryGetProperty("text", out var text) &&
                            text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null)
            .Where(text => !string.IsNullOrEmpty(text)));
}

internal static class A2AResponseText
{
    internal static string Extract(JsonElement root) => Read(root).Text;

    internal static (string Text, CitationBundle? Citations, AdaptiveCardBundle? Cards) Read(JsonElement root)
    {
        var parts = ReadParts(root);
        CitationBundle? citations = null;
        AdaptiveCardBundle? cards = null;
        foreach (var part in parts)
        {
            if (IsInformative(part))
            {
                continue;
            }
            foreach (var name in new[] { "data", "metadata" })
            {
                if (part.TryGetProperty(name, out var container))
                {
                    citations = AgentCitations.Merge(citations, AgentCitations.ReadContainer(container));
                    cards = MergeCards(cards, AgentAdaptiveCards.ReadContainerLenient(container));
                }
            }
        }
        return (JoinPartText(parts), citations, cards);
    }

    internal static AdaptiveCardBundle? MergeCards(AdaptiveCardBundle? current, AdaptiveCardBundle? incoming)
    {
        try
        {
            return AgentAdaptiveCards.Merge(current, incoming);
        }
        catch (AdaptiveCardFormatException)
        {
            return current;
        }
    }

    private static JsonElement[] ReadParts(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result))
        {
            return [];
        }

        if (result.TryGetProperty("message", out var message) &&
            message.TryGetProperty("parts", out var messageParts))
        {
            return ToParts(messageParts);
        }

        if (result.TryGetProperty("parts", out var resultParts))
        {
            return ToParts(resultParts);
        }

        if (!result.TryGetProperty("task", out var task))
        {
            task = result;
        }

        JsonElement[] artifactParts = [];
        if (task.TryGetProperty("artifacts", out var artifacts) &&
            artifacts.ValueKind == JsonValueKind.Array)
        {
            artifactParts = artifacts.EnumerateArray()
                .Where(artifact => artifact.TryGetProperty("parts", out _))
                .SelectMany(artifact => ToParts(artifact.GetProperty("parts")))
                .ToArray();
            if (!string.IsNullOrWhiteSpace(JoinPartText(artifactParts)))
            {
                return artifactParts;
            }
        }

        return task.TryGetProperty("status", out var status) &&
               status.TryGetProperty("message", out var statusMessage) &&
               statusMessage.TryGetProperty("parts", out var statusParts)
            ? [.. artifactParts, .. ToParts(statusParts)]
            : artifactParts;
    }

    private static JsonElement[] ToParts(JsonElement parts) =>
        parts.ValueKind == JsonValueKind.Array
            ? parts.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object).ToArray()
            : [];

    private static bool IsInformative(JsonElement part) =>
        part.TryGetProperty("metadata", out var metadata) &&
        metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty("isInformative", out var informative) &&
        informative.ValueKind == JsonValueKind.True;

    private static string JoinPartText(IEnumerable<JsonElement> parts) =>
        string.Join(
            Environment.NewLine,
            parts.Where(part => !IsInformative(part) && part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
}
