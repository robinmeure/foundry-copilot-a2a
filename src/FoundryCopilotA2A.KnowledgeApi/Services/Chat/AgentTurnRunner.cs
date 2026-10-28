using System.Text;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using Microsoft.Agents.AI;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Chat;

/// <summary>Collects an agent turn while normalizing citation markers across streaming chunks.</summary>
internal static class AgentTurnRunner
{
    public static async Task<string> RunAsync(AIAgent agent, string prompt, AgentSession session, ChatProgress progress, CancellationToken ct)
    {
        if (!progress.StreamsText)
        {
            return (await agent.RunAsync(prompt, session, cancellationToken: ct)).Text;
        }

        var text = new StringBuilder();
        var formatter = new CitationStreamFormatter();
        await foreach (var update in agent.RunStreamingAsync(prompt, session, cancellationToken: ct))
        {
            if (string.IsNullOrEmpty(update.Text))
            {
                continue;
            }

            text.Append(update.Text);
            await progress.WriteTextAsync(formatter.Append(update.Text), ct);
        }

        await progress.WriteTextAsync(formatter.Flush(), ct);
        return text.ToString();
    }

}
