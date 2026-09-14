using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;

namespace FoundryCopilotA2A.Adapter;

internal static class CopilotStudioCitations
{
    public static CitationBundle? Read(IActivity activity, AgentResponder responder)
    {
        if (!string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        CitationBundle? result = null;
        var messageId = CopilotStudioStreamingText.ReadStreamInfo(activity).StreamId ?? activity.Id;
        foreach (var entity in activity.Entities ?? [])
        {
            var value = ProtocolJsonSerializer.ToJsonElement(entity);
            if (!value.TryGetProperty("citation", out var citations))
            {
                continue;
            }
            if (citations.ValueKind != JsonValueKind.Array || citations.GetArrayLength() > 200)
            {
                throw AgentCitations.Invalid("The provider citation entity must contain an array.");
            }

            foreach (var citation in citations.EnumerateArray())
            {
                if (citation.ValueKind != JsonValueKind.Object ||
                    !citation.TryGetProperty("appearance", out var appearance) ||
                    appearance.ValueKind != JsonValueKind.Object)
                {
                    throw AgentCitations.Invalid("A provider citation must contain its source appearance.");
                }
                string? marker = null;
                if (citation.TryGetProperty("position", out var position))
                {
                    if (position.ValueKind != JsonValueKind.Number ||
                        !position.TryGetInt32(out var number) || number <= 0)
                    {
                        throw AgentCitations.Invalid("A provider citation position must be a positive integer.");
                    }
                    marker = $"[{number}]";
                }

                result = AgentCitations.Merge(result, AgentCitations.FromProviderSource(
                    responder,
                    messageId,
                    AgentCitations.RequiredString(appearance, "name"),
                    AgentCitations.OptionalString(appearance, "url"),
                    marker,
                    AgentCitations.OptionalString(appearance, "abstract")));
            }
        }
        return result;
    }
}
