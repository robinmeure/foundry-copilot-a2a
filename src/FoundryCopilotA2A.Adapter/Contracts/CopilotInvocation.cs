namespace FoundryCopilotA2A.Adapter;

public sealed record CopilotInvocationResult(
    string Text,
    string? ConversationId,
    string? ResponseId)
{
    public AgentResponder? Responder { get; init; }
    public CitationBundle? Citations { get; init; }
    public AdaptiveCardBundle? Cards { get; init; }

    /// <inheritdoc cref="CopilotInvocationUpdate.ConversationHandle"/>
    public string? ConversationHandle { get; init; }
}

public sealed record CopilotInvocationUpdate(
    string Text,
    string? ConversationId,
    string? ResponseId,
    bool IsInformative = false)
{
    public AgentResponder? Responder { get; init; }
    public CitationBundle? Citations { get; init; }
    public AdaptiveCardBundle? Cards { get; init; }
    public AgentPlanUpdate? PlanUpdate { get; init; }

    /// <summary>
    /// The update carries the whole answer so far, with its complete citations and cards, and
    /// replaces everything previously streamed. Used when the provider's final message differs
    /// from the deltas it streamed, for example because it resolved inline citation markers.
    /// </summary>
    public bool ReplacesAnswer { get; init; }

    /// <summary>
    /// Opaque, protected reference to the Copilot Studio conversation. The client stores it with
    /// its chat and sends it back so a restarted adapter can continue the same conversation.
    /// </summary>
    public string? ConversationHandle { get; init; }
}

public sealed record AgentResponder(string Id, string Name);
