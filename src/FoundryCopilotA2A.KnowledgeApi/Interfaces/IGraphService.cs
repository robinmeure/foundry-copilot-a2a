using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using Microsoft.Graph;

namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Provides caller-authorized Graph reads, downloads and short-lived SDK operations.</summary>
public interface IGraphService
{
    Task<GraphPage> Browse(
        HttpContext context, string kind, string? parent, string? drive,
        string? query, string? nextPage, CancellationToken ct);

    Task<(byte[] Bytes, string Name, string ETag, string? Url)> Download(
        HttpContext context, ImportRequest input, CancellationToken ct);

    /// <summary>Rechecks current access and source version before indexed SharePoint content is used.</summary>
    Task Reauthorize(HttpContext context, DocumentRecord document, CancellationToken ct);

    Task<GraphEvidence> Evidence(
        HttpContext context, string question, string[] sources, CancellationToken ct);

    Task<JsonDocument> ReadForSkill(
        HttpContext context, string name, JsonElement arguments,
        CancellationToken ct, string? preferredTimeZone = null);

    Task<(JsonElement[] Items, bool IsFallback)> SearchForSkillAsync(
        HttpContext context, string source, string query, CancellationToken cancellationToken);

    /// <summary>Runs an operation with a delegated client that must not escape the callback.</summary>
    Task<T> WithClientAsync<T>(
        HttpContext context,
        Func<GraphServiceClient, Task<T>> send,
        CancellationToken cancellationToken);
}
