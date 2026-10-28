namespace FoundryCopilotA2A.Adapter;

public sealed record A2ARequestMetadata
{
    public required string? ContextId { get; init; }

    public required string? MessageId { get; init; }

    public required string AgentId { get; init; }

    /// <summary>Composite tenant-scoped caller identity. Never a shared fallback in a secured deployment.</summary>
    public required string UserId { get; init; }

    public required string? PayloadHash { get; init; }

    public required string? BearerToken { get; init; }

    public string? ChainTargetAgentId { get; init; }

    public bool IsAgentRoute { get; init; }

    /// <summary>Prior turns supplied by the caller, oldest first. Empty when the caller sent none.</summary>
    public IReadOnlyList<A2AConversationTurn> History { get; init; } = [];
    public IReadOnlyList<A2AInputFile> Files { get; init; } = [];

    /// <summary>Opaque Copilot Studio conversation handles returned by earlier turns, newest first.</summary>
    public IReadOnlyList<string> ConversationHandles { get; init; } = [];

    /// <summary>
    /// Overridden so the synthesized record ToString() cannot leak the delegated bearer token
    /// into logs or telemetry.
    /// </summary>
    public override string ToString() =>
        $"A2ARequestMetadata {{ ContextId = {ContextId}, MessageId = {MessageId}, " +
        $"AgentId = {AgentId}, UserId = {UserId}, PayloadHash = {PayloadHash}, " +
        $"ChainTargetAgentId = {ChainTargetAgentId}, HistoryTurns = {History.Count}, " +
        $"FileCount = {Files.Count}, ConversationHandles = {ConversationHandles.Count}, " +
        "BearerToken = [redacted] }";
}

/// <summary>A single prior turn of the caller's conversation.</summary>
public sealed record A2AConversationTurn(string Role, string Text)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";
}
