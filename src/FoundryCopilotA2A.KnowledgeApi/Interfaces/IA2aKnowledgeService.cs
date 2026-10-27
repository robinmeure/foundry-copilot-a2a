using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;

namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Discovers and invokes approved agents within the caller's selected knowledge sources.</summary>
public interface IA2aKnowledgeService
{
    bool Enabled { get; }
    string[]? ResolveSelection(IReadOnlyCollection<string> sources, string[]? agentIds);
    Task<KnowledgeAgentDescriptor[]> GetAvailableAgentsAsync(CancellationToken cancellationToken);

    /// <summary>Lists only available agents permitted by the current turn's selection.</summary>
    Task<KnowledgeAgentDescriptor[]> ListAsync(
        HttpContext context,
        JsonElement arguments,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? selectedAgentIds = null);

    /// <summary>Invokes an approved agent with delegated authorization and returns untrusted evidence.</summary>
    Task<Passage> AskAsync(
        HttpContext context,
        JsonElement arguments,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? selectedAgentIds = null);
}
