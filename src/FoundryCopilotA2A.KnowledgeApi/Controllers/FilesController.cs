using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

/// <summary>Downloads files generated during a chat turn. Files are bound to the caller and conversation and expire.</summary>
[Route("api/conversations/{conversation}/files")]
public sealed class FilesController(
    IConfiguration configuration,
    GeneratedFileStore generatedFiles) : KnowledgeControllerBase(configuration)
{
    [HttpGet("{id}")]
    public IActionResult Download(string conversation, string id)
    {
        var document = (GeneratedFileStore.IsValidId(id) ? generatedFiles.Get(id, ResolveConversationScope(conversation)) : null)
            ?? throw new ApiException(
                StatusCodes.Status404NotFound,
                "file_not_found",
                "This file expired. Ask again to recreate it.");
        Response.Headers.CacheControl = "no-store";
        return File(document.Content, document.MediaType, document.Name);
    }
}
