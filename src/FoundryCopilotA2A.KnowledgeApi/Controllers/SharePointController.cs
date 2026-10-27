using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[Route("api/conversations/{conversation}/sharepoint")]
public sealed class SharePointController(
    IConfiguration configuration,
    IKnowledgeService knowledge,
    IGraphService graph) : KnowledgeControllerBase(configuration)
{
    [HttpPost]
    public async Task<ActionResult<DocumentRecord>> ImportSharePointDocument(
        string conversation,
        [FromBody] ImportRequest input,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        var scope = caller.Scope(conversation);
        var downloadedFile = await graph.Download(HttpContext, input, cancellationToken);

        var document = await knowledge.Import(
            scope: scope,
            name: downloadedFile.Name,
            bytes: downloadedFile.Bytes,
            source: input,
            eTag: downloadedFile.ETag,
            webUrl: downloadedFile.Url,
            ct: cancellationToken,
            userId: caller.User);

        return Ok(document);
    }
}
