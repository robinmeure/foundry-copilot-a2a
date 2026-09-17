using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal sealed record CopilotStudioConsentBypassTarget(
    Guid TenantId,
    Guid AdminClientId,
    Guid EnvironmentId,
    Guid BotId);

internal static class CopilotStudioConsentBypassCommands
{
    private const string AdminScope =
        "https://api.powerplatform.com/copilotstudio.adminactions.invoke";
    private const string ApiVersion = "2022-03-01-preview";

    public static async Task<int> GetAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "tenant-id", "admin-client-id", "environment-id", "bot-id", "help");
        var target = ParseTarget(arguments);
        var token = await AcquireAdminTokenAsync(context, target, cancellationToken);
        var enabled = await ReadSettingAsync(
            context.HttpClient,
            token,
            target.EnvironmentId,
            target.BotId,
            cancellationToken);

        WriteStatus(context.Out, target, enabled);
        return 0;
    }

    public static async Task<int> SetAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "tenant-id", "admin-client-id", "environment-id", "bot-id", "enabled", "help");
        if (!arguments.Has("enabled"))
        {
            throw new CliException("Option '--enabled' is required and expects true or false.");
        }

        var target = ParseTarget(arguments);
        var requested = arguments.Flag("enabled");
        var token = await AcquireAdminTokenAsync(context, target, cancellationToken);
        var enabled = await WriteSettingAsync(
            context.HttpClient,
            token,
            target.EnvironmentId,
            target.BotId,
            requested,
            cancellationToken);

        WriteStatus(context.Out, target, enabled);
        return 0;
    }

    internal static CopilotStudioConsentBypassTarget ParseTarget(CommandArguments arguments) =>
        new(
            RequireGuid(arguments, "tenant-id"),
            RequireGuid(arguments, "admin-client-id"),
            RequireGuid(arguments, "environment-id"),
            RequireGuid(arguments, "bot-id"));

    internal static async Task<bool> ReadSettingAsync(
        HttpClient client,
        string accessToken,
        Guid environmentId,
        Guid botId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get, accessToken, environmentId, botId);
        using var response = await client.SendAsync(request, cancellationToken);
        return await ReadResponseAsync(response, expected: null, cancellationToken);
    }

    internal static async Task<bool> WriteSettingAsync(
        HttpClient client,
        string accessToken,
        Guid environmentId,
        Guid botId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Put, accessToken, environmentId, botId);
        request.Content = JsonContent.Create(new { adminConsentBypass = enabled });
        using var response = await client.SendAsync(request, cancellationToken);
        return await ReadResponseAsync(response, enabled, cancellationToken);
    }

    private static async Task<string> AcquireAdminTokenAsync(
        CliContext context,
        CopilotStudioConsentBypassTarget target,
        CancellationToken cancellationToken)
    {
        var result = await InteractiveAuth.AcquireAsync(
            context,
            target.TenantId.ToString(),
            target.AdminClientId.ToString(),
            [AdminScope],
            cancellationToken);
        return result.AccessToken;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string accessToken,
        Guid environmentId,
        Guid botId)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new CliException("The administrator access token is empty.");
        }

        var request = new HttpRequestMessage(
            method,
            "https://api.powerplatform.com/copilotstudio/environments/" +
            $"{environmentId:D}/bots/{botId:D}/api/connectorConsentBypass" +
            $"?api-version={ApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<bool> ReadResponseAsync(
        HttpResponseMessage response,
        bool? expected,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = string.IsNullOrWhiteSpace(body)
                ? response.ReasonPhrase ?? "No response body was returned."
                : body.Length <= 2000 ? body : $"{body[..2000]}...";
            var guidance = response.StatusCode switch
            {
                HttpStatusCode.Forbidden =>
                    " The signed-in account must be a Power Platform Administrator, " +
                    "AI Administrator, or Global Administrator, and the admin client must have " +
                    "the delegated CopilotStudio.AdminActions.Invoke permission.",
                HttpStatusCode.NotFound =>
                    " Verify that the Dataverse bot GUID belongs to the specified environment.",
                HttpStatusCode.MethodNotAllowed =>
                    " This environment or realm doesn't support changing the bypass setting.",
                _ => string.Empty
            };
            throw new CliException(
                $"Power Platform returned HTTP {(int)response.StatusCode} while reading or " +
                $"changing connector consent bypass: {detail}{guidance}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("adminConsentBypass", out var value) ||
            value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new CliException(
                "Power Platform returned an invalid connector consent-bypass response.");
        }

        var enabled = value.GetBoolean();
        if (expected is not null && enabled != expected.Value)
        {
            throw new CliException(
                $"Power Platform returned adminConsentBypass={enabled.ToString().ToLowerInvariant()} " +
                $"after {expected.Value.ToString().ToLowerInvariant()} was requested.");
        }

        return enabled;
    }

    private static Guid RequireGuid(CommandArguments arguments, string name)
    {
        var value = arguments.Require(name);
        return Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new CliException($"Option '--{name}' must be a GUID.");
    }

    private static void WriteStatus(
        TextWriter output,
        CopilotStudioConsentBypassTarget target,
        bool enabled)
    {
        output.WriteLine(
            $"Connector consent-card bypass for bot {target.BotId:D} in environment " +
            $"{target.EnvironmentId:D}: {(enabled ? "enabled" : "disabled")}.");
    }
}
