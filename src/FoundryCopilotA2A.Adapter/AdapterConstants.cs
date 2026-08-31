using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Adapter;

public static class AdapterConstants
{
    public const string AgentName = "copilot-studio-adapter";
    public const string RuntimePath = "/a2a/copilot-studio";
    public const string ChainAgentsPath = "/a2a-agents";
    public const string AgentsPath = "/api/agents";
    public const string TracesPath = "/api/traces";
    public const string TraceHeaderName = "X-Trace-Id";
    public const string AgentHeaderName = "X-Copilot-Agent";
    public const string ChainTargetHeaderName = "X-A2A-Chain-Target";
    public const string RouteAgentItem = "a2a.route-agent";
    public const string ChainTargetItem = "a2a.chain-target";
    public const string ContextIdItem = "a2a.context-id";
    public const string MessageIdItem = "a2a.message-id";
    public const string PayloadHashItem = "a2a.payload-hash";
    public const string HistoryItem = "a2a.history";

    /// <summary>Key under <c>params.message.metadata</c> carrying the prior turns of the conversation.</summary>
    public const string HistoryMetadataKey = "history";

    /// <summary>Caps how much caller-supplied conversation history the adapter will relay.</summary>
    public const int MaxHistoryTurns = 20;

    /// <summary>Caps the length of a single relayed history turn.</summary>
    public const int MaxHistoryTurnLength = 4000;

    /// <summary>
    /// Only ever used when authentication is disabled AND the operator has explicitly opted in
    /// via Adapter:AllowAnonymousDevelopmentMode.
    /// </summary>
    public const string AnonymousDevelopmentUser = "development|anonymous";

    public static string ChainAgentBasePath(string agentId) =>
        $"{ChainAgentsPath}/{Uri.EscapeDataString(agentId)}";

    public static string ChainAgentRuntimePath(string agentId) =>
        $"{ChainAgentBasePath(agentId)}/a2a";
}

public static class PayloadHash
{
    /// <summary>
    /// Binds a cached idempotent response to the request that produced it, so a replay that
    /// reuses a messageId but changes the content is detected instead of silently being served
    /// another caller's answer.
    /// </summary>
    public static string Compute(JsonElement message)
    {
        var text = ExtractText(message);
        return ComputeFromPrompt(text);
    }

    public static string ComputeFromPrompt(string prompt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));

    private static string ExtractText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(textElement.GetString());
                builder.Append('\u001f');
            }
        }

        return builder.ToString();
    }
}
