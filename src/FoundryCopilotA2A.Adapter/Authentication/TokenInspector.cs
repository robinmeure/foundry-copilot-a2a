using System.IdentityModel.Tokens.Jwt;

namespace FoundryCopilotA2A.Adapter;

public static class TokenInspector
{
    /// <summary>Best-effort audience read. The token itself is validated by the JWT middleware.</summary>
    public static string? ReadAudience(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.CanReadToken(jwt)
                ? handler.ReadJwtToken(jwt).Audiences.FirstOrDefault()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Distinguishes an app-only token from a delegated one. A delegated token carries a
    /// user identity, so it can be exchanged on-behalf-of. An app-only token has no user
    /// behind it, and Entra rejects OBO for it with AADSTS7000114.
    /// </summary>
    public static bool IsAppOnly(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(jwt))
            {
                return false;
            }

            var token = handler.ReadJwtToken(jwt);
            var identityType = token.Claims
                .FirstOrDefault(claim => claim.Type == "idtyp")?.Value;
            if (identityType is not null)
            {
                return string.Equals(identityType, "app", StringComparison.OrdinalIgnoreCase);
            }

            // Older tokens omit idtyp. A delegated token always carries a scope claim; an
            // app-only token carries roles (or nothing) instead.
            var hasScope = token.Claims.Any(claim =>
                claim.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope");
            return !hasScope;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
