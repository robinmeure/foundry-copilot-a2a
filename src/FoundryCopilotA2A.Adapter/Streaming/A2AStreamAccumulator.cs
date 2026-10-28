using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Adapter;

internal sealed record A2AStreamDelta(
    string Text, bool IsInformative, CitationBundle? Citations, AdaptiveCardBundle? Cards = null,
    AgentPlanUpdate? PlanUpdate = null, bool ReplacesAnswer = false);

/// <summary>
/// Turns A2A streaming events into text deltas. Answer chunks normally arrive as appended
/// artifact updates; a replacing update, message, or final task that repeats text already sent
/// is reduced to its unseen suffix so the caller never receives the answer twice. A replacing
/// update whose text diverges from what was sent is canonical and becomes a replacement of the
/// whole answer.
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
    private CitationBundle? _citations;
    private AdaptiveCardBundle? _cards;

    public int EventCount { get; private set; }

    public int ReplacedCount { get; private set; }

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

        var plans = new List<AgentPlanUpdate>();
        foreach (var part in parts)
        {
            if (part.TryGetProperty("data", out var data) &&
                AgentPlanUpdates.ReadContainer(data) is { } plan)
            {
                plans.Add(plan);
            }
        }

        if (plans.Count > 0)
        {
            return plans.Select(plan => new A2AStreamDelta(
                AgentPlanUpdates.Summary(plan), true, null, PlanUpdate: plan));
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

        var answerParts = parts.Where(part => !IsInformative(part)).ToArray();
        var (citations, cards) = ReadAnswerMetadata(answerParts);
        var text = JoinText(answerParts);
        _citations = AgentCitations.Merge(_citations, citations);
        _cards = A2AResponseText.MergeCards(_cards, cards);

        var delta = string.Empty;
        var replaced = false;
        if (text.Length > 0)
        {
            (delta, replaced) = Accept(key, text, append);
        }

        if (replaced)
        {
            ReplacedCount++;
            deltas.Add(new A2AStreamDelta(
                string.Join(Environment.NewLine, _order.Select(other => _sent[other].ToString())
                    .Where(value => value.Length > 0)),
                false, _citations, _cards, ReplacesAnswer: true));
            return deltas;
        }
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

    private static (CitationBundle? Citations, AdaptiveCardBundle? Cards) ReadAnswerMetadata(
        IEnumerable<JsonElement> parts)
    {
        CitationBundle? citations = null;
        AdaptiveCardBundle? cards = null;
        foreach (var part in parts)
        {
            foreach (var name in new[] { "data", "metadata" })
            {
                if (!part.TryGetProperty(name, out var container))
                {
                    continue;
                }

                citations = AgentCitations.Merge(citations, AgentCitations.ReadContainer(container));
                cards = A2AResponseText.MergeCards(
                    cards, AgentAdaptiveCards.ReadContainerLenient(container));
            }
        }

        return (citations, cards);
    }

    private (string Delta, bool Replaced) Accept(string key, string text, bool append)
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
            return (separator + text, false);
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
            return (suffix, false);
        }

        if (string.Equals(text.Trim(), previous.Trim(), StringComparison.Ordinal))
        {
            SuppressedCount++;
            return (string.Empty, false);
        }

        sent.Clear().Append(text);
        return (string.Empty, true);
    }

    private static string ArtifactKey(JsonElement artifact) =>
        artifact.TryGetProperty("artifactId", out var id) && id.GetString() is { Length: > 0 } value
            ? value
            : string.Empty;

    private static bool IsKind(JsonElement result, string kind) =>
        result.TryGetProperty("kind", out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() == kind;

    private static JsonElement[] Parts(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return parts.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object).ToArray();
    }

    private static bool IsInformative(JsonElement part) =>
        part.TryGetProperty("metadata", out var metadata) &&
        metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty("isInformative", out var informative) &&
        informative.ValueKind == JsonValueKind.True;

    private static string JoinText(IEnumerable<JsonElement> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
            {
                text.Append(value.GetString());
            }
        }

        return text.ToString();
    }
}
