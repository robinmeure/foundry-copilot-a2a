using Microsoft.Extensions.Caching.Memory;

namespace FoundryCopilotA2A.Adapter;

/// <summary>
/// Maps an A2A contextId to a Copilot Studio conversationId, partitioned by caller identity.
/// Bounded and sliding-expiry so it cannot grow without limit.
/// </summary>
public sealed class CopilotConversationStore : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CopilotConversationStore(IConfiguration configuration)
    {
        _ttl = TimeSpan.FromMinutes(configuration.GetValue("Adapter:ConversationTtlMinutes", 30));
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = configuration.GetValue<long>("Adapter:MaxCacheEntries", 10_000),
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });
    }

    public string? Get(string userId, string agentId, string? contextId) =>
        contextId is null ? null : _cache.Get<string>(ToKey(userId, agentId, contextId));

    public void Set(string userId, string agentId, string? contextId, string conversationId)
    {
        if (contextId is null)
        {
            return;
        }

        _cache.Set(ToKey(userId, agentId, contextId), conversationId, new MemoryCacheEntryOptions
        {
            SlidingExpiration = _ttl,
            Size = 1
        });
    }

    public void Remove(string userId, string agentId, string? contextId)
    {
        if (contextId is not null)
        {
            _cache.Remove(ToKey(userId, agentId, contextId));
        }
    }

    private static string ToKey(string userId, string agentId, string contextId) =>
        $"{userId}|{agentId}|{contextId}";

    public void Dispose() => _cache.Dispose();
}
