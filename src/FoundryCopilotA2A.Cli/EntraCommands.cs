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
