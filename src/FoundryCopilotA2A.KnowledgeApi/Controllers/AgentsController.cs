using FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[Route("api/agents")]
public sealed class AgentsController(
    IConfiguration configuration,
    IA2aKnowledgeService a2a) : KnowledgeControllerBase(configuration)
{
    /// <summary>APIM knowledge agents the signed-in user can select for local chat.</summary>
    [HttpGet]
    public async Task<ActionResult<KnowledgeAgentsResponse>> List(CancellationToken cancellationToken)
    {
        var agents = a2a.Enabled ? await a2a.GetAvailableAgentsAsync(cancellationToken) : [];
        return Ok(new KnowledgeAgentsResponse(agents));
    }
}
