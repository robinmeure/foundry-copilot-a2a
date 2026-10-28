namespace FoundryCopilotA2A.Adapter;

public sealed class AdapterOptions
{
    public const string SectionName = "Adapter";

    public string Backend { get; set; } = "Mock";

    public string PublicBaseUrl { get; set; } = "http://localhost:5099";

    public int IdempotencyTtlMinutes { get; set; } = 15;

    public int ConversationTtlMinutes { get; set; } = 30;

    /// <summary>
    /// How long a client-held Copilot Studio conversation handle stays valid. Saved chats and
    /// adapter restarts within this window continue the conversation instead of replaying history.
    /// </summary>
    public int ConversationHandleLifetimeMinutes { get; set; } = 480;

    /// <summary>
    /// Most recent caller-supplied turns replayed into a new Copilot Studio conversation. An
    /// existing conversation already holds its transcript, so this applies only to a new one.
    /// </summary>
    public int MaxReplayTurns { get; set; } = 6;

    /// <summary>Character budget for history replayed into a new Copilot Studio conversation.</summary>
    public int MaxReplayCharacters { get; set; } = 6_000;

    /// <summary>
    /// Must be set explicitly to run without authentication. Prevents an unsecured deployment
    /// from starting by accident, since both caches partition on the caller identity.
    /// </summary>
    public bool AllowAnonymousDevelopmentMode { get; set; }

    /// <summary>
    /// Forwards validated display-only Adaptive Cards in a dedicated data part and advertises the
    /// extension. Off by default; the text answer is unchanged either way.
    /// </summary>
    public bool AdaptiveCardsEnabled { get; set; }

    /// <summary>Exposes a deterministic failing agent for local error-state UX testing.</summary>
    public bool EnableFailureMock { get; set; }

    /// <summary>Bounds a single delegated invocation so a hung backend cannot pin a request open.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Per-attempt budget for an outbound Foundry A2A call. A chained call runs an LLM turn, an
    /// inbound A2A call back into this adapter, and a Copilot Studio turn, so it needs far more
    /// than the 10s default. The call is not retried, because a retry re-runs the whole chain.
    /// </summary>
    public int FoundryRequestTimeoutSeconds { get; set; } = 120;

    /// <summary>Hard cap on cache entries. A2A messageIds are unique per message, so without a cap the caches only grow.</summary>
    public long MaxCacheEntries { get; set; } = 10_000;

    /// <summary>Exact browser origins allowed to call the A2A endpoint.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    public bool UseMockBackend =>
        string.Equals(Backend, "Mock", StringComparison.OrdinalIgnoreCase);
}
