using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

/// <summary>
/// Confirms or cancels Graph changes staged by the orchestrator. Each action is bound to the caller and
/// conversation that produced it and can be used once; a consumed action stays consumed even if Graph fails.
/// </summary>
[Route("api/actions")]
public sealed class ActionsController(
    IConfiguration configuration,
    GraphActionStore pendingActions,
    GraphActions graphActions) : KnowledgeControllerBase(configuration)
{
    [HttpPost("{id}/confirm")]
    public async Task<ActionResult<GraphActionResult>> Confirm(string id, [FromBody] GraphActionRequest request, CancellationToken cancellationToken)
    {
        var proposal = TakePendingAction(id, request);
        return Ok(await graphActions.ExecuteAsync(HttpContext, id, proposal, cancellationToken));
    }

    [HttpPost("{id}/cancel")]
    public ActionResult<GraphActionResult> Cancel(string id, [FromBody] GraphActionRequest request)
    {
        TakePendingAction(id, request);
        return Ok(new GraphActionResult(id, "cancelled", "Cancelled. Nothing was changed."));
    }

    private GraphActionProposal TakePendingAction(string id, GraphActionRequest request)
    {
        var scope = ResolveConversationScope(request.ConversationId);
        return pendingActions.Take(id, scope)
            ?? throw new ApiException(
                StatusCodes.Status404NotFound,
                "action_not_found",
                "This change expired or was already handled. Ask again to stage a new one.");
    }
}
