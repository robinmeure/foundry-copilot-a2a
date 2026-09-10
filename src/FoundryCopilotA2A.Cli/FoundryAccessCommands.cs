using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoundryCopilotA2A.Cli;

internal static class FoundryAccessCommands
{
    internal const string ResourceUri = "https://ai.azure.com";
    internal const string DelegatedScope = "user_impersonation";

    public static async Task<int> GrantConsentAsync(
        CliContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("tenant-id", "api-client-id", "help");
        var tenantId = arguments.Require("tenant-id");
        var clientId = arguments.Require("api-client-id");
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _))
        {
            throw new CliException("--tenant-id and --api-client-id must be application/tenant GUIDs.");
        }

        var token = await GetGraphTokenAsync(context, tenantId, cancellationToken);
        await GrantAsync(context.HttpClient, token, clientId, cancellationToken);
        context.Out.WriteLine(
            $"Granted delegated Foundry access ({ResourceUri}/{DelegatedScope}) " +
            $"to the existing backend '{clientId}'.");
        context.Out.WriteLine(
            "Other permissions and registrations were preserved. No application role was granted. " +
            "End users still need Foundry Agent Consumer (or broader) access to the calling agent.");
        return 0;
    }

    public static async Task<int> RegisterRedirectAsync(
        CliContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("tenant-id", "api-client-id", "redirect-uri", "help");
        var tenantId = arguments.Require("tenant-id");
        var clientId = arguments.Require("api-client-id");
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _))
        {
            throw new CliException("--tenant-id and --api-client-id must be application/tenant GUIDs.");
        }

        var redirect = ValidateConsentRedirect(arguments.Require("redirect-uri"));
        var token = await GetGraphTokenAsync(context, tenantId, cancellationToken);
        await AddRedirectAsync(context.HttpClient, token, clientId, redirect, cancellationToken);
        context.Out.WriteLine($"Registered Foundry Web callback on the existing backend: {redirect}");
        context.Out.WriteLine(
            "Existing redirects and Web settings were preserved. No SPA, credentials, or permissions " +
            "were changed. This does not complete the native connection's end-user consent.");
        return 0;
    }

    private static async Task<string> GetGraphTokenAsync(
        CliContext context, string tenantId, CancellationToken cancellationToken)
    {
        var token = await context.Processes.CaptureAsync(
            "az",
            [
                "account", "get-access-token", "--tenant", tenantId,
                "--resource", "https://graph.microsoft.com",
                "--query", "accessToken", "--output", "tsv", "--only-show-errors"
            ],
            cancellationToken);
        return !string.IsNullOrWhiteSpace(token.StandardOutput)
            ? token.StandardOutput.Trim()
            : throw new CliException("Azure CLI returned no Microsoft Graph access token.");
    }

    internal static string ValidateConsentRedirect(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !uri.Host.EndsWith(".consent.azure-apim.net", StringComparison.OrdinalIgnoreCase) ||
            (uri.AbsolutePath != "/redirect" &&
             !uri.AbsolutePath.StartsWith("/redirect/", StringComparison.Ordinal)) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new CliException(
                "--redirect-uri must be the actual generated HTTPS Azure APIM consent callback, " +
                "without credentials, a query, or a fragment.");
        }
        return uri.AbsoluteUri;
    }

    internal static async Task AddRedirectAsync(
        HttpClient client, string token, string clientId, string redirect,
        CancellationToken cancellationToken)
    {
        redirect = ValidateConsentRedirect(redirect);
        using var applications = await GetAsync(client, token,
            "applications?$filter=" + Uri.EscapeDataString($"appId eq '{clientId}'") +
            "&$select=id,api,web", cancellationToken);
        var application = SingleValue(applications, "existing backend application");
        if (!application.GetProperty("api").GetProperty("oauth2PermissionScopes").EnumerateArray()
                .Any(scope => scope.GetProperty("value").GetString() == "access_as_user" &&
                              scope.GetProperty("isEnabled").GetBoolean()))
        {
            throw new CliException("The selected application is not an access_as_user backend API.");
        }

        var web = JsonNode.Parse(application.GetProperty("web").GetRawText()) as JsonObject
            ?? throw new CliException("The backend has an invalid Web platform configuration.");
        var redirects = web["redirectUris"] as JsonArray
            ?? throw new CliException("The backend has an invalid Web redirect URI list.");
        var existing = redirects.Select(item => item?.GetValue<string>()
            ?? throw new CliException("The backend has an invalid Web redirect URI.")).ToArray();
        if (existing.Contains(redirect, StringComparer.Ordinal))
        {
            return;
        }

        redirects.Add(redirect);
        var applicationId = application.GetProperty("id").GetString()!;
        await WriteAsync(client, token, HttpMethod.Patch, $"applications/{applicationId}",
            new { web }, cancellationToken);
        using var confirmed = await GetAsync(
            client, token, $"applications/{applicationId}?$select=web", cancellationToken);
        var saved = confirmed.RootElement.GetProperty("web").GetProperty("redirectUris")
            .EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!saved.IsSupersetOf(existing.Append(redirect)))
        {
            throw new CliException("The new and existing Web redirects were not all present after the update.");
        }
    }

    internal static async Task GrantAsync(
        HttpClient client, string token, string clientId, CancellationToken cancellationToken)
    {
        using var resourceList = await GetAsync(client, token,
            "servicePrincipals?$filter=" + Uri.EscapeDataString(
                $"servicePrincipalNames/any(name:name eq '{ResourceUri}')") +
            "&$select=id,appId,oauth2PermissionScopes", cancellationToken);
        var resource = SingleValue(resourceList, "Foundry resource service principal");
        var permission = resource.GetProperty("oauth2PermissionScopes").EnumerateArray()
            .Where(scope => scope.GetProperty("value").GetString() == DelegatedScope &&
                            scope.GetProperty("isEnabled").GetBoolean())
            .ToArray();
        if (permission.Length != 1)
        {
            throw new CliException(
                $"The Entra resource for '{ResourceUri}' does not expose one enabled '{DelegatedScope}' scope.");
        }
        var resourceAppId = resource.GetProperty("appId").GetString()!;
        var resourceObjectId = resource.GetProperty("id").GetString()!;
        var scopeId = permission[0].GetProperty("id").GetString()!;

        using var applications = await GetAsync(client, token,
            "applications?$filter=" + Uri.EscapeDataString($"appId eq '{clientId}'") +
            "&$select=id,api,requiredResourceAccess", cancellationToken);
        var application = SingleValue(applications, "existing backend application");
        if (!application.GetProperty("api").GetProperty("oauth2PermissionScopes").EnumerateArray()
                .Any(scope => scope.GetProperty("value").GetString() == "access_as_user" &&
                              scope.GetProperty("isEnabled").GetBoolean()))
        {
            throw new CliException("The selected application is not an access_as_user backend API.");
        }
        var applicationId = application.GetProperty("id").GetString()!;

        using var principals = await GetAsync(client, token,
            "servicePrincipals?$filter=" + Uri.EscapeDataString($"appId eq '{clientId}'") +
            "&$select=id", cancellationToken);
        var clientObjectId = SingleValue(principals, "existing backend service principal")
            .GetProperty("id").GetString()!;
        var grantQuery = "oauth2PermissionGrants?$filter=" + Uri.EscapeDataString(
            $"clientId eq '{clientObjectId}' and resourceId eq '{resourceObjectId}' " +
            "and consentType eq 'AllPrincipals'");
        using var grants = await GetAsync(client, token, grantQuery, cancellationToken);
        var existingGrants = grants.RootElement.GetProperty("value").EnumerateArray().ToArray();
        if (existingGrants.Length > 1)
        {
            throw new CliException("Found multiple tenant-wide Foundry grants; refusing an ambiguous update.");
        }

        var access = AddDelegatedPermission(
            application.GetProperty("requiredResourceAccess"), resourceAppId, scopeId);
        await WriteAsync(client, token, HttpMethod.Patch, $"applications/{applicationId}",
            new { requiredResourceAccess = access }, cancellationToken);
        if (existingGrants.Length == 0)
        {
            await WriteAsync(client, token, HttpMethod.Post, "oauth2PermissionGrants",
                new
                {
                    clientId = clientObjectId,
                    resourceId = resourceObjectId,
                    consentType = "AllPrincipals",
                    scope = DelegatedScope
                }, cancellationToken);
        }
        else
        {
            var grant = existingGrants[0];
            var scopes = (grant.GetProperty("scope").GetString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Append(DelegatedScope).Distinct(StringComparer.Ordinal);
            await WriteAsync(client, token, HttpMethod.Patch,
                $"oauth2PermissionGrants/{grant.GetProperty("id").GetString()}",
                new { scope = string.Join(' ', scopes) }, cancellationToken);
        }

        using var confirmed = await GetAsync(client, token, grantQuery, cancellationToken);
        var confirmedGrant = SingleValue(confirmed, "tenant-wide Foundry delegated grant");
        if (!(confirmedGrant.GetProperty("scope").GetString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(DelegatedScope, StringComparer.Ordinal))
        {
            throw new CliException("The Foundry delegated grant was not present after the update.");
        }
    }

    internal static JsonArray AddDelegatedPermission(
        JsonElement existing, string resourceAppId, string scopeId)
    {
        var access = JsonNode.Parse(existing.GetRawText()) as JsonArray
            ?? throw new CliException("The backend has an invalid requiredResourceAccess manifest.");
        var resource = access.OfType<JsonObject>().SingleOrDefault(item =>
            string.Equals(item["resourceAppId"]?.GetValue<string>(),
                resourceAppId, StringComparison.OrdinalIgnoreCase));
        if (resource is null)
        {
            resource = new JsonObject { ["resourceAppId"] = resourceAppId, ["resourceAccess"] = new JsonArray() };
            access.Add(resource);
        }
        var permissions = resource["resourceAccess"] as JsonArray
            ?? throw new CliException("The backend has an invalid resourceAccess manifest.");
        if (!permissions.OfType<JsonObject>().Any(item =>
                string.Equals(item["id"]?.GetValue<string>(), scopeId, StringComparison.OrdinalIgnoreCase) &&
                item["type"]?.GetValue<string>() == "Scope"))
        {
            permissions.Add(new JsonObject { ["id"] = scopeId, ["type"] = "Scope" });
        }
        return access;
    }

    private static JsonElement SingleValue(JsonDocument document, string description)
    {
        var values = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        return values.Length == 1
            ? values[0]
            : throw new CliException($"Expected exactly one {description}; found {values.Length}.");
    }

    private static async Task<JsonDocument> GetAsync(
        HttpClient client, string token, string path, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path, token);
        using var response = await client.SendAsync(request, cancellationToken);
        await RequireSuccessAsync(response, cancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task WriteAsync(
        HttpClient client, string token, HttpMethod method, string path, object body,
        CancellationToken cancellationToken)
    {
        using var request = Request(method, path, token);
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        await RequireSuccessAsync(response, cancellationToken);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task RequireSuccessAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var detail = body.RootElement.TryGetProperty("error", out var error) &&
                         error.TryGetProperty("message", out var message)
                ? message.GetString() ?? string.Empty
                : string.Empty;
            if (detail.Length > 800)
            {
                detail = detail[..800];
            }
            throw new CliException(
                $"Foundry backend registration configuration returned HTTP {(int)response.StatusCode} " +
                $"for {response.RequestMessage?.Method} " +
                $"{response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path)}: {detail} " +
                "An authorized directory identity/client is required; any completed registration " +
                "changes remain in place and rerunning the command is safe.");
        }
    }
}
