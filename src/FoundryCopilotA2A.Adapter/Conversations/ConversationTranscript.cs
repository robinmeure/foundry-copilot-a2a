namespace FoundryCopilotA2A.Adapter;

/// <summary>
/// Renders caller-supplied history into a prompt prefix for backends that do not keep the
/// conversation server-side.
/// </summary>
public static class ConversationTranscript
{
    public static string Prepend(string prompt, IReadOnlyList<A2AConversationTurn> history) =>
        Prepend(prompt, history, int.MaxValue, int.MaxValue);

    /// <summary>
    /// Renders only the most recent turns that fit both budgets. A turn that would exceed the
    /// character budget ends the window, so the replayed context is always contiguous.
    /// </summary>
    public static string Prepend(
        string prompt,
        IReadOnlyList<A2AConversationTurn> history,
        int maxTurns,
        int maxCharacters)
    {
        var start = history.Count;
        var characters = 0;
        while (start > 0 && history.Count - start < maxTurns)
        {
            var length = history[start - 1].Text.Length;
            if (characters + length > maxCharacters)
            {
                break;
            }

            characters += length;
            start--;
        }

        if (start == history.Count)
        {
            return prompt;
        }

        var builder = new System.Text.StringBuilder(start == 0
            ? "Conversation so far:"
            : $"Conversation so far (last {history.Count - start} of {history.Count} turns):");
        for (var index = start; index < history.Count; index++)
        {
            var turn = history[index];
            builder.Append('\n')
                .Append(turn.Role == A2AConversationTurn.UserRole ? "User: " : "Assistant: ")
                .Append(turn.Text);
        }

        return builder.Append("\n\n---\n").Append(prompt).ToString();
    }
}
