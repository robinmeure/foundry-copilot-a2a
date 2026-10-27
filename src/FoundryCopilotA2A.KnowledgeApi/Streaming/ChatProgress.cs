namespace FoundryCopilotA2A.KnowledgeApi.Streaming;

/// <summary>
/// Receives live status and answer text while a chat turn runs. The default instance ignores
/// everything, so the JSON endpoint keeps its single-response behavior.
/// </summary>
public class ChatProgress
{
    public static readonly ChatProgress None = new();

    /// <summary>True when the receiver wants answer text as it is generated.</summary>
    public virtual bool StreamsText => false;

    public virtual ValueTask ReportAsync(ChatStatus status, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask WriteTextAsync(string text, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
