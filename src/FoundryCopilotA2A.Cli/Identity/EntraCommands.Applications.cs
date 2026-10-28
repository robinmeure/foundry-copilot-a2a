using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal static partial class EntraCommands
{
    public static async Task<int> RegisterAppAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "display-name", "preauthorize-azure-cli", "admin-consent",
            "no-client-secret", "owner-object-id", "help");

        var displayName = arguments.Optional(
            "display-name", "foundry-copilot-a2a-api")!;
        var preauthorizeAzureCli = arguments.Flag("preauthorize-azure-cli");
        var attemptAdminConsent = arguments.Flag("admin-consent");
        var noClientSecret = arguments.Flag("no-client-secret");
        var ownerObjectId = OptionalGuid(arguments, "owner-object-id");

        var tenantId = await ResolveTenantIdAsync(context, cancellationToken);
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
            await AddOwnerAsync(context, appId, ownerObjectId, cancellationToken);
            var objectId = await ResolveApplicationObjectIdAsync(
                context, appId, cancellationToken);
            context.Out.WriteLine("Configuring API scope and public-client device flow...");
            var patchBody = BuildAdapterRegistrationPatch(appId, Guid.NewGuid(), preauthorizeAzureCli);
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

            await ConfigureCopilotStudioAccessAsync(context, appId, cancellationToken);

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

            string? secret = null;
            if (!noClientSecret)
            {
                context.Out.WriteLine("Creating one-year client secret...");
                secret = (await AzAsync(
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
            }

            context.Out.WriteLine();
            context.Out.WriteLine(noClientSecret
                ? "Application created without a client secret."
                : "Application created. The client secret is shown once.");
            context.Out.WriteLine($"TenantId:     {tenantId}");
            context.Out.WriteLine($"ClientId:     {appId}");
            context.Out.WriteLine($"Audience:     api://{appId}");
            context.Out.WriteLine(
                $"Authority:    https://login.microsoftonline.com/{tenantId}/v2.0");
            if (secret is not null)
            {
                context.Out.WriteLine($"ClientSecret: {secret}");
            }
            else
            {
                context.Out.WriteLine(
                    "Credential:   add a managed-identity federated credential before Azure use.");
            }
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

    internal static string BuildAdapterRegistrationPatch(
        string appId,
        Guid delegatedScopeId,
        bool preauthorizeAzureCli)
    {
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
                    adminConsentDisplayName = "Access the A2A adapter as the signed-in user",
                    adminConsentDescription =
                        "Allows the caller to invoke the A2A adapter on behalf of the signed-in user.",
                    userConsentDisplayName = "Access the A2A adapter on your behalf",
                    userConsentDescription = "Allows the caller to invoke the A2A adapter on your behalf."
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

        return JsonSerializer.Serialize(new Dictionary<string, object?>
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
    }

    private static async Task ConfigureCopilotStudioAccessAsync(
        CliContext context,
        string appId,
        CancellationToken cancellationToken)
    {
        context.Out.WriteLine("Adding CopilotStudio.Copilots.Invoke...");
        await RetryAsync(
            () => AzAsync(
                context, cancellationToken,
                "ad", "app", "permission", "add", "--id", appId,
                "--api", PowerPlatformApiAppId,
                "--api-permissions", $"{CopilotStudioInvokeScopeId}=Scope",
                "--only-show-errors"),
            cancellationToken);

        context.Out.WriteLine("Creating service principal...");
        await RetryAsync(
            () => AzAsync(
                context, cancellationToken,
                "ad", "sp", "create", "--id", appId, "--only-show-errors"),
            cancellationToken);
    }

    public static async Task<int> RegisterSpaAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "api-client-id", "display-name", "redirect-uri", "admin-consent",
            "owner-object-id", "help");

        var apiClientId = arguments.Require("api-client-id");
        var displayName = arguments.Optional(
            "display-name", "foundry-copilot-a2a-web")!;
        var redirectUri = arguments
            .AbsoluteHttpUri("redirect-uri", "http://localhost:5173")
            .AbsoluteUri.TrimEnd('/');
        var attemptAdminConsent = arguments.Flag("admin-consent");
        var ownerObjectId = OptionalGuid(arguments, "owner-object-id");

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
            await AddOwnerAsync(context, spaClientId, ownerObjectId, cancellationToken);
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

    public static async Task<int> RegisterOAuthClientAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "api-client-id", "display-name", "owner-object-id", "help");

        var apiClientId = RequireGuid(arguments, "api-client-id");
        var displayName = arguments.Require("display-name");
        var ownerObjectId = OptionalGuid(arguments, "owner-object-id");
        var tenantId = await ResolveTenantIdAsync(context, cancellationToken);
        var apiScopeId = await ResolveAccessAsUserScopeIdAsync(
            context, apiClientId, cancellationToken);

        var existing = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "list",
            "--display-name", displayName,
            "--query", "[].appId",
            "--output", "tsv"))
            .StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        if (existing.Length > 0)
        {
            throw new CliException(
                $"An application named '{displayName}' already exists " +
                $"(client id {string.Join(", ", existing)}).");
        }

        context.Out.WriteLine($"Creating OAuth client registration '{displayName}'...");
        var clientId = (await AzAsync(
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
            await AddOwnerAsync(context, clientId, ownerObjectId, cancellationToken);
            context.Out.WriteLine("Granting delegated access to the API...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "permission", "add",
                    "--id", clientId,
                    "--api", apiClientId,
                    "--api-permissions", $"{apiScopeId}=Scope",
                    "--only-show-errors"),
                cancellationToken);

            context.Out.WriteLine("Creating the OAuth client service principal...");
            await RetryAsync(
                () => AzAsync(
                    context,
                    cancellationToken,
                    "ad", "sp", "create",
                    "--id", clientId,
                    "--only-show-errors"),
                cancellationToken);

            context.Out.WriteLine();
            context.Out.WriteLine("OAuth client created without a credential or redirect URI.");
            context.Out.WriteLine($"TenantId:       {tenantId}");
            context.Out.WriteLine($"ClientId:       {clientId}");
            context.Out.WriteLine($"Delegated scope: api://{apiClientId}/access_as_user");
            context.Out.WriteLine(
                "Next: create the credential in an approved secret store, configure the " +
                "Copilot Studio connection, then register its generated callback.");
            context.Out.WriteLine($"Cleanup:        delete-app --client-id {clientId}");
            return 0;
        }
        catch
        {
            context.Error.WriteLine(
                $"OAuth client registration did not complete. Clean up with: " +
                $"delete-app --client-id {clientId}");
            throw;
        }
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
            "preauthorize-client-ids", "admin-consent", "owner-object-id", "help");

        var apiClientId = RequireGuid(arguments, "api-client-id");
        var displayName = arguments.Optional(
            "display-name", "foundry-copilot-a2a-hub")!;
        var ownerObjectId = OptionalGuid(arguments, "owner-object-id");
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
            await AddOwnerAsync(context, hubClientId, ownerObjectId, cancellationToken);
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
}
