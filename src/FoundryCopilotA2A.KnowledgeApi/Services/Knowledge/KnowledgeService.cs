using System.Globalization;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Context;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using FoundryCopilotA2A.KnowledgeApi.Streaming;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Knowledge;

/// <summary>Coordinates ingestion, current document authorization, grounded turns and staged actions.</summary>
public sealed class KnowledgeService(
    IConfiguration configuration,
    IDocumentStore documents,
    ISearchStore search,
    AzureServices azure,
    IGraphService graph,
    IResponsesCoordinator responses,
    IConversationHistoryStore history,
    GraphActionStore pendingActions,
    UserContextService userContexts,
    IA2aKnowledgeService a2a,
    KnowledgeSkillRegistry skills,
    IDocumentRetrieval retrieval,
    ILogger<KnowledgeService> logger) : IKnowledgeService, IDisposable
{
    private const int MaxDocumentsPerConversation = 30;
    private const int MaxQuestionCharacters = 4000;
    private const int MaxInitialEvidencePassages = 24;
    private const int MaxPassageCharacters = 3000;

    private static readonly string[] SupportedLiveSources = ["teams", "people"];
    private static readonly string[] PlainTextExtensions = [".txt", ".md", ".csv"];

    private readonly SemaphoreSlim ingestionSemaphore = new(2);
    private readonly SemaphoreSlim[] ingestionGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    public async Task<DocumentRecord> Import(
        string scope,
        string name,
        byte[] bytes,
        ImportRequest? source,
        string? eTag,
        string? webUrl,
        CancellationToken ct,
        string? userId = null)
    {
        Files.Validate(name, bytes);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(10));
        var cancellationToken = budget.Token;

        var scopeGate = ingestionGates[
            int.Parse(scope.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) % ingestionGates.Length];
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            // Same-scope waiters must not occupy the process-wide ingestion slots.
            await ingestionSemaphore.WaitAsync(cancellationToken);
            try
            {
                var existingDocuments = await documents.List(scope, cancellationToken);
                var activeCount = existingDocuments.Count(
                    document => document.Status == "Ready" && document.ExpiresAt > DateTimeOffset.UtcNow);

                if (activeCount >= MaxDocumentsPerConversation)
                {
                    throw new ApiException(
                        StatusCodes.Status409Conflict,
                        "document_quota",
                        $"Remove a document before importing more ({MaxDocumentsPerConversation} per conversation).");
                }

                var pages = await ExtractPagesAsync(name, bytes, cancellationToken);
                var documentId = Guid.NewGuid().ToString("N");
                var passages = Chunker.Split(documentId, name, pages, webUrl, userId);

                var pendingDocument = new DocumentRecord(
                    Id: documentId,
                    Scope: scope,
                    Name: name,
                    ExpiresAt: DateTimeOffset.UtcNow.AddDays(configuration.GetValue<int>("Knowledge:RetentionDays")),
                    Chunks: passages.Length,
                    DriveId: source?.DriveId,
                    ItemId: source?.ItemId,
                    ETag: eTag,
                    WebUrl: webUrl,
                    Status: "Pending",
                    UserId: userId);

                await documents.Commit(pendingDocument, cancellationToken);

                // Uncommitted chunks remain invisible and are reclaimed by the cleanup worker.
                await search.Index(pendingDocument, passages, cancellationToken);

                var readyDocument = pendingDocument with { Status = "Ready" };
                await documents.Commit(readyDocument, cancellationToken);

                return readyDocument;
            }
            finally
            {
                ingestionSemaphore.Release();
            }
        }
        finally
        {
            scopeGate.Release();
        }
    }

    public async Task<KnowledgeChatResult> Chat(
        HttpContext context,
        Caller caller,
        ChatRequest input,
        CancellationToken ct,
        ChatProgress? progress = null)
    {
        progress ??= ChatProgress.None;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(4));
        var cancellationToken = budget.Token;

        if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > MaxQuestionCharacters)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_question",
                "Enter a question of 1-4,000 characters.");
        }

        var requestedSources = input.Sources ?? [];
        skills.EnsureKnown(requestedSources);
        var selectedAgents = a2a.ResolveSelection(requestedSources, input.Agents);
        skills.EnsureConfigured(requestedSources);

        var scope = caller.Scope(input.ConversationId);
        await progress.ReportAsync(new ChatStatus("Checking your profile and time zone"), cancellationToken);
        var user = await userContexts.ResolveAsync(context, caller, input.TimeZone, cancellationToken);
        var answer = await history.RunAsync(scope,
            (session, token) => RunChatTurnAsync(
                context, caller, input, scope, requestedSources, selectedAgents, user, session, progress, token),
            cancellationToken);
        var actions = pendingActions.Add(scope, answer.Actions);
        return new KnowledgeChatResult(answer.Answer, answer.Evidence, answer.Warnings, actions);
    }

    private async Task<GroundedAnswer> RunChatTurnAsync(
        HttpContext context,
        Caller caller,
        ChatRequest input,
        string scope,
        string[] requestedSources,
        string[]? selectedAgents,
        UserContext user,
        JsonElement? session,
        ChatProgress progress,
        CancellationToken cancellationToken)
    {
        var allowedDocuments = await ListActiveDocumentsAsync(scope, caller.User, cancellationToken);

        if (configuration["Knowledge:Backend"] == "Mock")
        {
            if (allowedDocuments.Any())
            {
                await progress.ReportAsync(new ChatStatus("Searching your documents", ChatStatus.Started, "search_documents"), cancellationToken);
            }

            var mockPassages = await SearchAuthorizedDocumentsAsync(
                context,
                scope,
                caller.User,
                allowedDocuments,
                input.Text,
                cancellationToken);
            if (allowedDocuments.Any())
            {
                await progress.ReportAsync(new ChatStatus(
                    ToolStatus.Completed("search_documents", mockPassages.Length), ChatStatus.Completed, "search_documents"),
                    cancellationToken);
            }

            var mockAnswer = await responses.RunMock(
                context,
                input.Text,
                mockPassages,
                requestedSources,
                initialWarnings: [],
                cancellationToken,
                session,
                user,
                progress,
                selectedAgents);

            return mockAnswer;
        }

        var availableDocumentNames = allowedDocuments
            .Select(document => $"{document.Name} (id: {document.Id}, chunks: {document.Chunks})")
            .ToArray();

        var groundedAnswer = await responses.Run(
            context: context,
            question: input.Text,
            initial: [],
            sources: requestedSources,
            initialWarnings: [],
            ct: cancellationToken,
            availableDocumentNames: availableDocumentNames,
            searchDocumentsAsync: (query, searchToken) => SearchAuthorizedDocumentsAsync(
                context,
                scope,
                caller.User,
                allowedDocuments,
                query,
                searchToken),
            sessionState: session,
            user: user,
            progress: progress,
            selectedAgents: selectedAgents,
            retrieveDocumentsAsync: async (request, token) =>
            {
                var selected = DocumentRetrieval.SelectDocuments(request,
                    await ListActiveDocumentsAsync(scope, caller.User, token));
                foreach (var document in selected)
                {
                    await graph.Reauthorize(context, document, token);
                }
                var read = await retrieval.ReadAsync(scope, caller.User, selected, request, progress, token);
                await EnsureDocumentsStillActiveAsync(scope, caller.User, selected, token);
                var analyzed = await retrieval.AnalyzeAsync(read, request.Query, progress, token);
                foreach (var document in selected)
                {
                    await graph.Reauthorize(context, document, token);
                }
                await EnsureDocumentsStillActiveAsync(scope, caller.User, selected, token);
                return analyzed;
            });

        if (groundedAnswer.Warnings.Length > 0)
        {
            logger.LogWarning(
                "Knowledge chat completed with {Count} source warnings",
                groundedAnswer.Warnings.Length);
        }

        return groundedAnswer;
    }

    private async Task EnsureDocumentsStillActiveAsync(
        string scope, string userId, DocumentRecord[] selected, CancellationToken ct)
    {
        var active = await ListActiveDocumentsAsync(scope, userId, ct);
        if (selected.Any(document => !active.Any(current => current.Id == document.Id &&
                current.Chunks == document.Chunks && current.ETag == document.ETag)))
        {
            throw new ApiException(409, "document_unavailable",
                "A document was removed, expired or changed during reading. Retry with the current documents.");
        }
    }

    private async Task<Passage[]> SearchAuthorizedDocumentsAsync(
        HttpContext context,
        string scope,
        string userId,
        DocumentRecord[] allowedDocuments,
        string query,
        CancellationToken cancellationToken)
    {
        if (allowedDocuments.Length == 0)
        {
            return [];
        }

        foreach (var document in allowedDocuments)
        {
            await graph.Reauthorize(context, document, cancellationToken);
        }

        var documentPassages = await search.Search(
            scope,
            allowedDocuments,
            query,
            cancellationToken,
            userId);

        var stillAllowedIds = (await ListActiveDocumentsAsync(scope, userId, cancellationToken))
            .Select(document => document.Id)
            .ToHashSet();

        return documentPassages
            .Where(passage => stillAllowedIds.Contains(passage.DocumentId))
            .Take(MaxInitialEvidencePassages)
            .Select(passage => passage with
            {
                Content = passage.Content[..Math.Min(passage.Content.Length, MaxPassageCharacters)]
            })
            .ToArray();
    }

    private async Task<ExtractedPage[]> ExtractPagesAsync(
        string name,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (PlainTextExtensions.Contains(extension))
        {
            return Files.Text(bytes);
        }

        if (configuration["Knowledge:Backend"] == "Mock")
        {
            return
            [
                new ExtractedPage(1, $"Synthetic mock extraction of {name}. The project owner is Morgan.")
            ];
        }

        return await azure.Extract(bytes, cancellationToken);
    }

    private async Task<DocumentRecord[]> ListActiveDocumentsAsync(
        string scope,
        string userId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var allDocuments = await documents.List(scope, cancellationToken);

        return allDocuments
            .Where(document =>
                document.Status == "Ready" &&
                document.ExpiresAt > now &&
                (string.IsNullOrWhiteSpace(document.UserId) ||
                 string.Equals(document.UserId, userId, StringComparison.Ordinal)))
            .ToArray();
    }

    public void Dispose()
    {
        ingestionSemaphore.Dispose();
        foreach (var gate in ingestionGates) gate.Dispose();
    }
}
