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

    /// <summary>
    /// Invokes an approved agent with delegated authorization and returns untrusted evidence: the
    /// specialist's answer plus the sources it cited. <paramref name="onProgress"/> receives the
    /// specialist's own progress lines (plans, searches) while it works.
    /// </summary>
    Task<A2aKnowledgeAnswer> AskAsync(
        HttpContext context,
        JsonElement arguments,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? selectedAgentIds = null,
        Func<string, CancellationToken, ValueTask>? onProgress = null);
}

/// <summary>A specialist answer and the provider-reported sources it cited.</summary>
public sealed record A2aKnowledgeAnswer(Passage Answer, IReadOnlyList<Passage> Sources)
{
    public IEnumerable<Passage> Passages => Sources.Prepend(Answer);
}
