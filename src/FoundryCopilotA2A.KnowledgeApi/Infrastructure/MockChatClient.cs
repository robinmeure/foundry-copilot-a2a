using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.KnowledgeApi.Infrastructure;

/// <summary>Produces deterministic replies and word-sized streaming updates without network access.</summary>
internal sealed class MockChatClient(string answer) : IChatClient
{
    private static readonly Regex MockStreamChunk = new(@"\S+\s*|\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageId = Guid.NewGuid().ToString("N");
        foreach (Match word in MockStreamChunk.Matches(answer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word.Value) { MessageId = messageId };
        }

        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, MessageId = messageId, FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
