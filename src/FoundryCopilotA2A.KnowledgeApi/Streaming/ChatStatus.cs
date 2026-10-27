namespace FoundryCopilotA2A.KnowledgeApi.Streaming;

/// <summary>A user-facing progress line for one chat turn.</summary>
public sealed record ChatStatus(string Message, string State = ChatStatus.Started, string? Tool = null)
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
