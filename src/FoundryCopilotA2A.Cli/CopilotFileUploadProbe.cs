using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Agents.CopilotStudio.Client.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Cli;

internal sealed record FileProbeFixture(string Project, string Destination, string Approval)
{
    internal const string Cargo = "brass telescope";
    internal const string SummaryQuestion =
        "Summarize the shipment described in the attached TXT file in one sentence, including " +
        "the cargo, project codename, and destination code exactly. Do not guess missing facts.";
    internal const string FollowUpQuestion =
        "What is the approval code in the file I uploaded earlier? Give the exact code. " +
        "Do not guess if you cannot read the file.";

    internal static FileProbeFixture Create() => new(
        $"cedar-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}",
        $"harbor-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}",
        $"permit-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");

    internal Activity CreateActivity()
    {
        var content = Encoding.UTF8.GetBytes(
            $"Fictional shipment record.\nCargo: {Cargo}.\n" +
            $"Project codename: {Project}\nDestination code: {Destination}\n" +
            $"Approval code: {Approval}\n");
        return new Activity
        {
            Type = ActivityTypes.Message,
            Text = SummaryQuestion,
            Attachments =
            [
                new Attachment
                {
                    Name = "shipment.txt",
                    ContentType = "text/plain",
                    ContentUrl = $"data:text/plain;base64,{Convert.ToBase64String(content)}"
                }
            ]
        };
    }
}

internal static class CopilotFileUploadProbe
{
    internal static async Task<int> RunAsync(
        CliContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "direct-connect-url-env", "bearer-token-env", "obo", "tenant-id-env",
            "client-id-env", "client-secret-env", "timeout-seconds", "help");
        var seconds = arguments.Integer("timeout-seconds", 300, minimum: 10);
        if (seconds > 600)
        {
            throw new CliException("Option '--timeout-seconds' cannot exceed 600.");
        }

        var url = RequireEnvironment(arguments.Require("direct-connect-url-env"));
        var settings = CreateSettings(url);
        var obo = arguments.Flag("obo");
        if (!obo && new[] { "tenant-id-env", "client-id-env", "client-secret-env" }.Any(arguments.Has))
        {
            throw new CliException("Backend credential options require --obo.");
        }

        var tokenVariable = arguments.Optional(
            "bearer-token-env", "COPILOT_STUDIO_BEARER_TOKEN")!;
        var token = Environment.GetEnvironmentVariable(tokenVariable);
        try
        {
            if (obo)
            {
                var tenant = RequireEnvironment(arguments.Optional(
                    "tenant-id-env", "COPILOT_STUDIO_TENANT_ID")!);
                var clientId = RequireEnvironment(arguments.Optional(
                    "client-id-env", "COPILOT_STUDIO_CLIENT_ID")!);
                var secret = RequireEnvironment(arguments.Optional(
                    "client-secret-env", "COPILOT_STUDIO_CLIENT_SECRET")!);
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            var headers = new ConversationHeaderHandler();
            var services = new ServiceCollection();
            services.AddHttpClient("copilot-file-probe", client =>
                client.Timeout = TimeSpan.FromSeconds(seconds))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false
                })
                .AddHttpMessageHandler(() => headers);
            using var provider = services.BuildServiceProvider();
            var client = new CopilotClient(
                settings, provider.GetRequiredService<IHttpClientFactory>(),
                _ => Task.FromResult(token), NullLogger.Instance, "copilot-file-probe");
            context.Out.WriteLine($"Authentication: {(obo ? "delegated OBO" : "delegated token")}. " +
                "SDK: 1.8.77; payload: TXT data-URI attachment.");
            context.Out.WriteLine(
                "Agent orchestration and file-upload settings must be confirmed separately.");

            return await ProbeAsync(
                async ct =>
                {
                    headers.ConversationId = null;
                    string? conversationId = null;
                    await foreach (var activity in client.StartConversationAsync(
                        emitStartConversationEvent: true, cancellationToken: ct))
                    {
                        CheckActivity(activity);
                        conversationId ??= activity.Conversation?.Id;
                    }
                    return conversationId ?? headers.ConversationId ??
                        throw new CliException("No conversation ID returned; probe is inconclusive.");
                },
                async (conversationId, activity, ct) =>
                    await ReadAnswerAsync(client.ExecuteAsync(conversationId, activity, ct), ct),
                context.Out, timeout.Token);
        }
        catch (MsalException exception)
        {
            throw new CliException(
                $"Delegated authentication failed ({exception.ErrorCode}); file support is inconclusive.");
        }
        catch (HttpRequestException exception)
        {
            var status = exception.StatusCode is { } code ? ((int)code).ToString() : "unavailable";
            throw new CliException(
                $"Copilot Studio SDK request failed (HTTP {status}); " +
                "the candidate payload is not verified. No response body or URL is logged.");
        }
        catch (JsonException)
        {
            throw new CliException("Malformed SDK response; file support is inconclusive.");
        }
    }

    internal static ConnectionSettings CreateSettings(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || !uri.Host.EndsWith(
                ".api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                "The connection variable must contain an HTTPS public-cloud Power Platform " +
                "direct-connect URL without credentials or a fragment.");
        }
        return new ConnectionSettings { DirectConnectUrl = uri.AbsoluteUri, Cloud = PowerPlatformCloud.Prod };
    }

    internal static async Task<int> ProbeAsync(
        Func<CancellationToken, Task<string>> start,
        Func<string, Activity, CancellationToken, Task<string>> send,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var fixture = FileProbeFixture.Create();
        var controlConversation = await start(cancellationToken);
        var control = await send(controlConversation, new Activity
        {
            Type = ActivityTypes.Message, Text = FileProbeFixture.SummaryQuestion
        }, cancellationToken);
        if (new[] { fixture.Project, fixture.Destination, fixture.Approval }
            .Any(fact => control.Contains(fact, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CliException("No-file control contained hidden facts; probe is invalid.");
        }
        output.WriteLine("PASS: separate no-file control did not recover the hidden facts.");

        var conversation = await start(cancellationToken);
        if (string.Equals(conversation, controlConversation, StringComparison.Ordinal))
        {
            throw new CliException("The file and control conversations were not isolated.");
        }
        var answer = await send(conversation, fixture.CreateActivity(), cancellationToken);
        var summaryFacts = new[] { FileProbeFixture.Cargo, fixture.Project, fixture.Destination };
        output.WriteLine(
            $"File-summary grounding: {CountFacts(answer, summaryFacts)}/{summaryFacts.Length} facts. " +
            $"Identical to no-file control: {string.Equals(answer, control, StringComparison.Ordinal)}.");

        var followUp = await send(conversation, new Activity
        {
            Type = ActivityTypes.Message, Text = FileProbeFixture.FollowUpQuestion
        }, cancellationToken);
        output.WriteLine($"Follow-up grounding: {CountFacts(followUp, fixture.Approval)}/1 hidden facts.");
        RequireFacts(answer, "file summary", summaryFacts);
        RequireFacts(followUp, "follow-up", fixture.Approval);
        output.WriteLine("PASS: summary response contains the file's cargo and both hidden identifiers.");
        output.WriteLine("PASS: follow-up recovered a different hidden fact without reattaching the file.");
        output.WriteLine("VERIFIED: this agent read the synthetic TXT payload through SDK ExecuteAsync. " +
            "Other file formats, limits, agents, and A2A forwarding were not tested.");
        return 0;
    }

    internal static async Task<string> ReadAnswerAsync(
        IAsyncEnumerable<IActivity> activities, CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        await foreach (var activity in activities.WithCancellation(cancellationToken))
        {
            CheckActivity(activity);
            if (activity.Type == ActivityTypes.Message &&
                !string.IsNullOrEmpty(activity.Text) &&
                activity.From?.Role != "user")
            {
                if (answer.Length + activity.Text.Length > 32_768)
                {
                    throw new CliException("SDK answer exceeded the probe's 32,768-character bound.");
                }
                answer.AppendLine(activity.Text);
            }
        }
        if (string.IsNullOrWhiteSpace(answer.ToString()))
        {
            throw new CliException("No text answer returned; file support is inconclusive.");
        }
        return answer.ToString();
    }

    private static void CheckActivity(IActivity activity)
    {
        if (activity.Attachments?.Any(attachment =>
            attachment.ContentType == OAuthCard.ContentType) == true)
        {
            throw new CliException(
                "Agent returned an OAuth sign-in card; complete existing agent sign-in separately. " +
                "File support is inconclusive; no token exchange or settings change was attempted.");
        }
        if (activity.Type == ActivityTypes.Event &&
            activity.Name?.Contains("error", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new CliException("Agent returned an error event; file support is inconclusive.");
        }
    }

    private static void RequireFacts(string answer, string stage, params string[] facts)
    {
        var recovered = CountFacts(answer, facts);
        if (recovered != facts.Length)
        {
            throw new CliException(
                $"NOT VERIFIED: {stage} recovered {recovered}/{facts.Length} required file facts. " +
                "This agent/payload failed the probe; this does not prove all SDK file input unsupported.");
        }
    }

    private static int CountFacts(string answer, params string[] facts) =>
        facts.Count(fact => answer.Contains(fact, StringComparison.OrdinalIgnoreCase));

    private static string RequireEnvironment(string name)
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

    private sealed class ConversationHeaderHandler : DelegatingHandler
    {
        internal string? ConversationId { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("x-ms-conversationid", out var values))
            {
                ConversationId = values.FirstOrDefault();
            }
            return response;
        }
    }
}
