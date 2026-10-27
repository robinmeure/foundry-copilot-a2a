using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Authentication;

/// <summary>Identifies the tenant and user that own a conversation scope.</summary>
public sealed record Caller(string Tenant, string User)
{
    public static Caller From(ClaimsPrincipal principal, bool mock)
    {
        if (mock)
        {
            return new Caller("local", "mock-user");
        }

        var tenantId = principal.FindFirstValue("tid")
            ?? throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "missing_identity",
                "Tenant claim is missing.");

        var userId = principal.FindFirstValue("oid")
            ?? throw new ApiException(
                StatusCodes.Status401Unauthorized,
                "missing_identity",
                "User claim is missing.");

        return new Caller(tenantId, userId);
    }

    public string Scope(string conversation)
    {
        if (!Guid.TryParseExact(conversation, "D", out _))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_conversation",
                "Conversation must be a UUID.");
        }

        var rawScope = Encoding.UTF8.GetBytes($"{Tenant}\n{User}\n{conversation}");
        return Convert.ToHexStringLower(SHA256.HashData(rawScope));
    }
}
