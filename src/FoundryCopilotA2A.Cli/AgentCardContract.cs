using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal static class AgentCardContract
{
    internal static bool AdvertisesRuntime(JsonElement card, Func<string?, bool> matchesRuntime)
    {
        if (card.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasUrl = card.TryGetProperty("url", out var runtime);
        var hasInterfaces = card.TryGetProperty("supportedInterfaces", out var interfaces);
        if (hasUrl && (runtime.ValueKind != JsonValueKind.String || !matchesRuntime(runtime.GetString())))
        {
            return false;
        }

        if (hasInterfaces &&
            (interfaces.ValueKind != JsonValueKind.Array ||
             !interfaces.EnumerateArray().Any(item =>
                 item.ValueKind == JsonValueKind.Object &&
                 item.TryGetProperty("url", out var url) &&
                 url.ValueKind == JsonValueKind.String &&
                 matchesRuntime(url.GetString()))))
        {
            return false;
        }

        return hasUrl || hasInterfaces;
    }
}
