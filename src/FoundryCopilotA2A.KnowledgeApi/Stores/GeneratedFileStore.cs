using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Files;

namespace FoundryCopilotA2A.KnowledgeApi.Stores;

/// <summary>
/// Holds generated Word and PowerPoint files and the originals of local uploads in memory for the caller and
/// conversation that produced them, so they can be downloaded or saved to OneDrive. A restart drops them.
/// </summary>
public sealed partial class GeneratedFileStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(60);

    private const int MaxPerScope = 10;
    private const int MaxTotal = 500;
    private const long MaxTotalBytes = 256L * 1024 * 1024;

    private readonly Dictionary<string, StoredFile> files = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private long sequence;

    public GeneratedFileStore()
        : this(TimeProvider.System)
    {
    }

    internal GeneratedFileStore(TimeProvider clock)
    {
        this.clock = clock;
    }

    private sealed record StoredFile(string Id, string Scope, GeneratedDocument Document, long Sequence, DateTimeOffset ExpiresAt,
        string? SourceDocumentId);

    public GeneratedFileView[] Add(string scope, IReadOnlyCollection<GeneratedDocument> documents) => Add(scope, documents, null);

    public void AddUpload(string scope, string documentId, GeneratedDocument document) => Add(scope, [document], documentId);

    public DateTimeOffset? UploadExpiresAt(string scope, string documentId)
    {
        lock (gate)
        {
            return files.Values.FirstOrDefault(file => file.Scope == scope && file.SourceDocumentId == documentId &&
                file.ExpiresAt > clock.GetUtcNow())?.ExpiresAt;
        }
    }

    /// <summary>Consumes a local original once, including when the subsequent Graph call fails.</summary>
    public GeneratedDocument? TakeUpload(string scope, string documentId)
    {
        lock (gate)
        {
            var file = files.Values.FirstOrDefault(file => file.Scope == scope && file.SourceDocumentId == documentId);
            if (file is null) return null;
            files.Remove(file.Id);
            return file.ExpiresAt > clock.GetUtcNow() ? file.Document : null;
        }
    }

    private GeneratedFileView[] Add(string scope, IReadOnlyCollection<GeneratedDocument> documents, string? sourceDocumentId)
    {
        if (documents.Count == 0)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        lock (gate)
        {
            RemoveExpired(now);
            var added = documents.Select(document =>
            {
                var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                var stored = new StoredFile(id, scope, document, ++sequence, now + Lifetime, sourceDocumentId);
                files[id] = stored;
                return stored;
            }).ToArray();

            EvictOldest(files.Values.Where(file => file.Scope == scope), MaxPerScope);
            EvictOldest(files.Values, MaxTotal);
            while (files.Values.Sum(file => (long)file.Document.Content.Length) > MaxTotalBytes)
            {
                files.Remove(files.Values.MinBy(file => file.Sequence)!.Id);
            }
            return added
                .Where(file => files.ContainsKey(file.Id))
                .Select(file => new GeneratedFileView(file.Id, file.Document.Name, file.Document.MediaType,
                    file.Document.Content.Length, file.ExpiresAt))
                .ToArray();
        }
    }

    /// <summary>Returns the file when it belongs to the scope and has not expired.</summary>
    public GeneratedDocument? Get(string id, string scope)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        lock (gate)
        {
            return files.TryGetValue(id, out var file) && file.Scope == scope && file.ExpiresAt > clock.GetUtcNow()
                ? file.Document
                : null;
        }
    }

    public static bool IsValidId(string? id) =>
        id is not null && FileId().IsMatch(id);

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var expired in files.Values.Where(file => file.ExpiresAt <= now).ToArray())
        {
            files.Remove(expired.Id);
        }
    }

    private void EvictOldest(IEnumerable<StoredFile> candidates, int limit)
    {
        var ordered = candidates.OrderBy(file => file.Sequence).ToArray();
        foreach (var evicted in ordered.Take(Math.Max(0, ordered.Length - limit)))
        {
            files.Remove(evicted.Id);
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileId();
}
