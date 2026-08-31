using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace FoundryCopilotA2A.Adapter.Tests;

/// <summary>
/// A Foundry connection that authenticates as the project managed identity sends an app-only
/// token, which has no user behind it. Entra rejects the on-behalf-of exchange for such tokens
/// with AADSTS7000114, so the adapter must detect them and use the client-credentials flow
/// instead. These tests pin that detection, because getting it wrong surfaces as an opaque
/// 400 from the token endpoint long after the request left Foundry.
/// </summary>
public class AppOnlyTokenTests
{
    private static string BuildToken(params Claim[] claims)
    {
        var token = new JwtSecurityToken(
            issuer: "https://sts.windows.net/tenant/",
            audience: "api://adapter",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: null);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void AppOnlyTokenIsDetectedFromIdentityTypeClaim()
    {
        var token = BuildToken(new Claim("idtyp", "app"));

        Assert.True(TokenInspector.IsAppOnly(token));
    }

    [Fact]
    public void DelegatedTokenIsNotTreatedAsAppOnly()
    {
        var token = BuildToken(
            new Claim("idtyp", "user"),
            new Claim("scp", "access_as_user"),
            new Claim("oid", "11111111-2222-3333-4444-555555555555"));

        Assert.False(TokenInspector.IsAppOnly(token));
    }

    [Fact]
    public void DelegatedTokenWithoutIdentityTypeIsDetectedFromScopeClaim()
    {
        // Older tokens omit idtyp; a scope claim only ever appears on a delegated token.
        var token = BuildToken(new Claim("scp", "access_as_user"));

        Assert.False(TokenInspector.IsAppOnly(token));
    }

    [Fact]
    public void AppOnlyTokenWithoutIdentityTypeIsDetectedFromMissingScope()
    {
        var token = BuildToken(new Claim("roles", "Adapter.Invoke"));

        Assert.True(TokenInspector.IsAppOnly(token));
    }

    [Fact]
    public void MalformedTokenIsNotTreatedAsAppOnly()
    {
        // Never route an unreadable token to the app-only path; let normal validation reject it.
        Assert.False(TokenInspector.IsAppOnly("not-a-jwt"));
    }

    [Theory]
    [InlineData(
        "https://api.powerplatform.com/CopilotStudio.Copilots.Invoke",
        "https://api.powerplatform.com/.default")]
    [InlineData(
        "https://api.powerplatform.com/.default",
        "https://api.powerplatform.com/.default")]
    public void DelegatedScopeIsConvertedToResourceDefaultScope(string scope, string expected)
    {
        // The client-credentials flow only accepts resource-wide "/.default" scopes.
        Assert.Equal(expected, OboTokenBroker.ResolveDefaultScope(scope));
    }
}
