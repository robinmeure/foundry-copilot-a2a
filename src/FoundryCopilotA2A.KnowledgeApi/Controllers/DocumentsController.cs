using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[Route("api/conversations/{conversation}/documents")]
public sealed class DocumentsController(
    IConfiguration configuration,
    IDocumentStore documents,
    ISearchStore search,
    IKnowledgeService knowledge) : KnowledgeControllerBase(configuration)
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DocumentRecord>>> ListDocuments(
        string conversation,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        var scope = caller.Scope(conversation);
        var now = DateTimeOffset.UtcNow;
        var allDocuments = await documents.List(scope, cancellationToken);

        var activeDocuments = allDocuments
            .Where(document =>
                document.Status == "Ready" &&
                document.ExpiresAt > now &&
                (string.IsNullOrWhiteSpace(document.UserId) ||
                 string.Equals(document.UserId, caller.User, StringComparison.Ordinal)));

        return Ok(activeDocuments);
    }

    [HttpPost]
    public async Task<ActionResult<DocumentRecord>> UploadDocument(
        string conversation,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        var scope = caller.Scope(conversation);

        if (!Request.HasFormContentType)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "multipart_required",
                "Upload a multipart document.");
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        if (form.Files.Count != 1)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "one_file_required",
                "Upload exactly one document.");
        }

        var uploadedFile = form.Files[0];
        await using var stream = uploadedFile.OpenReadStream();
        var bytes = await Files.ReadLimited(stream, Configuration.GetValue<int>("Knowledge:MaxFileBytes"), cancellationToken);

        var record = await knowledge.Import(
            scope: scope,
            name: uploadedFile.FileName,
            bytes: bytes,
            source: null,
            eTag: null,
            webUrl: null,
            ct: cancellationToken,
            userId: caller.User);

        return Ok(record);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteDocument(
        string conversation,
        string id,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        var scope = caller.Scope(conversation);
        var document = await documents.Get(scope, id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(document.UserId) &&
            !string.Equals(document.UserId, caller.User, StringComparison.Ordinal))
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                "document_not_found",
                "Document not found in this conversation.");
        }

        await documents.Commit(document with { Status = "Deleted" }, cancellationToken);
        await search.Delete(document, cancellationToken);
        documents.Remove(document);

        return NoContent();
    }
}
