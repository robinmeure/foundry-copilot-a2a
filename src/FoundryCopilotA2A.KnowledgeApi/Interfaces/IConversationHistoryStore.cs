using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;

namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Serializes turns per scope and commits session state only after a successful turn.</summary>
public interface IConversationHistoryStore
{
    Task<GroundedAnswer> RunAsync(
        string scope,
        Func<JsonElement?, CancellationToken, Task<GroundedAnswer>> run,
        CancellationToken cancellationToken);

    /// <summary>Removes expired state without waiting for conversations with active turns.</summary>
    Task SweepExpiredAsync(CancellationToken cancellationToken);
}
