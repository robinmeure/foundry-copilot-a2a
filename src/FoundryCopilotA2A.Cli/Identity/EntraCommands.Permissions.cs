using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal static partial class EntraCommands
{
    public static async Task<int> AddFederatedCredentialAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("client-id", "principal-id", "name", "help");

        var clientId = RequireGuid(arguments, "client-id");
        var principalId = RequireGuid(arguments, "principal-id");
        var name = arguments.Optional("name", "managed-identity")!;
        var tenantId = await ResolveTenantIdAsync(context, cancellationToken);
        var issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        const string audience = "api://AzureADTokenExchange";

        var existingJson = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "federated-credential", "list",
            "--id", clientId,
            "--output", "json"))
            .StandardOutput;
        using var existing = JsonDocument.Parse(existingJson);
        var namedCredentials = existing.RootElement
            .EnumerateArray()
            .Where(item => string.Equals(
                item.GetProperty("name").GetString(),
                name,
                StringComparison.Ordinal))
            .ToArray();
        if (namedCredentials.Length > 1)
        {
            throw new CliException(
                $"Application {clientId} has multiple federated credentials named '{name}'.");
        }

        if (namedCredentials.Length == 1)
        {
            var credential = namedCredentials[0];
            var audiences = credential.GetProperty("audiences")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            if (!string.Equals(
                    credential.GetProperty("issuer").GetString(),
                    issuer,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    credential.GetProperty("subject").GetString(),
                    principalId,
                    StringComparison.OrdinalIgnoreCase) ||
                audiences.Length != 1 ||
                !string.Equals(audiences[0], audience, StringComparison.Ordinal))
            {
                throw new CliException(
                    $"Federated credential '{name}' exists with different trust settings.");
            }

            context.Out.WriteLine(
                $"Federated credential '{name}' already matches the managed identity.");
            return 0;
        }

        var body = JsonSerializer.Serialize(new
        {
            name,
            issuer,
            subject = principalId,
            audiences = new[] { audience },
            description = "Managed identity federation for foundry-copilot-a2a."
        });
        using var bodyFile = await TemporaryTextFile.CreateAsync(body, cancellationToken);
        await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "federated-credential", "create",
            "--id", clientId,
            "--parameters", bodyFile.AzureCliReference,
            "--only-show-errors");

        context.Out.WriteLine(
            $"Added federated credential '{name}' to application {clientId}.");
        return 0;
    }

    public static async Task<int> ConsentAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("tenant-id", "client-id", "scope", "help");
        var tenantId = arguments.Require("tenant-id");
        var clientId = arguments.Require("client-id");
        var scope = arguments.Optional("scope", DefaultScope)!;
        var scopes = scope.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Length == 0)
        {
            throw new CliException("At least one delegated scope is required.");
        }

        var result = await DeviceCodeAuth.AcquireAsync(
            context, tenantId, clientId, scopes, cancellationToken);

        context.Out.WriteLine(
            $"Consent granted for {result.Account?.Username ?? "the signed-in user"}; " +
            $"token expires at {result.ExpiresOn:u}.");
        return 0;
    }

    /// <summary>
    /// Grants an existing frontend registration delegated access to the hub's access_as_user
    /// scope so the browser requests a hub-audience token instead of an adapter-audience one.
    /// </summary>
    public static async Task<int> GrantHubAccessAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("client-id", "hub-client-id", "admin-consent", "help");

        var clientId = RequireGuid(arguments, "client-id");
        var hubClientId = RequireGuid(arguments, "hub-client-id");
        if (string.Equals(clientId, hubClientId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                "The frontend and hub client IDs must be different applications.");
        }

        var attemptAdminConsent = arguments.Flag("admin-consent");
        var tenantId = await ResolveTenantIdAsync(context, cancellationToken);
        context.Out.WriteLine($"Tenant: {tenantId}");

        var hubScopeId = await ResolveAccessAsUserScopeIdAsync(
            context, hubClientId, cancellationToken);

        context.Out.WriteLine("Granting the frontend delegated access to the hub...");
        await RetryAsync(
            () => AzAsync(
                context,
                cancellationToken,
                "ad", "app", "permission", "add",
                "--id", clientId,
                "--api", hubClientId,
                "--api-permissions", $"{hubScopeId}=Scope",
                "--only-show-errors"),
            cancellationToken);

        if (attemptAdminConsent)
        {
            context.Out.WriteLine("Attempting tenant-wide admin consent...");
            await AzAsync(
                context,
                cancellationToken,
                "ad", "app", "permission", "admin-consent",
                "--id", clientId,
                "--only-show-errors");
        }

        context.Out.WriteLine();
        context.Out.WriteLine("The frontend can now request the hub scope.");
        context.Out.WriteLine($"Frontend ClientId:  {clientId}");
        context.Out.WriteLine($"Requested scope:    api://{hubClientId}/access_as_user");
        context.Out.WriteLine(
            $"Frontend setting:   VITE_ADAPTER_API_CLIENT_ID={hubClientId}");
        context.Out.WriteLine(
            "The existing adapter permission is left in place. Remove it once every caller " +
            "uses the hub.");
        return 0;
    }

    /// <summary>
    /// Preauthorizes a client application for an API's delegated scope. Preauthorization is the
    /// documented way to let a middle tier receive a scope in the OBO flow without a separate
    /// consent prompt, and it does not require a privileged role.
    /// </summary>
    public static async Task<int> PreauthorizeClientAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("api-client-id", "client-id", "scope", "help");

        var apiClientId = RequireGuid(arguments, "api-client-id");
        var clientId = RequireGuid(arguments, "client-id");
        if (string.Equals(apiClientId, clientId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                "The API and client IDs must be different applications.");
        }

        var scope = arguments.Optional("scope", "access_as_user")!;
        var tenantId = await ResolveTenantIdAsync(context, cancellationToken);
        context.Out.WriteLine($"Tenant: {tenantId}");

        var scopeId = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "show",
            "--id", apiClientId,
            "--query", $"api.oauth2PermissionScopes[?value=='{scope}' && isEnabled].id | [0]",
            "--output", "tsv"))
            .StandardOutput.Trim();
        if (!Guid.TryParse(scopeId, out _))
        {
            throw new CliException(
                $"Application {apiClientId} does not expose an enabled {scope} scope.");
        }

        var objectId = await ResolveApplicationObjectIdAsync(
            context, apiClientId, cancellationToken);
        var existingJson = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "show",
            "--id", apiClientId,
            "--query", "api.preAuthorizedApplications",
            "--output", "json"))
            .StandardOutput.Trim();

        // Preserve every existing entry: this property is replaced wholesale on PATCH.
        var entries = new List<PreauthorizedApplication>();
        if (!string.IsNullOrWhiteSpace(existingJson) && existingJson != "null")
        {
            entries.AddRange(
                JsonSerializer.Deserialize<List<PreauthorizedApplication>>(existingJson) ?? []);
        }

        var match = entries.FirstOrDefault(entry =>
            string.Equals(entry.AppId, clientId, StringComparison.OrdinalIgnoreCase));
        if (match is not null && match.DelegatedPermissionIds.Contains(scopeId, StringComparer.OrdinalIgnoreCase))
        {
            context.Out.WriteLine(
                $"Client {clientId} is already preauthorized for {scope} on {apiClientId}.");
            return 0;
        }

        if (match is not null)
        {
            entries.Remove(match);
            entries.Add(new PreauthorizedApplication
            {
                AppId = match.AppId,
                DelegatedPermissionIds = [.. match.DelegatedPermissionIds, scopeId]
            });
        }
        else
        {
            entries.Add(new PreauthorizedApplication
            {
                AppId = clientId,
                DelegatedPermissionIds = [scopeId]
            });
        }

        context.Out.WriteLine($"Preauthorizing {clientId} for {scope} on {apiClientId}...");
        var patchBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["api"] = new Dictionary<string, object?>
            {
                ["preAuthorizedApplications"] = entries
                    .Select(entry => new
                    {
                        appId = entry.AppId,
                        delegatedPermissionIds = entry.DelegatedPermissionIds
                    })
                    .ToArray()
            }
        });

        using var patchFile = await TemporaryTextFile.CreateAsync(patchBody, cancellationToken);
        await RetryAsync(
            () => AzAsync(
                context,
                cancellationToken,
                "rest",
                "--method", "PATCH",
                "--url", $"https://graph.microsoft.com/v1.0/applications/{objectId}",
                "--headers", "Content-Type=application/json",
                "--body", patchFile.AzureCliReference,
                "--output", "none"),
            cancellationToken);

        context.Out.WriteLine();
        context.Out.WriteLine(
            $"{clientId} can now receive api://{apiClientId}/{scope} without a consent prompt.");
        context.Out.WriteLine(
            "Preauthorization does not grant the API's own downstream permissions.");
        return 0;
    }

    private sealed class PreauthorizedApplication
    {
        [System.Text.Json.Serialization.JsonPropertyName("appId")]
        public string AppId { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("delegatedPermissionIds")]
        public string[] DelegatedPermissionIds { get; set; } = [];
    }
}
