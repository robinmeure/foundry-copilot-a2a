using System.Security.Claims;

namespace FoundryCopilotA2A.Adapter;

public sealed class A2ARequestMetadataAccessor(
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    AgentCatalog agentCatalog)
{
    public A2ARequestMetadata Current
    {
        get
        {
            var context = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("No active HTTP request is available.");

            return new A2ARequestMetadata
            {
                ContextId = context.Items[AdapterConstants.ContextIdItem] as string,
                MessageId = context.Items[AdapterConstants.MessageIdItem] as string,
                AgentId = context.Items[AdapterConstants.SelectedAgentItem] as string ??
                          context.Items[AdapterConstants.RouteAgentItem] as string ??
                          agentCatalog.ResolveAgentId(
                              context.Request.Headers[AdapterConstants.AgentHeaderName].ToString()),
                UserId = ResolveUserId(context),
                PayloadHash = context.Items[AdapterConstants.PayloadHashItem] as string,
                BearerToken = ReadBearerToken(context),
                ChainTargetAgentId =
                    context.Items[AdapterConstants.ChainTargetItem] as string,
                IsAgentRoute = context.Items[AdapterConstants.RouteAgentItem] is string,
                History =
                    context.Items[AdapterConstants.HistoryItem]
                        as IReadOnlyList<A2AConversationTurn> ?? [],
                Files = context.Items[AdapterConstants.FilesItem] as IReadOnlyList<A2AInputFile> ?? [],
                ConversationHandles =
                    context.Items[AdapterConstants.ConversationHandlesItem] as IReadOnlyList<string> ?? []
            };
        }
    }

    /// <summary>
    /// Resolves a tenant-scoped caller identity. Fails closed: an authenticated deployment that
    /// cannot establish who the caller is must not fall back to a shared partition, because both
    /// the idempotency cache and the conversation store are keyed on this value.
    /// </summary>
    public string ResolveUserId(HttpContext context)
    {
        var objectId = context.User.FindFirstValue("oid")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");

        if (objectId is not null)
        {
            // "oid" is unique only within a tenant, so scope it with "tid".
            var tenantId = context.User.FindFirstValue("tid") ?? "unknown-tenant";
            return $"{tenantId}|{objectId}";
        }

        if (!configuration.GetValue<bool>("Authentication:Enabled") &&
            configuration.GetValue<bool>("Adapter:AllowAnonymousDevelopmentMode"))
        {
            return AdapterConstants.AnonymousDevelopmentUser;
        }

        throw new UnauthorizedAccessException(
            "The delegated caller identity could not be established from the request token.");
    }

    private static string? ReadBearerToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}
