using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.KnowledgeApi.Stores;

internal sealed class ConversationHistory
{
    internal const int MaxTurns = 12;
    internal const int MaxCharacters = 24000;
    private const int MaxAssistantCharacters = 8000;
    internal const string PrivateReply =
        "A previous reply used private context. Retrieve fresh authorized evidence for follow-up questions; that reply is not retained as evidence.";

    public InMemoryChatHistoryProvider Provider { get; } =
        new(new InMemoryChatHistoryProviderOptions { StateKey = "dialogue-v1" });

    private List<ChatMessage> previousMessages = [];

    public async Task<AgentSession> LoadAsync(AIAgent agent, JsonElement? state, CancellationToken cancellationToken)
    {
        var session = state is { } saved
            ? await agent.DeserializeSessionAsync(saved, cancellationToken: cancellationToken)
            : await agent.CreateSessionAsync(cancellationToken);
        previousMessages = Provider.GetMessages(session).ToList();
        if (state is not null &&
            (previousMessages.Count == 0 || previousMessages.Count > MaxTurns * 2 || previousMessages.Count % 2 != 0 ||
             previousMessages.Sum(message => message.Text.Length) > MaxCharacters ||
             previousMessages.Where((message, index) =>
                 message.Role != (index % 2 == 0 ? ChatRole.User : ChatRole.Assistant) ||
                 message.Contents.Count != 1 || message.Contents[0] is not TextContent).Any()))
        {
            throw new ApiException(503, "history_unavailable",
                "The saved conversation history is invalid. Start a new conversation or contact the service administrator.");
        }
        previousMessages = previousMessages.Select(message => new ChatMessage(message.Role, message.Text)).ToList();
        return session;
    }

    public async Task<JsonElement> CompleteAsync(
        AIAgent agent, string question, string answer, bool hadPrivateContext, CancellationToken cancellationToken)
    {
        var reply = hadPrivateContext ? PrivateReply : answer[..Math.Min(answer.Length, MaxAssistantCharacters)];
        List<ChatMessage> messages =
        [
            .. previousMessages,
            new(ChatRole.User, question),
            new(ChatRole.Assistant, reply)
        ];
        while (messages.Count > MaxTurns * 2 || messages.Sum(message => message.Text.Length) > MaxCharacters)
        {
            messages.RemoveRange(0, 2);
        }

        // A fresh session excludes response IDs, reasoning, tool results and enriched prompts.
        var persisted = await agent.CreateSessionAsync(cancellationToken);
        Provider.SetMessages(persisted, messages);
        return await agent.SerializeSessionAsync(persisted, cancellationToken: cancellationToken);
    }
}
