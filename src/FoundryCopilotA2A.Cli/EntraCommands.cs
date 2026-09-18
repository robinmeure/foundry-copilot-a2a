using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal static class EntraCommands
{
    private const string PowerPlatformApiAppId = "8578e004-a5c6-46e7-913e-12f58912df43";
    private const string CopilotStudioInvokeScopeId = "204440d3-c1d0-4826-b570-99eb6f5e2aeb";
    private const string AzureCliAppId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private const string DefaultScope =
        "https://api.powerplatform.com/CopilotStudio.Copilots.Invoke offline_access";

    public static async Task<int> RegisterAppAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "display-name", "preauthorize-azure-cli", "admin-consent", "help");

        var displayName = arguments.Optional(
            "display-name", "foundry-copilot-a2a-api")!;
        var preauthorizeAzureCli = arguments.Flag("preauthorize-azure-cli");
        var attemptAdminConsent = arguments.Flag("admin-consent");

        var tenantId = (await AzAsync(
            context,
            cancellationToken,
            "account", "show", "--query", "tenantId", "--output", "tsv"))
            .StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new CliException("Run 'az login' before registering the application.");
        }

        context.Out.WriteLine($"Tenant: {tenantId}");
        var existing = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "list",
            "--display-name", displayName,
            "--query", "[0].appId",
            "--output", "tsv"))
            .StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            throw new CliException(
                $"An application named '{displayName}' already exists (client id {existing}).");
        }

        context.Out.WriteLine($"Creating app registration '{displayName}'...");
        var appId = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "create",
            "--display-name", displayName,
            "--sign-in-audience", "AzureADMyOrg",
            "--query", "appId",
            "--output", "tsv"))
            .StandardOutput.Trim();

        try
        {
            var objectId = await ResolveApplicationObjectIdAsync(
                context, appId, cancellationToken);
            var delegatedScopeId = Guid.NewGuid();

            context.Out.WriteLine("Configuring API scope and public-client device flow...");
            var api = new Dictionary<string, object?>
            {
                ["requestedAccessTokenVersion"] = 2,
                ["oauth2PermissionScopes"] = new[]
                {
                    new
                    {
                        id = delegatedScopeId,
                        value = "access_as_user",
                        type = "User",
                        isEnabled = true,
                        adminConsentDisplayName =
                            "Access the A2A adapter as the signed-in user",
                        adminConsentDescription =
                            "Allows the caller to invoke the A2A adapter on behalf of the signed-in user.",
                        userConsentDisplayName =
                            "Access the A2A adapter on your behalf",
                        userConsentDescription =
                            "Allows the caller to invoke the A2A adapter on your behalf."
                    }
                }
            };

            if (preauthorizeAzureCli)
            {
                api["preAuthorizedApplications"] = new[]
                {
                    new
                    {
                        appId = AzureCliAppId,
                        delegatedPermissionIds = new[] { delegatedScopeId }
                    }
                };
            }

            var patchBody = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["identifierUris"] = new[] { $"api://{appId}" },
                ["isFallbackPublicClient"] = true,
                ["publicClient"] = new
                {
                    redirectUris = new[]
                    {
                        "http://localhost",
                        "https://login.microsoftonline.com/common/oauth2/nativeclient"
                    }
                },
                ["api"] = api
            });

            using var patchFile = await TemporaryTextFile.CreateAsync(
                patchBody, cancellationToken);
            await AzAsync(
                context,
                cancellationToken,
                "rest",
                "--method", "PATCH",
                "--url", $"https://graph.microsoft.com/v1.0/applications/{objectId}",
                "--headers", "Content-Type=application/json",
                "--body", patchFile.AzureCliReference,
                "--output", "none");

            context.Out.WriteLine("Adding CopilotStudio.Copilots.Invoke...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "permission", "add",
                    "--id", appId,
                    "--api", PowerPlatformApiAppId,
                    "--api-permissions", $"{CopilotStudioInvokeScopeId}=Scope",
                    "--only-show-errors"),
                cancellationToken);

            context.Out.WriteLine("Creating service principal...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "sp", "create",
                    "--id", appId,
                    "--only-show-errors"),
                cancellationToken);

            if (attemptAdminConsent)
            {
                context.Out.WriteLine("Attempting tenant-wide admin consent...");
                try
                {
                    await AzAsync(
                        context,
                        cancellationToken,
                        "ad", "app", "permission", "admin-consent",
                        "--id", appId,
                        "--only-show-errors");
                }
                catch (ExternalCommandException exception)
                {
                    context.Error.WriteLine(
                        $"Admin consent was not granted: {exception.Message}");
                    context.Error.WriteLine(
                        "The app remains usable with the per-user 'consent' command.");
                }
            }

            context.Out.WriteLine("Creating one-year client secret...");
            var secret = (await AzAsync(
                context,
                cancellationToken,
                "ad", "app", "credential", "reset",
                "--id", appId,
                "--append",
                "--display-name", "adapter-obo",
                "--years", "1",
                "--query", "password",
                "--output", "tsv",
                "--only-show-errors"))
                .StandardOutput.Trim();

            context.Out.WriteLine();
            context.Out.WriteLine("Application created. The client secret is shown once.");
            context.Out.WriteLine($"TenantId:     {tenantId}");
            context.Out.WriteLine($"ClientId:     {appId}");
            context.Out.WriteLine($"Audience:     api://{appId}");
            context.Out.WriteLine(
                $"Authority:    https://login.microsoftonline.com/{tenantId}/v2.0");
            context.Out.WriteLine($"ClientSecret: {secret}");
            context.Out.WriteLine(
                $"Cleanup:      delete-app --client-id {appId}");
            return 0;
        }
        catch
        {
            context.Error.WriteLine(
                $"Registration did not complete. Clean up with: delete-app --client-id {appId}");
            throw;
        }
    }

    public static async Task<int> RegisterSpaAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "api-client-id", "display-name", "redirect-uri", "admin-consent", "help");

        var apiClientId = arguments.Require("api-client-id");
        var displayName = arguments.Optional(
            "display-name", "foundry-copilot-a2a-web")!;
        var redirectUri = arguments
            .AbsoluteHttpUri("redirect-uri", "http://localhost:5173")
            .AbsoluteUri.TrimEnd('/');
        var attemptAdminConsent = arguments.Flag("admin-consent");

        var tenantId = (await AzAsync(
            context,
            cancellationToken,
            "account", "show", "--query", "tenantId", "--output", "tsv"))
            .StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new CliException("Run 'az login' before registering the application.");
        }

        var apiScopeId = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "show",
            "--id", apiClientId,
            "--query", "api.oauth2PermissionScopes[?value=='access_as_user' && isEnabled].id | [0]",
            "--output", "tsv"))
            .StandardOutput.Trim();
        if (!Guid.TryParse(apiScopeId, out _))
        {
            throw new CliException(
                $"Backend application {apiClientId} does not expose an enabled access_as_user scope.");
        }

        var existing = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "list",
            "--display-name", displayName,
            "--query", "[0].appId",
            "--output", "tsv"))
            .StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            throw new CliException(
                $"An application named '{displayName}' already exists (client id {existing}).");
        }

        context.Out.WriteLine($"Creating SPA registration '{displayName}'...");
        var spaClientId = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "create",
            "--display-name", displayName,
            "--sign-in-audience", "AzureADMyOrg",
            "--query", "appId",
            "--output", "tsv"))
            .StandardOutput.Trim();

        try
        {
            var objectId = await ResolveApplicationObjectIdAsync(
                context, spaClientId, cancellationToken);
            var patchBody = JsonSerializer.Serialize(new
            {
                spa = new
                {
                    redirectUris = new[] { redirectUri }
                }
            });

            using var patchFile = await TemporaryTextFile.CreateAsync(
                patchBody, cancellationToken);
            await AzAsync(
                context,
                cancellationToken,
                "rest",
                "--method", "PATCH",
                "--url", $"https://graph.microsoft.com/v1.0/applications/{objectId}",
                "--headers", "Content-Type=application/json",
                "--body", patchFile.AzureCliReference,
                "--output", "none");

            context.Out.WriteLine("Granting delegated access to the backend API...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "permission", "add",
                    "--id", spaClientId,
                    "--api", apiClientId,
                    "--api-permissions", $"{apiScopeId}=Scope",
                    "--only-show-errors"),
                cancellationToken);

            context.Out.WriteLine("Creating frontend service principal...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "sp", "create",
                    "--id", spaClientId,
                    "--only-show-errors"),
                cancellationToken);

            if (attemptAdminConsent)
            {
                context.Out.WriteLine("Attempting tenant-wide admin consent...");
                await AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "permission", "admin-consent",
                    "--id", spaClientId,
                    "--only-show-errors");
            }

            context.Out.WriteLine();
            context.Out.WriteLine("SPA application created without a client secret.");
            context.Out.WriteLine($"TenantId:           {tenantId}");
            context.Out.WriteLine($"SPA ClientId:       {spaClientId}");
            context.Out.WriteLine($"Backend API ClientId: {apiClientId}");
            context.Out.WriteLine($"Delegated scope:    api://{apiClientId}/access_as_user");
            context.Out.WriteLine($"Redirect URI:       {redirectUri}");
            context.Out.WriteLine(
                $"Cleanup:            delete-app --client-id {spaClientId}");
            return 0;
        }
        catch
        {
            context.Error.WriteLine(
                $"SPA registration did not complete. Clean up with: delete-app --client-id {spaClientId}");
            throw;
        }
    }

    public static async Task<int> DeleteAppAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("client-id", "help");
        var clientId = arguments.Require("client-id");

        await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "delete", "--id", clientId, "--only-show-errors");

        var remaining = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "list",
            "--filter", $"appId eq '{clientId}'",
            "--query", "[].appId",
            "--output", "tsv"))
            .StandardOutput.Trim();
        if (!string.IsNullOrEmpty(remaining))
        {
            throw new CliException($"Application {clientId} is still present after deletion.");
        }

        context.Out.WriteLine($"Deleted application {clientId} and its service principal.");
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
    /// Creates the API Hub registration used by the gateway to exchange a hub-audience caller
    /// token for an adapter-audience token. The hub is a separate confidential application, so
    /// browser tokens are never valid against the adapter directly.
    /// </summary>
    public static async Task<int> RegisterHubAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "api-client-id", "display-name", "managed-identity-principal-id",
            "preauthorize-client-ids", "admin-consent", "help");

        var apiClientId = RequireGuid(arguments, "api-client-id");
        var displayName = arguments.Optional(
            "display-name", "foundry-copilot-a2a-hub")!;
        var managedIdentityPrincipalId = arguments.Optional("managed-identity-principal-id");
        if (managedIdentityPrincipalId is not null &&
            !Guid.TryParse(managedIdentityPrincipalId, out _))
        {
            throw new CliException(
                "Option '--managed-identity-principal-id' must be the managed identity's object (principal) ID.");
        }

        var preauthorizedClientIds = ParseGuidList(
            arguments.Optional("preauthorize-client-ids"), "preauthorize-client-ids");
        var attemptAdminConsent = arguments.Flag("admin-consent");
        var adminConsentGranted = attemptAdminConsent;

        var tenantId = await ResolveTenantIdAsync(context, cancellationToken);
        context.Out.WriteLine($"Tenant: {tenantId}");

        // The hub can only exchange for a scope the adapter actually exposes.
        var adapterScopeId = await ResolveAccessAsUserScopeIdAsync(
            context, apiClientId, cancellationToken);

        var existing = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "list",
            "--display-name", displayName,
            "--query", "[0].appId",
            "--output", "tsv"))
            .StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            throw new CliException(
                $"An application named '{displayName}' already exists (client id {existing}).");
        }

        context.Out.WriteLine($"Creating API Hub registration '{displayName}'...");
        var hubClientId = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "create",
            "--display-name", displayName,
            "--sign-in-audience", "AzureADMyOrg",
            "--query", "appId",
            "--output", "tsv"))
            .StandardOutput.Trim();

        try
        {
            var objectId = await ResolveApplicationObjectIdAsync(
                context, hubClientId, cancellationToken);
            var hubScopeId = Guid.NewGuid();

            context.Out.WriteLine("Exposing the hub's access_as_user scope...");
            var api = new Dictionary<string, object?>
            {
                ["requestedAccessTokenVersion"] = 2,
                ["oauth2PermissionScopes"] = new[]
                {
                    new
                    {
                        id = hubScopeId,
                        value = "access_as_user",
                        type = "User",
                        isEnabled = true,
                        adminConsentDisplayName =
                            "Access the A2A API Hub as the signed-in user",
                        adminConsentDescription =
                            "Allows the caller to invoke the A2A API Hub on behalf of the signed-in user.",
                        userConsentDisplayName =
                            "Access the A2A API Hub on your behalf",
                        userConsentDescription =
                            "Allows the caller to invoke the A2A API Hub on your behalf."
                    }
                }
            };

            var patchBody = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["identifierUris"] = new[] { $"api://{hubClientId}" },
                ["api"] = api
            });

            using (var patchFile = await TemporaryTextFile.CreateAsync(
                patchBody, cancellationToken))
            {
                await AzAsync(
                    context,
                    cancellationToken,
                    "rest",
                    "--method", "PATCH",
                    "--url", $"https://graph.microsoft.com/v1.0/applications/{objectId}",
                    "--headers", "Content-Type=application/json",
                    "--body", patchFile.AzureCliReference,
                    "--output", "none");
            }

            // Microsoft Graph rejects preAuthorizedApplications that reference a scope created in
            // the same request, so the scope must already exist before clients are preauthorized.
            if (preauthorizedClientIds.Count > 0)
            {
                context.Out.WriteLine("Preauthorizing the configured client applications...");
                var preauthorizeBody = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["api"] = new Dictionary<string, object?>
                    {
                        ["preAuthorizedApplications"] = preauthorizedClientIds
                            .Select(clientId => new
                            {
                                appId = clientId,
                                delegatedPermissionIds = new[] { hubScopeId }
                            })
                            .ToArray()
                    }
                });

                using var preauthorizeFile = await TemporaryTextFile.CreateAsync(
                    preauthorizeBody, cancellationToken);
                await RetryAsync(
                    () => AzAsync(
                        context,
                        cancellationToken,
                        "rest",
                        "--method", "PATCH",
                        "--url", $"https://graph.microsoft.com/v1.0/applications/{objectId}",
                        "--headers", "Content-Type=application/json",
                        "--body", preauthorizeFile.AzureCliReference,
                        "--output", "none"),
                    cancellationToken);
            }

            context.Out.WriteLine("Granting the hub delegated access to the adapter API...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "permission", "add",
                    "--id", hubClientId,
                    "--api", apiClientId,
                    "--api-permissions", $"{adapterScopeId}=Scope",
                    "--only-show-errors"),
                cancellationToken);

            context.Out.WriteLine("Creating the hub service principal...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "sp", "create",
                    "--id", hubClientId,
                    "--only-show-errors"),
                cancellationToken);

            if (managedIdentityPrincipalId is not null)
            {
                context.Out.WriteLine(
                    "Adding the federated identity credential for the gateway managed identity...");
                var credentialBody = JsonSerializer.Serialize(new
                {
                    name = "apim-managed-identity",
                    issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
                    subject = managedIdentityPrincipalId,
                    audiences = new[] { "api://AzureADTokenExchange" },
                    description =
                        "Lets API Management authenticate as this hub without a client secret."
                });
                using var credentialFile = await TemporaryTextFile.CreateAsync(
                    credentialBody, cancellationToken);
                await RetryAsync(
                    () => AzAsync(
                        context,
                        cancellationToken,
                        "ad", "app", "federated-credential", "create",
                        "--id", hubClientId,
                        "--parameters", credentialFile.AzureCliReference,
                        "--only-show-errors"),
                    cancellationToken);
            }

            if (attemptAdminConsent)
            {
                context.Out.WriteLine("Attempting tenant-wide admin consent...");
                try
                {
                    await AzAsync(
                        context,
                        cancellationToken,
                        "ad", "app", "permission", "admin-consent",
                        "--id", hubClientId,
                        "--only-show-errors");
                }
                catch (ExternalCommandException)
                {
                    // The registration itself is complete and usable. Consent is a separate
                    // privileged operation, so a failure here must not imply a broken app.
                    adminConsentGranted = false;
                    context.Error.WriteLine(
                        "Tenant-wide admin consent was not granted. The registration is still " +
                        "valid. Grant consent as a Privileged Role Administrator, or preauthorize " +
                        "this hub on the adapter API with 'preauthorize-client'.");
                }
            }

            context.Out.WriteLine();
            context.Out.WriteLine("API Hub registration created without a client secret.");
            context.Out.WriteLine($"TenantId:          {tenantId}");
            context.Out.WriteLine($"Hub ClientId:      {hubClientId}");
            context.Out.WriteLine($"Hub audience:      api://{hubClientId}");
            context.Out.WriteLine($"Hub scope:         api://{hubClientId}/access_as_user");
            context.Out.WriteLine($"Adapter ClientId:  {apiClientId}");
            if (managedIdentityPrincipalId is null)
            {
                context.Out.WriteLine(
                    "Gateway credential: none configured. Add a federated identity credential " +
                    "before the gateway can exchange tokens.");
            }

            if (!adminConsentGranted)
            {
                context.Out.WriteLine(
                    "Consent:           the hub -> adapter grant still requires user or admin consent, " +
                    "or preauthorization on the adapter API.");
            }

            context.Out.WriteLine(
                $"Cleanup:           delete-app --client-id {hubClientId}");
            return 0;
        }
        catch
        {
            context.Error.WriteLine(
                $"Hub registration did not complete. Clean up with: delete-app --client-id {hubClientId}");
            throw;
        }
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

    private static async Task<string> ResolveTenantIdAsync(
        CliContext context,
        CancellationToken cancellationToken)
    {
        var tenantId = (await AzAsync(
            context,
            cancellationToken,
            "account", "show", "--query", "tenantId", "--output", "tsv"))
            .StandardOutput.Trim();
        return !string.IsNullOrWhiteSpace(tenantId)
            ? tenantId
            : throw new CliException("Run 'az login' before registering the application.");
    }

    private static async Task<string> ResolveAccessAsUserScopeIdAsync(
        CliContext context,
        string clientId,
        CancellationToken cancellationToken)
    {
        var scopeId = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "show",
            "--id", clientId,
            "--query", "api.oauth2PermissionScopes[?value=='access_as_user' && isEnabled].id | [0]",
            "--output", "tsv"))
            .StandardOutput.Trim();
        return Guid.TryParse(scopeId, out _)
            ? scopeId
            : throw new CliException(
                $"Application {clientId} does not expose an enabled access_as_user scope.");
    }

    private static string RequireGuid(CommandArguments arguments, string name)
    {
        var value = arguments.Require(name);
        return Guid.TryParse(value, out _)
            ? value
            : throw new CliException($"Option '--{name}' must be an application (client) ID.");
    }

    private static IReadOnlyList<string> ParseGuidList(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var items = value.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in items)
        {
            if (!Guid.TryParse(item, out _))
            {
                throw new CliException(
                    $"Option '--{name}' must be a comma-separated list of application (client) IDs.");
            }
        }

        return items;
    }

    private static async Task<string> ResolveApplicationObjectIdAsync(
        CliContext context,
        string appId,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                var objectId = (await AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "show",
                    "--id", appId,
                    "--query", "id",
                    "--output", "tsv"))
                    .StandardOutput.Trim();
                if (!string.IsNullOrWhiteSpace(objectId))
                {
                    return objectId;
                }
            }
            catch (ExternalCommandException exception)
            {
                lastException = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new CliException(
            $"The new application was not visible through Microsoft Graph. {lastException?.Message}");
    }

    private static async Task RetryAsync(
        Func<Task<CommandResult>> action,
        CancellationToken cancellationToken)
    {
        ExternalCommandException? lastException = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (ExternalCommandException exception) when (attempt < 6)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }

        throw new InvalidOperationException("Retry operation failed without an exception.");
    }

    private static Task<CommandResult> AzAsync(
        CliContext context,
        CancellationToken cancellationToken,
        params string[] arguments) =>
        context.Processes.CaptureAsync("az", arguments, cancellationToken);
}
