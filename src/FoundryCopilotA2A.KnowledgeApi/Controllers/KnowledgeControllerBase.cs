using FoundryCopilotA2A.KnowledgeApi.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[ApiController]
[ServiceFilter(typeof(KnowledgeAccessFilter))]
public abstract class KnowledgeControllerBase(IConfiguration configuration) : ControllerBase
{
    protected IConfiguration Configuration { get; } = configuration;

    protected Caller ResolveCaller()
    {
        return Caller.From(User, Configuration["Knowledge:Backend"] == "Mock");
    }

    protected string ResolveConversationScope(string conversation) =>
        ResolveCaller().Scope(conversation);
}
