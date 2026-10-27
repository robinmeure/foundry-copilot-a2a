using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Persists document metadata independently from indexed passage content.</summary>
public interface IDocumentStore
{
    /// <summary>Lists metadata, including pending or expired records; callers apply visibility rules.</summary>
    Task<DocumentRecord[]> List(string scope, CancellationToken ct);

    /// <summary>Atomically replaces the metadata record used to decide whether indexed content is visible.</summary>
    Task Commit(DocumentRecord document, CancellationToken ct);

    Task<DocumentRecord> Get(string scope, string id, CancellationToken ct);

    void Remove(DocumentRecord document);

    /// <summary>Enumerates metadata across scopes for retention cleanup, not for user-facing retrieval.</summary>
    Task<DocumentRecord[]> All(CancellationToken ct);
}
