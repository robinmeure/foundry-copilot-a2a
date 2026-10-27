using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Coordinates document imports and grounded chat turns.</summary>
public interface IKnowledgeService
{
    /// <summary>Extracts and indexes a document, exposing it only after indexing succeeds.</summary>
    Task<DocumentRecord> Import(
        string scope,
        string name,
        byte[] bytes,
        ImportRequest? source,
        string? eTag,
        string? webUrl,
        CancellationToken ct,
        string? userId = null);

    /// <summary>Runs a caller-scoped turn; proposed Graph writes remain unexecuted until confirmation.</summary>
    Task<KnowledgeChatResult> Chat(
        HttpContext context,
        Caller caller,
        ChatRequest input,
        CancellationToken ct,
        ChatProgress? progress = null);
}
