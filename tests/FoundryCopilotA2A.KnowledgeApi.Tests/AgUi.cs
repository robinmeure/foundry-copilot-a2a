using System.Text.Json;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

/// <summary>Builds AG-UI run requests and reads AG-UI event streams for endpoint tests.</summary>
internal static class AgUi
{
    /// <summary>A minimal <c>RunAgentInput</c> body as an AG-UI HttpAgent sends it.</summary>
    public static object Run(string conversationId, string text, string[] sources, string[]? agents = null) => new
    {
        threadId = conversationId,
        runId = Guid.NewGuid().ToString(),
        state = new { },
        messages = new[] { new { id = Guid.NewGuid().ToString(), role = "user", content = text } },
        tools = Array.Empty<object>(),
        context = Array.Empty<object>(),
        forwardedProps = new { sources, agents }
    };

    /// <summary>Parses <c>data:</c>-only SSE frames into AG-UI events.</summary>
    public static List<JsonElement> ParseEvents(string body) =>
        body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => string.Join('\n', frame.Split('\n')
                .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(line => line["data: ".Length..])))
            .Select(data => JsonDocument.Parse(data).RootElement.Clone())
            .ToList();

    public static string Type(this JsonElement agUiEvent) => agUiEvent.GetProperty("type").GetString()!;

    /// <summary>The <c>ChatStatus</c> a step event carries, or null for protocol bookkeeping steps.</summary>
    public static JsonElement? Status(this JsonElement agUiEvent) =>
        agUiEvent.Type() is "STEP_STARTED" or "STEP_FINISHED" && agUiEvent.TryGetProperty("metadata", out var status)
            ? status
            : null;
}
