using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

internal static class CopilotStudioAdaptiveCards
{
    private const string ConnectionManagerActivityName = "connectors/connectionManagerCard";

    public static AdaptiveCardBundle? Read(IActivity activity, AgentResponder responder)
    {
        // The OAuth/connection-manager card has its own handling and is never a display card.
        if (activity.Attachments is null ||
            string.Equals(activity.Name, ConnectionManagerActivityName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return AgentAdaptiveCards.FromAttachments(
            activity.Attachments.Select(a => (a.ContentType ?? string.Empty, (object?)a.Content)),
            activity.Id ?? "activity",
            responder);
    }
}

internal static class CopilotStudioAttachmentText
{
    private const string AdaptiveCardContentType = "application/vnd.microsoft.card.adaptive";
    private const string ConnectionManagerActivityName = "connectors/connectionManagerCard";

    public static string? ExtractConnectionManagerCardText(IActivity activity)
    {
        if (!string.Equals(
                activity.Name,
                ConnectionManagerActivityName,
                StringComparison.OrdinalIgnoreCase) ||
            activity.Attachments is null)
        {
            return null;
        }

        var textBlocks = new List<string>();
        foreach (var attachment in activity.Attachments)
        {
            if (!string.Equals(
                    attachment.ContentType,
                    AdaptiveCardContentType,
                    StringComparison.OrdinalIgnoreCase) ||
                attachment.Content is null)
            {
                continue;
            }

            var content = attachment.Content switch
            {
                JsonElement element => element,
                JsonDocument document => document.RootElement,
                _ => JsonSerializer.SerializeToElement(
                    attachment.Content,
                    attachment.Content.GetType())
            };
            CollectTextBlocks(content, textBlocks);
        }

        return textBlocks.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, textBlocks);
    }

    private static void CollectTextBlocks(JsonElement element, List<string> textBlocks)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectTextBlocks(item, textBlocks);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "TextBlock", StringComparison.OrdinalIgnoreCase) &&
            element.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(text.GetString()))
        {
            textBlocks.Add(text.GetString()!);
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                CollectTextBlocks(property.Value, textBlocks);
            }
        }
    }
}
