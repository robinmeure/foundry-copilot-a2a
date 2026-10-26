using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Agents.CopilotStudio.Client.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Cli;

internal sealed class CopilotProbeSession(
    CopilotClient client,
    ServiceProvider provider,
    CopilotProbeSession.ConversationHeaderHandler headers) : IDisposable
{
    internal CopilotClient Client { get; } = client;

    internal static async Task<CopilotProbeSession> ConnectAsync(
        CliContext context, CommandArguments arguments, int seconds, CancellationToken cancellationToken)
    {
        var settings = CreateSettings(RequireEnvironment(arguments.Require("direct-connect-url-env")));
        var obo = arguments.Flag("obo");
        if (!obo && new[] { "tenant-id-env", "client-id-env", "client-secret-env" }.Any(arguments.Has))
        {
            throw new CliException("Backend credential options require --obo.");
        }

        var tokenVariable = arguments.Optional("bearer-token-env", "COPILOT_STUDIO_BEARER_TOKEN")!;
        var token = Environment.GetEnvironmentVariable(tokenVariable);
        if (obo)
        {
            var tenant = RequireEnvironment(arguments.Optional("tenant-id-env", "COPILOT_STUDIO_TENANT_ID")!);
            var clientId = RequireEnvironment(arguments.Optional("client-id-env", "COPILOT_STUDIO_CLIENT_ID")!);
            var secret = RequireEnvironment(arguments.Optional("client-secret-env", "COPILOT_STUDIO_CLIENT_SECRET")!);
            if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(clientId, out _))
            {
                throw new CliException("The tenant and client ID variables must contain GUIDs.");
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                token = (await DeviceCodeAuth.AcquireAsync(
                    context, tenant, clientId, [$"api://{clientId}/access_as_user"],
                    cancellationToken)).AccessToken;
            }
            RequireDelegatedToken(token);
            var application = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
                .WithClientSecret(secret)
                .Build();
            token = (await application.AcquireTokenOnBehalfOf(
                [CopilotClient.ScopeFromSettings(settings)], new UserAssertion(token))
                .ExecuteAsync(cancellationToken)).AccessToken;
        }
        else if (string.IsNullOrWhiteSpace(token))
        {
            throw new CliException($"Environment variable '{tokenVariable}' must contain " +
                "a delegated Copilot Studio token, or use --obo with existing backend credentials.");
        }
        RequireDelegatedToken(token);

        var headers = new ConversationHeaderHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("copilot-probe", http => http.Timeout = TimeSpan.FromSeconds(seconds))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler(() => headers);
        var provider = services.BuildServiceProvider();
        var client = new CopilotClient(
            settings, provider.GetRequiredService<IHttpClientFactory>(),
            _ => Task.FromResult(token), NullLogger.Instance, "copilot-probe");
        return new CopilotProbeSession(client, provider, headers);
    }

    internal async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        headers.ConversationId = null;
        string? conversationId = null;
        await foreach (var activity in Client.StartConversationAsync(
            emitStartConversationEvent: true, cancellationToken: cancellationToken))
        {
            CheckActivity(activity);
            conversationId ??= activity.Conversation?.Id;
        }
        return conversationId ?? headers.ConversationId ??
            throw new CliException("No conversation ID returned; probe is inconclusive.");
    }

    internal static ConnectionSettings CreateSettings(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.Host.EndsWith(".api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                "The connection variable must contain an HTTPS public-cloud Power Platform " +
                "direct-connect URL without credentials or a fragment.");
        }
        return new ConnectionSettings { DirectConnectUrl = uri.AbsoluteUri, Cloud = PowerPlatformCloud.Prod };
    }

    internal static void CheckActivity(IActivity activity)
    {
        if (activity.Attachments?.Any(attachment => attachment.ContentType == OAuthCard.ContentType) == true)
        {
            throw new CliException(
                "Agent returned an OAuth sign-in card; complete existing agent sign-in separately. " +
                "The probe is inconclusive; no token exchange or settings change was attempted.");
        }
        if (activity.Type == ActivityTypes.Event &&
            activity.Name?.Contains("error", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new CliException("Agent returned an error event; the probe is inconclusive.");
        }
    }

    internal static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) ? value :
            throw new CliException($"Environment variable '{name}' is required.");
    }

    internal static void RequireDelegatedToken(string token)
    {
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3)
            {
                throw new CliException("A delegated JWT user token is required; app-only auth is not allowed.");
            }
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("scp", out var scope) ||
                scope.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(scope.GetString()) ||
                (root.TryGetProperty("idtyp", out var idtyp) &&
                 idtyp.ValueKind == JsonValueKind.String && idtyp.GetString() == "app"))
            {
                throw new CliException("A delegated user token is required; app-only auth is not allowed.");
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new CliException("The token variable does not contain a readable delegated JWT.");
        }
    }

    public void Dispose() => provider.Dispose();

    internal sealed class ConversationHeaderHandler : DelegatingHandler
    {
        internal string? ConversationId { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var ready = false;
            try
            {
                // SDK 1.8.77 can deserialize JSON synchronously after ResponseHeadersRead.
                // Buffer under a byte limit and cancellation before giving it the response.
                using var original = response.Content;
                var bytes = await ProbeResponse.ReadBoundedBytesAsync(original, cancellationToken);
                var buffered = new ByteArrayContent(bytes);
                foreach (var header in original.Headers)
                {
                    buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                response.Content = buffered;
                if (response.Headers.TryGetValues("x-ms-conversationid", out var values))
                {
                    ConversationId = values.FirstOrDefault();
                }
                ready = true;
                return response;
            }
            finally
            {
                if (!ready)
                {
                    response.Dispose();
                }
            }
        }
    }
}

internal static class ProbeResponse
{
    internal const int MaxBytes = 2 * 1024 * 1024;

    internal static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxBytes)
        {
            throw new CliException("Response exceeded the probe's 2 MiB bound.");
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length + count > MaxBytes)
            {
                throw new CliException("Response exceeded the probe's 2 MiB bound.");
            }
            bytes.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return bytes.ToArray();
    }
}
