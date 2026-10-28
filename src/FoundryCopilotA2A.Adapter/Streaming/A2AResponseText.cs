using System.Text.Json;

namespace FoundryCopilotA2A.Adapter;

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
