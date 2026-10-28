using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

internal static class CopilotStudioConnectionSettings
{
    private const string StudioBaseUrl = "https://copilotstudio.microsoft.com";

    public static string? TryCreateUrl(IActivity activity)
    {
        var segments = activity.From?.Id?.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments is not { Length: 2 } ||
            !Guid.TryParse(segments[0], out var environmentId) ||
            !Guid.TryParse(segments[1], out var botId))
        {
            return null;
        }

        return $"{StudioBaseUrl}/environments/{environmentId:D}/bots/{botId:D}" +
               "/settings/connections";
    }
}
