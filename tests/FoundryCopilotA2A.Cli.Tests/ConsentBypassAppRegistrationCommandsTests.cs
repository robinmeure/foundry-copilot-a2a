using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class ConsentBypassAppRegistrationCommandsTests
{
    private static readonly Guid ScopeId =
        Guid.Parse("db2c958a-27e1-4238-8eb8-610a8b5d8436");

    [Fact]
    public void ResolvesTheEnabledAdminActionsScope()
    {
        using var scopes = JsonDocument.Parse(
            """
            [
              {
                "id": "db2c958a-27e1-4238-8eb8-610a8b5d8436",
                "value": "CopilotStudio.AdminActions.Invoke",
                "isEnabled": true
              },
              {
                "id": "33333333-3333-3333-3333-333333333333",
                "value": "other",
                "isEnabled": true
              }
            ]
            """);

        var resolved = ConsentBypassAppRegistrationCommands.ResolveAdminScopeId(
            scopes.RootElement);

        Assert.Equal(ScopeId, resolved);
    }

    [Fact]
    public void AcceptsTheExactLeastPrivilegeRegistration()
    {
        using var application = Registration();

        ConsentBypassAppRegistrationCommands.ValidateRegistration(
            application.RootElement,
            ConsentBypassAppRegistrationCommands.DefaultDisplayName,
            ScopeId);
    }

    [Theory]
    [InlineData(
        """{"displayName":"foundry-copilot-a2a-consent-admin","signInAudience":"AzureADMultipleOrgs","isFallbackPublicClient":true,"publicClient":{"redirectUris":["http://localhost"]},"requiredResourceAccess":[{"resourceAppId":"8578e004-a5c6-46e7-913e-12f58912df43","resourceAccess":[{"id":"db2c958a-27e1-4238-8eb8-610a8b5d8436","type":"Scope"}]}],"passwordCredentials":[],"keyCredentials":[],"web":{"redirectUris":[]},"spa":{"redirectUris":[]}}""",
        "single-tenant audience")]
    [InlineData(
        """{"displayName":"foundry-copilot-a2a-consent-admin","signInAudience":"AzureADMyOrg","isFallbackPublicClient":true,"publicClient":{"redirectUris":["http://localhost","http://localhost:1234"]},"requiredResourceAccess":[{"resourceAppId":"8578e004-a5c6-46e7-913e-12f58912df43","resourceAccess":[{"id":"db2c958a-27e1-4238-8eb8-610a8b5d8436","type":"Scope"}]}],"passwordCredentials":[],"keyCredentials":[],"web":{"redirectUris":[]},"spa":{"redirectUris":[]}}""",
        "single native redirect")]
    [InlineData(
        """{"displayName":"foundry-copilot-a2a-consent-admin","signInAudience":"AzureADMyOrg","isFallbackPublicClient":true,"publicClient":{"redirectUris":["http://localhost"]},"requiredResourceAccess":[{"resourceAppId":"8578e004-a5c6-46e7-913e-12f58912df43","resourceAccess":[{"id":"db2c958a-27e1-4238-8eb8-610a8b5d8436","type":"Scope"},{"id":"33333333-3333-3333-3333-333333333333","type":"Scope"}]}],"passwordCredentials":[],"keyCredentials":[],"web":{"redirectUris":[]},"spa":{"redirectUris":[]}}""",
        "only delegated permission")]
    [InlineData(
        """{"displayName":"foundry-copilot-a2a-consent-admin","signInAudience":"AzureADMyOrg","isFallbackPublicClient":true,"publicClient":{"redirectUris":["http://localhost"]},"requiredResourceAccess":[{"resourceAppId":"8578e004-a5c6-46e7-913e-12f58912df43","resourceAccess":[{"id":"db2c958a-27e1-4238-8eb8-610a8b5d8436","type":"Scope"}]}],"passwordCredentials":[{"keyId":"44444444-4444-4444-4444-444444444444"}],"keyCredentials":[],"web":{"redirectUris":[]},"spa":{"redirectUris":[]}}""",
        "no client credentials")]
    public void RejectsBroaderOrConflictingRegistrations(
        string json,
        string expectedProblem)
    {
        using var application = JsonDocument.Parse(json);

        var exception = Assert.Throws<CliException>(
            () => ConsentBypassAppRegistrationCommands.ValidateRegistration(
                application.RootElement,
                ConsentBypassAppRegistrationCommands.DefaultDisplayName,
                ScopeId));

        Assert.Contains(expectedProblem, exception.Message);
        Assert.Contains("Refusing to overwrite", exception.Message);
    }

    private static JsonDocument Registration() => JsonDocument.Parse(
        """
        {
          "displayName": "foundry-copilot-a2a-consent-admin",
          "signInAudience": "AzureADMyOrg",
          "isFallbackPublicClient": true,
          "publicClient": {
            "redirectUris": ["http://localhost"]
          },
          "requiredResourceAccess": [
            {
              "resourceAppId": "8578e004-a5c6-46e7-913e-12f58912df43",
              "resourceAccess": [
                {
                  "id": "db2c958a-27e1-4238-8eb8-610a8b5d8436",
                  "type": "Scope"
                }
              ]
            }
          ],
          "passwordCredentials": [],
          "keyCredentials": [],
          "web": {
            "redirectUris": []
          },
          "spa": {
            "redirectUris": []
          }
        }
        """);
}
