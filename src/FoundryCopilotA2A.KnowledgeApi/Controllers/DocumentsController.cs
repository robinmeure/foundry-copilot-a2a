using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Files;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.OfficeDocuments;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[Route("api/conversations/{conversation}/documents")]
public sealed class DocumentsController(
    IConfiguration configuration,
    IDocumentStore documents,
    ISearchStore search,
    IKnowledgeService knowledge,
    GeneratedFileStore generatedFiles,
    GraphActions graphActions) : KnowledgeControllerBase(configuration)
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
                 string.Equals(document.UserId, caller.User, StringComparison.Ordinal)))
            .Select(document => document with { OneDriveUploadExpiresAt = generatedFiles.UploadExpiresAt(scope, document.Id) });

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

        generatedFiles.AddUpload(scope, record.Id, new GeneratedDocument(UploadName(record.Name), MediaType(record.Name), bytes));

        return Ok(record with { OneDriveUploadExpiresAt = generatedFiles.UploadExpiresAt(scope, record.Id) });
    }

    [HttpPost("{id}/onedrive")]
    public async Task<ActionResult<GraphActionResult>> UploadToOneDrive(
        string conversation, string id, CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        var scope = caller.Scope(conversation);
        var document = await documents.Get(scope, id, cancellationToken);
        if (document.Status != "Ready" || document.ExpiresAt <= DateTimeOffset.UtcNow ||
            document.DriveId is not null ||
            (!string.IsNullOrWhiteSpace(document.UserId) && document.UserId != caller.User))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "document_not_found",
                "Local document not found in this conversation.");
        }

        var original = generatedFiles.TakeUpload(scope, id)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "file_not_found",
                "The original file is no longer available or an upload was already attempted. Attach it again to upload to OneDrive.");
        return Ok(await graphActions.SaveFileAsync(HttpContext, id, original,
            OfficeDocumentsConnector.Folder(Configuration), cancellationToken));
    }

    private static string UploadName(string name) =>
        OfficeDocumentOutline.SafeFileStem(Path.GetFileNameWithoutExtension(name)) + Path.GetExtension(name).ToLowerInvariant();

    private static string MediaType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".csv" => "text/csv",
        ".md" => "text/markdown",
        _ => "text/plain",
    };

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
        generatedFiles.TakeUpload(scope, id);

        return NoContent();
    }
}
