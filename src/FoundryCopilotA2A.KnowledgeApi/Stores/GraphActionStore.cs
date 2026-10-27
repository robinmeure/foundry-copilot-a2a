using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

namespace FoundryCopilotA2A.KnowledgeApi.Stores;

/// <summary>
/// Holds staged Graph changes in memory until the same user confirms or cancels them in the same
/// conversation. Entries are single-use and short-lived; a restart simply drops pending proposals.
/// </summary>
public sealed partial class GraphActionStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private const int MaxPerScope = 10;
    private const int MaxTotal = 2000;

    private readonly Dictionary<string, PendingAction> actions = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private long sequence;

    public GraphActionStore()
        : this(TimeProvider.System)
    {
    }

    internal GraphActionStore(TimeProvider clock)
    {
        this.clock = clock;
    }

    private sealed record PendingAction(
        string Id,
        string Scope,
        GraphActionProposal Proposal,
        long Sequence,
        DateTimeOffset ExpiresAt);

    public GraphActionView[] Add(string scope, IReadOnlyCollection<GraphActionProposal> proposals)
    {
        if (proposals.Count == 0)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        lock (gate)
        {
            RemoveExpired(now);

            var views = new List<GraphActionView>(proposals.Count);
            foreach (var proposal in proposals)
            {
                var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                var pending = new PendingAction(id, scope, proposal, ++sequence, now + Lifetime);
                actions[id] = pending;
                views.Add(new GraphActionView(id, proposal.Kind, proposal.Title, proposal.Summary, proposal.Details, pending.ExpiresAt));
            }

            EvictOldest(actions.Values.Where(action => action.Scope == scope), MaxPerScope);
            EvictOldest(actions.Values, MaxTotal);
            return views.Where(view => actions.ContainsKey(view.Id)).ToArray();
        }
    }

    /// <summary>Atomically removes and returns the action when it belongs to the scope and has not expired.</summary>
    public GraphActionProposal? Take(string id, string scope)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        lock (gate)
        {
            if (!actions.TryGetValue(id, out var pending) || pending.Scope != scope)
            {
                return null;
            }

            actions.Remove(id);
            return pending.ExpiresAt > clock.GetUtcNow() ? pending.Proposal : null;
        }
    }

    public static bool IsValidId(string? id) =>
        id is not null && ActionId().IsMatch(id);

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var expired in actions.Values.Where(action => action.ExpiresAt <= now).ToArray())
        {
            actions.Remove(expired.Id);
        }
    }

    private void EvictOldest(IEnumerable<PendingAction> candidates, int limit)
    {
        var ordered = candidates.OrderBy(action => action.Sequence).ToArray();
        foreach (var evicted in ordered.Take(Math.Max(0, ordered.Length - limit)))
        {
            actions.Remove(evicted.Id);
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionId();
}
