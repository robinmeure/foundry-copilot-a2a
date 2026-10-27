using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Reads authorized documents with explicit coverage and bounded analysis budgets.</summary>
public interface IDocumentRetrieval
{
    /// <summary>Reads the selected documents; scope, ownership and completeness checks must not be bypassed.</summary>
    Task<DocumentRetrievalResult> ReadAsync(
        string scope,
        string userId,
        DocumentRecord[] selected,
        DocumentRetrievalRequest request,
        ChatProgress progress,
        CancellationToken ct);

    /// <summary>Returns direct findings or bounded model-assisted findings with validated passage IDs.</summary>
    Task<DocumentRetrievalResult> AnalyzeAsync(
        DocumentRetrievalResult result,
        string question,
        ChatProgress progress,
        CancellationToken ct);
}
