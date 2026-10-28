using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal static partial class EntraCommands
{
    private const string PowerPlatformApiAppId = "8578e004-a5c6-46e7-913e-12f58912df43";
    private const string CopilotStudioInvokeScopeId = "204440d3-c1d0-4826-b570-99eb6f5e2aeb";
    private const string AzureCliAppId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private const string DefaultScope =
        "https://api.powerplatform.com/CopilotStudio.Copilots.Invoke offline_access";


    public static async Task<int> RegisterWebRedirectAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly("client-id", "redirect-uri", "help");

        var clientId = RequireGuid(arguments, "client-id");
        var redirectUri = arguments.AbsoluteHttpUri("redirect-uri");
        if (redirectUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(redirectUri.UserInfo) ||
            !string.IsNullOrEmpty(redirectUri.Query) ||
            !string.IsNullOrEmpty(redirectUri.Fragment))
        {
            throw new CliException(
                "Option '--redirect-uri' must be an HTTPS URL without credentials, a query, or a fragment.");
        }

        var objectId = await ResolveApplicationObjectIdAsync(
            context, clientId, cancellationToken);
        var redirectsJson = (await AzAsync(
            context,
            cancellationToken,
            "ad", "app", "show",
            "--id", clientId,
            "--query", "web.redirectUris",
            "--output", "json"))
            .StandardOutput;
        using var redirectsDocument = JsonDocument.Parse(redirectsJson);
        var redirects = redirectsDocument.RootElement.ValueKind == JsonValueKind.Array
            ? redirectsDocument.RootElement
                .EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList()
            : [];
        var normalized = redirectUri.AbsoluteUri;
        if (redirects.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            context.Out.WriteLine(
                $"Web redirect URI '{normalized}' is already registered.");
            return 0;
        }

        redirects.Add(normalized);
        var patchBody = JsonSerializer.Serialize(new
        {
            web = new
            {
                redirectUris = redirects
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

        context.Out.WriteLine(
            $"Registered Web redirect URI '{normalized}' on application {clientId}.");
        return 0;
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

    private static string? OptionalGuid(CommandArguments arguments, string name)
    {
        var value = arguments.Optional(name);
        if (value is null)
        {
            return null;
        }

        return Guid.TryParse(value, out _)
            ? value
            : throw new CliException($"Option '--{name}' must be an object ID.");
    }

    private static async Task AddOwnerAsync(
        CliContext context,
        string clientId,
        string? ownerObjectId,
        CancellationToken cancellationToken)
    {
        if (ownerObjectId is null)
        {
            return;
        }

        context.Out.WriteLine($"Assigning application owner {ownerObjectId}...");
        await RetryAsync(
            () => AzAsync(
                context,
                cancellationToken,
                "ad", "app", "owner", "add",
                "--id", clientId,
                "--owner-object-id", ownerObjectId,
                "--only-show-errors"),
            cancellationToken);
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
