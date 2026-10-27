using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Provides scope-isolated passage indexing, search, deletion, and full-document reads.</summary>
public interface ISearchStore
{
    /// <summary>Writes passages; the document metadata must still be committed as ready before retrieval.</summary>
    Task Index(DocumentRecord document, Passage[] passages, CancellationToken ct);

    /// <summary>Searches only the caller-authorized documents within the supplied conversation scope.</summary>
    Task<Passage[]> Search(
        string scope,
        DocumentRecord[] allowed,
        string query,
        CancellationToken ct,
        string? userId = null);

    Task Delete(DocumentRecord document, CancellationToken ct);

    /// <summary>Returns a complete authorized document or fails rather than silently omitting chunks.</summary>
    Task<Passage[]> ReadDocument(string scope, string userId, DocumentRecord document, CancellationToken ct);
}
