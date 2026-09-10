using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class FoundryAccessCommandsTests
{
    private const string Redirect = "https://global.consent.azure-apim.net/redirect/new-connection";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteringCallbackPreservesWebSettingsAndDoesNotChangePermissions(bool alreadyPresent)
    {
        var web = new JsonObject
        {
            ["redirectUris"] = new JsonArray("https://backend.example/existing"),
            ["logoutUrl"] = "https://backend.example/logout",
            ["implicitGrantSettings"] = new JsonObject
            {
                ["enableAccessTokenIssuance"] = false,
                ["enableIdTokenIssuance"] = false
            }
        };
        if (alreadyPresent)
        {
            web["redirectUris"]!.AsArray().Add(Redirect);
        }
        var writes = 0;
        using var handler = new CitadelTestHandler(async request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("graph-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Get)
            {
                return request.RequestUri.AbsolutePath.EndsWith("/applications", StringComparison.Ordinal)
                    ? CitadelCommandsTests.Json(new
                    {
                        value = new[]
                        {
                            new
                            {
                                id = "backend-app", web,
                                api = new
                                {
                                    oauth2PermissionScopes = new[]
                                    {
                                        new { value = "access_as_user", isEnabled = true }
                                    }
                                }
                            }
                        }
                    })
                    : CitadelCommandsTests.Json(new { web });
            }
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/applications/backend-app", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("web", Assert.Single(body.RootElement.EnumerateObject()).Name);
            web = JsonNode.Parse(body.RootElement.GetProperty("web").GetRawText())!.AsObject();
            writes++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new HttpClient(handler);

        await FoundryAccessCommands.AddRedirectAsync(
            client, "graph-token", "backend-client", Redirect, CancellationToken.None);
        await FoundryAccessCommands.AddRedirectAsync(
            client, "graph-token", "backend-client", Redirect, CancellationToken.None);

        Assert.Equal(alreadyPresent ? 0 : 1, writes);
        Assert.Equal(2, web["redirectUris"]!.AsArray().Count);
        Assert.Equal("https://backend.example/existing", web["redirectUris"]![0]!.GetValue<string>());
        Assert.Equal("https://backend.example/logout", web["logoutUrl"]!.GetValue<string>());
        Assert.False(web["implicitGrantSettings"]!["enableAccessTokenIssuance"]!.GetValue<bool>());
        Assert.False(web["implicitGrantSettings"]!["enableIdTokenIssuance"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("http://global.consent.azure-apim.net/redirect/one")]
    [InlineData("https://global.consent.azure-apim.net.evil.example/redirect/one")]
    [InlineData("https://global.consent.azure-apim.net:444/redirect/one")]
    [InlineData("https://user:secret@global.consent.azure-apim.net/redirect/one")]
    [InlineData("https://global.consent.azure-apim.net/unrelated/one")]
    [InlineData("https://global.consent.azure-apim.net/redirect/one?query=value")]
    [InlineData("https://global.consent.azure-apim.net/redirect/one#fragment")]
    public void CallbackRegistrationRejectsUntrustedOrNoncanonicalUrls(string redirect)
    {
        Assert.Throws<CliException>(() => FoundryAccessCommands.ValidateConsentRedirect(redirect));
    }

    [Fact]
    public void AddingFoundryScopePreservesOtherPermissionsAndIsIdempotent()
    {
        using var existing = JsonDocument.Parse(
            """
            [
              {"resourceAppId":"power-platform","resourceAccess":[{"id":"invoke","type":"Scope"}]},
              {"resourceAppId":"foundry","resourceAccess":[{"id":"existing","type":"Scope"}]}
            ]
            """);
        var access = FoundryAccessCommands.AddDelegatedPermission(existing.RootElement, "foundry", "new");
        using var updated = JsonDocument.Parse(access.ToJsonString());
        var repeated = FoundryAccessCommands.AddDelegatedPermission(updated.RootElement, "foundry", "new");

        Assert.Equal(access.ToJsonString(), repeated.ToJsonString());
        Assert.Equal(2, access.Count);
        Assert.Equal("power-platform", access[0]!["resourceAppId"]!.GetValue<string>());
        Assert.Equal(2, access[1]!["resourceAccess"]!.AsArray().Count);
        Assert.DoesNotContain("\"Role\"", access.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsentIsScopedToFoundryAndPreservesExistingGrants(bool existingGrant)
    {
        var savedScope = existingGrant ? "existing.scope" : null;
        JsonArray? manifest = null;
        var writes = new List<string>();
        using var handler = new CitadelTestHandler(async request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("graph-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (request.Method == HttpMethod.Get && query.Contains("servicePrincipalNames", StringComparison.Ordinal))
            {
                Assert.Contains(FoundryAccessCommands.ResourceUri, query);
                return CitadelCommandsTests.Json(new
                {
                    value = new[]
                    {
                        new
                        {
                            id = "foundry-sp", appId = "foundry-app",
                            oauth2PermissionScopes = new[]
                            {
                                new { id = "foundry-scope", value = "user_impersonation", isEnabled = true }
                            }
                        }
                    }
                });
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/applications", StringComparison.Ordinal))
            {
                return CitadelCommandsTests.Json(new
                {
                    value = new[]
                    {
                        new
                        {
                            id = "backend-app",
                            api = new
                            {
                                oauth2PermissionScopes = new[]
                                {
                                    new { value = "access_as_user", isEnabled = true }
                                }
                            },
                            requiredResourceAccess = new[]
                            {
                                new
                                {
                                    resourceAppId = "power-platform",
                                    resourceAccess = new[] { new { id = "invoke", type = "Scope" } }
                                }
                            }
                        }
                    }
                });
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/servicePrincipals", StringComparison.Ordinal))
            {
                return CitadelCommandsTests.Json(new { value = new[] { new { id = "backend-sp" } } });
            }
            if (request.Method == HttpMethod.Get)
            {
                Assert.EndsWith("/oauth2PermissionGrants", path);
                Assert.Contains("clientId eq 'backend-sp'", query);
                Assert.Contains("resourceId eq 'foundry-sp'", query);
                return CitadelCommandsTests.Json(new
                {
                    value = savedScope is null
                        ? []
                        : new[] { new { id = "foundry-grant", scope = savedScope } }
                });
            }

            writes.Add(path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (path.EndsWith("/applications/backend-app", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.Single(body.RootElement.EnumerateObject());
                manifest = JsonNode.Parse(body.RootElement.GetProperty("requiredResourceAccess").GetRawText())!.AsArray();
            }
            else
            {
                Assert.Equal(existingGrant ? HttpMethod.Patch : HttpMethod.Post, request.Method);
                savedScope = body.RootElement.GetProperty("scope").GetString();
                if (!existingGrant)
                {
                    Assert.Equal("backend-sp", body.RootElement.GetProperty("clientId").GetString());
                    Assert.Equal("foundry-sp", body.RootElement.GetProperty("resourceId").GetString());
                    Assert.Equal("AllPrincipals", body.RootElement.GetProperty("consentType").GetString());
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new HttpClient(handler);

        await FoundryAccessCommands.GrantAsync(client, "graph-token", "backend-client", CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Count);
        Assert.Equal("power-platform", manifest[0]!["resourceAppId"]!.GetValue<string>());
        Assert.Equal("foundry-app", manifest[1]!["resourceAppId"]!.GetValue<string>());
        Assert.Equal(existingGrant ? "existing.scope user_impersonation" : "user_impersonation", savedScope);
        Assert.Equal(2, writes.Count);
        Assert.DoesNotContain(writes, path => path.Contains("appRoleAssignments", StringComparison.Ordinal));
    }
}
