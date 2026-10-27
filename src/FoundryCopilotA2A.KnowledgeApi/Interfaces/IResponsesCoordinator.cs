using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Streaming;

namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Runs live or mock response generation and applies evidence/citation validation.</summary>
public interface IResponsesCoordinator
{
    /// <summary>Executes live tool-assisted generation; returned session state is committed by the history store.</summary>
    Task<GroundedAnswer> Run(
        HttpContext context,
        string question,
        Passage[] initial,
        string[] sources,
        string[] initialWarnings,
        CancellationToken ct,
        IReadOnlyList<string>? availableDocumentNames = null,
        Func<string, CancellationToken, Task<Passage[]>>? searchDocumentsAsync = null,
        JsonElement? sessionState = null,
        UserContext? user = null,
        ChatProgress? progress = null,
        IReadOnlyCollection<string>? selectedAgents = null,
        Func<DocumentRetrievalRequest, CancellationToken, Task<DocumentRetrievalResult>>? retrieveDocumentsAsync = null);

    /// <summary>Exercises the same history and streaming protocol with deterministic, model-free answers.</summary>
    Task<GroundedAnswer> RunMock(
        HttpContext context,
        string question,
        Passage[] initial,
        string[] sources,
        string[] initialWarnings,
        CancellationToken ct,
        JsonElement? sessionState = null,
        UserContext? user = null,
        ChatProgress? progress = null,
        IReadOnlyCollection<string>? selectedAgents = null);
}
