using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.BackgroundServices;

/// <summary>Periodically removes expired document metadata, passages, and conversation history.</summary>
public sealed class CleanupWorker(
    IDocumentStore documents,
    ISearchStore search,
    ILogger<CleanupWorker> logger,
    IConversationHistoryStore history) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PendingAbandonmentThreshold = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        try
        {
            do
            {
                await SweepExpiredDocumentsAsync(stoppingToken);
                try
                {
                    await history.SweepExpiredAsync(stoppingToken);
                }
                catch (Exception exception) when (IsRecoverable(exception, stoppingToken))
                {
                    logger.LogError(exception, "Conversation history cleanup failed; retrying on next sweep");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels both an active sweep and the timer.
        }
    }

    internal async Task SweepExpiredDocumentsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        DocumentRecord[] allDocuments;
        try
        {
            allDocuments = await documents.All(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            logger.LogError(exception, "Could not read cleanup metadata; retrying on next sweep");
            return;
        }

        foreach (var document in allDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isAbandonedPending =
                document.Status == "Pending" &&
                document.CreatedAt < now - PendingAbandonmentThreshold;
            var isExpiredOrDeleted =
                document.ExpiresAt <= now ||
                document.Status == "Deleted";

            if (!isExpiredOrDeleted && !isAbandonedPending)
            {
                continue;
            }

            try
            {
                await search.Delete(document, cancellationToken);
                documents.Remove(document);
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                logger.LogError(exception, "Document cleanup failed; retaining metadata and retrying on next sweep");
            }
        }
    }

    private static bool IsRecoverable(Exception exception, CancellationToken stoppingToken) =>
        !stoppingToken.IsCancellationRequested &&
        exception is IOException or UnauthorizedAccessException or ApiException or JsonException or HttpRequestException or OperationCanceledException;
}
