using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal static class ConsentBypassAppRegistrationCommands
{
    internal const string PowerPlatformApiAppId = "8578e004-a5c6-46e7-913e-12f58912df43";
    internal const string AdminScope = "CopilotStudio.AdminActions.Invoke";
    internal const string RedirectUri = "http://localhost";
    internal const string DefaultDisplayName = "foundry-copilot-a2a-consent-admin";

    public static async Task<int> RegisterAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("tenant-id", "display-name", "admin-consent", "help");
        var tenantId = arguments.Require("tenant-id");
        if (!Guid.TryParse(tenantId, out var expectedTenant))
        {
            throw new CliException("--tenant-id must be a GUID.");
        }

        var displayName = ValidateDisplayName(
            arguments.Optional("display-name", DefaultDisplayName)!);
        var attemptAdminConsent = arguments.Flag("admin-consent");
        var activeTenant = (await AzAsync(
            context,
            cancellationToken,
            "account", "show", "--query", "tenantId", "--output", "tsv"))
            .StandardOutput.Trim();
        if (!Guid.TryParse(activeTenant, out var actualTenant) ||
            actualTenant != expectedTenant)
        {
            throw new CliException(
                $"Azure CLI is signed in to tenant '{activeTenant}', not requested tenant '{tenantId}'.");
        }

        var scopesResult = await AzAsync(
            context,
            cancellationToken,
            "ad", "sp", "show",
            "--id", PowerPlatformApiAppId,
            "--query", "oauth2PermissionScopes",
            "--output", "json");
        using var scopes = JsonDocument.Parse(scopesResult.StandardOutput);
        var scopeId = ResolveAdminScopeId(scopes.RootElement);

        var existingResult = await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "list",
            "--display-name", displayName,
            "--output", "json");
        using var existing = JsonDocument.Parse(existingResult.StandardOutput);
        var matches = existing.RootElement
            .EnumerateArray()
            .Where(application =>
                string.Equals(
                    application.GetProperty("displayName").GetString(),
                    displayName,
                    StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new CliException(
                $"Found multiple applications named '{displayName}'; refusing an ambiguous deployment.");
        }

        var created = false;
        string clientId;
        if (matches.Length == 1)
        {
            clientId = matches[0].GetProperty("appId").GetString()
                ?? throw new CliException("The existing application has no client ID.");
            await VerifyRegistrationAsync(
                context, clientId, displayName, scopeId, cancellationToken);
            context.Out.WriteLine(
                $"Existing application '{displayName}' matches the required configuration.");
        }
        else
        {
            var requiredAccess = JsonSerializer.Serialize(new[]
            {
                new
                {
                    resourceAppId = PowerPlatformApiAppId,
                    resourceAccess = new[]
                    {
                        new { id = scopeId, type = "Scope" }
                    }
                }
            });
            using var requiredAccessFile = await TemporaryTextFile.CreateAsync(
                requiredAccess, cancellationToken);

            context.Out.WriteLine(
                $"Creating single-tenant public-client registration '{displayName}'...");
            clientId = (await AzAsync(
                context,
                cancellationToken,
                "ad", "app", "create",
                "--display-name", displayName,
                "--sign-in-audience", "AzureADMyOrg",
                "--is-fallback-public-client", "true",
                "--public-client-redirect-uris", RedirectUri,
                "--required-resource-accesses", requiredAccessFile.AzureCliReference,
                "--query", "appId",
                "--output", "tsv",
                "--only-show-errors"))
                .StandardOutput.Trim();
            if (!Guid.TryParse(clientId, out _))
            {
                throw new CliException(
                    "Azure CLI didn't return a client ID for the new administrator application.");
            }

            created = true;
            try
            {
                await RetryAsync(
                    () => AzAsync(
                        context,
                        cancellationToken,
                        "ad", "sp", "create",
                        "--id", clientId,
                        "--only-show-errors"),
                    cancellationToken);
                await VerifyRegistrationAsync(
                    context, clientId, displayName, scopeId, cancellationToken);
            }
            catch
            {
                context.Error.WriteLine(
                    $"Registration did not complete. Cleaning up application {clientId}.");
                try
                {
                    await AzAsync(
                        context,
                        cancellationToken,
                        "ad", "app", "delete",
                        "--id", clientId,
                        "--only-show-errors");
                }
                catch (Exception cleanupException)
                {
                    context.Error.WriteLine(
                        $"Automatic cleanup failed: {cleanupException.Message}");
                }
                throw;
            }
        }

        await EnsureServicePrincipalAsync(context, clientId, cancellationToken);
        if (attemptAdminConsent)
        {
            context.Out.WriteLine("Attempting tenant-wide admin consent...");
            try
            {
                await AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "permission", "admin-consent",
                    "--id", clientId,
                    "--only-show-errors");
            }
            catch (ExternalCommandException exception)
            {
                context.Error.WriteLine(
                    $"Admin consent wasn't granted: {exception.Message}");
                context.Error.WriteLine(
                    "The registration remains deployed. An authorized operator can consent " +
                    "during interactive sign-in or grant tenant-wide consent later.");
            }
        }

        context.Out.WriteLine();
        context.Out.WriteLine(
            created ? "Consent-bypass administrator application created."
                    : "Consent-bypass administrator application verified.");
        context.Out.WriteLine($"TenantId:             {tenantId}");
        context.Out.WriteLine($"Admin ClientId:       {clientId}");
        context.Out.WriteLine($"Redirect URI:         {RedirectUri}");
        context.Out.WriteLine(
            $"Delegated permission: https://api.powerplatform.com/{AdminScope}");
        context.Out.WriteLine("Client secret:         none (public client)");
        context.Out.WriteLine($"Cleanup:               delete-app --client-id {clientId}");
        return 0;
    }

    internal static Guid ResolveAdminScopeId(JsonElement scopes)
    {
        if (scopes.ValueKind != JsonValueKind.Array)
        {
            throw new CliException(
                "Power Platform API returned an invalid delegated permission inventory.");
        }

        var matches = scopes
            .EnumerateArray()
            .Where(scope =>
                scope.TryGetProperty("value", out var value) &&
                string.Equals(value.GetString(), AdminScope, StringComparison.Ordinal) &&
                scope.TryGetProperty("isEnabled", out var enabled) &&
                enabled.ValueKind == JsonValueKind.True)
            .Select(scope => scope.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => Guid.TryParse(id, out _))
            .Select(id => Guid.Parse(id!))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new CliException(
                $"Power Platform API didn't expose one enabled {AdminScope} delegated scope.");
    }

    internal static void ValidateRegistration(
        JsonElement application,
        string expectedDisplayName,
        Guid expectedScopeId)
    {
        var problems = new List<string>();
        if (!application.TryGetProperty("displayName", out var displayName) ||
            !string.Equals(
                displayName.GetString(), expectedDisplayName, StringComparison.Ordinal))
        {
            problems.Add("display name");
        }
        if (!application.TryGetProperty("signInAudience", out var audience) ||
            audience.GetString() != "AzureADMyOrg")
        {
            problems.Add("single-tenant audience");
        }
        if (!application.TryGetProperty("isFallbackPublicClient", out var publicClient) ||
            publicClient.ValueKind != JsonValueKind.True)
        {
            problems.Add("public-client flow");
        }

        var redirects = application.TryGetProperty("publicClient", out var platform) &&
                        platform.ValueKind == JsonValueKind.Object &&
                        platform.TryGetProperty("redirectUris", out var redirectValues) &&
                        redirectValues.ValueKind == JsonValueKind.Array
            ? redirectValues.EnumerateArray()
                .Select(value => value.GetString()?.TrimEnd('/'))
                .ToArray()
            : [];
        if (redirects.Length != 1 ||
            !string.Equals(
                redirects[0],
                RedirectUri.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"single native redirect {RedirectUri}");
        }

        if (!HasOnlyExpectedPermission(application, expectedScopeId))
        {
            problems.Add($"only delegated permission {AdminScope}");
        }
        if (HasValues(application, "passwordCredentials") ||
            HasValues(application, "keyCredentials"))
        {
            problems.Add("no client credentials");
        }
        if (HasPlatformRedirects(application, "web") ||
            HasPlatformRedirects(application, "spa"))
        {
            problems.Add("no Web or SPA redirects");
        }

        if (problems.Count > 0)
        {
            throw new CliException(
                $"An application named '{expectedDisplayName}' exists but doesn't match the " +
                $"required isolated admin-client configuration: {string.Join(", ", problems)}. " +
                "Refusing to overwrite it.");
        }
    }

    private static async Task VerifyRegistrationAsync(
        CliContext context,
        string clientId,
        string displayName,
        Guid scopeId,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                var result = await AzAsync(
                    context,
                    cancellationToken,
                    "ad", "app", "show",
                    "--id", clientId,
                    "--output", "json");
                using var application = JsonDocument.Parse(result.StandardOutput);
                ValidateRegistration(
                    application.RootElement, displayName, scopeId);
                return;
            }
            catch (Exception exception) when (
                exception is ExternalCommandException or CliException &&
                attempt < 6)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        throw new CliException(
            $"The administrator application couldn't be verified. {lastException?.Message}");
    }

    private static async Task EnsureServicePrincipalAsync(
        CliContext context,
        string clientId,
        CancellationToken cancellationToken)
    {
        var existing = (await AzAsync(
            context,
            cancellationToken,
            "ad", "sp", "list",
            "--filter", $"appId eq '{clientId}'",
            "--query", "[].id",
            "--output", "tsv"))
            .StandardOutput.Trim();
        if (!string.IsNullOrEmpty(existing))
        {
            return;
        }

        await RetryAsync(
            () => AzAsync(
                context,
                cancellationToken,
                "ad", "sp", "create",
                "--id", clientId,
                "--only-show-errors"),
            cancellationToken);
    }

    private static bool HasOnlyExpectedPermission(
        JsonElement application,
        Guid expectedScopeId)
    {
        if (!application.TryGetProperty(
                "requiredResourceAccess", out var requiredAccess) ||
            requiredAccess.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var resources = requiredAccess.EnumerateArray().ToArray();
        if (resources.Length != 1 ||
            resources[0].GetProperty("resourceAppId").GetString() != PowerPlatformApiAppId ||
            !resources[0].TryGetProperty("resourceAccess", out var access) ||
            access.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var permissions = access.EnumerateArray().ToArray();
        return permissions.Length == 1 &&
               permissions[0].GetProperty("id").GetGuid() == expectedScopeId &&
               permissions[0].GetProperty("type").GetString() == "Scope";
    }

    private static bool HasValues(JsonElement application, string propertyName) =>
        application.TryGetProperty(propertyName, out var values) &&
        values.ValueKind == JsonValueKind.Array &&
        values.GetArrayLength() > 0;

    private static bool HasPlatformRedirects(
        JsonElement application,
        string propertyName) =>
        application.TryGetProperty(propertyName, out var platform) &&
        platform.ValueKind == JsonValueKind.Object &&
        platform.TryGetProperty("redirectUris", out var redirects) &&
        redirects.ValueKind == JsonValueKind.Array &&
        redirects.GetArrayLength() > 0;

    private static string ValidateDisplayName(string value)
    {
        var displayName = value.Trim();
        if (displayName.Length is 0 or > 120 ||
            displayName.Any(char.IsControl))
        {
            throw new CliException(
                "--display-name must contain 1-120 characters without control characters.");
        }
        return displayName;
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

        throw new InvalidOperationException(
            "Retry operation failed without an exception.");
    }

    private static Task<CommandResult> AzAsync(
        CliContext context,
        CancellationToken cancellationToken,
        params string[] arguments) =>
        context.Processes.CaptureAsync("az", arguments, cancellationToken);
}
