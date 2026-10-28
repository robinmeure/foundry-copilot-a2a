namespace FoundryCopilotA2A.KnowledgeApi.Streaming;

/// <summary>A user-facing progress line for one chat turn.</summary>
public sealed record ChatStatus(string Message, string State = ChatStatus.Started, string? Tool = null)
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";

    /// <summary>
    /// A self-contained update inside a running step, such as a specialist agent's own progress.
    /// It never opens or closes the tool's step.
    /// </summary>
    public const string Progress = "progress";
}
