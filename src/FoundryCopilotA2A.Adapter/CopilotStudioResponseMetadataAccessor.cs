namespace FoundryCopilotA2A.Adapter;

public sealed class CopilotStudioResponseMetadataAccessor
{
    private readonly AsyncLocal<Capture?> _current = new();

    public Capture BeginCapture()
    {
        var capture = new Capture(this, _current.Value);
        _current.Value = capture;
        return capture;
    }

    public void CaptureConversationId(string conversationId)
    {
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            _current.Value?.SetConversationId(conversationId);
        }
    }

    public sealed class Capture(
        CopilotStudioResponseMetadataAccessor owner,
        Capture? parent) : IDisposable
    {
        public string? ConversationId { get; private set; }

        public void Dispose()
        {
            if (ReferenceEquals(owner._current.Value, this))
            {
                owner._current.Value = parent;
            }
        }

        internal void SetConversationId(string conversationId) =>
            ConversationId ??= conversationId;
    }
}
