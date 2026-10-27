using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[Route("api/graph")]
public sealed class GraphController(
    IConfiguration configuration,
    IGraphService graph) : KnowledgeControllerBase(configuration)
{
    [HttpGet("{kind}")]
    public async Task<ActionResult<GraphPage>> Browse(
        string kind,
        [FromQuery] string? parent,
        [FromQuery] string? drive,
        [FromQuery] string? query,
        [FromQuery] string? nextPage,
        CancellationToken cancellationToken)
    {
        var page = await graph.Browse(
            HttpContext,
            kind,
            parent,
            drive,
            query,
            nextPage,
            cancellationToken);

        return Ok(page);
    }
}
