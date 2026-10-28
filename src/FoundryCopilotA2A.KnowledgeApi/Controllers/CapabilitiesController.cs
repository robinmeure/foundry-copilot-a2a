using FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/capabilities")]
public sealed class CapabilitiesController(
    IConfiguration configuration,
    IA2aKnowledgeService a2a,
    KnowledgeSkillRegistry skills) : ControllerBase
{
    [HttpGet]
    public ActionResult<CapabilitiesResponse> GetCapabilities()
    {
        return Ok(new CapabilitiesResponse(
            Backend: configuration["Knowledge:Backend"]!,
            MaxFileBytes: configuration.GetValue<int>("Knowledge:MaxFileBytes"),
            Extensions: Files.Extensions,
            DocumentModel: configuration["DocumentIntelligence:ModelId"],
            Graph: true,
            A2a: a2a.Enabled,
            Kamervragen: skills.IsKnown("kamervragen") && skills.IsConfigured("kamervragen"),
            Sources: skills.Sources()));
    }
}
