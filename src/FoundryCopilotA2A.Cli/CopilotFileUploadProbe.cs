using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
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

        try
        {
            using var session = await CopilotProbeSession.ConnectAsync(
                context, arguments, seconds, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            context.Out.WriteLine($"Authentication: {(arguments.Flag("obo") ? "delegated OBO" : "delegated token")}. " +
                "SDK: 1.8.77; payload: TXT data-URI attachment.");
            context.Out.WriteLine(
                "Agent orchestration and file-upload settings must be confirmed separately.");

            return await ProbeAsync(
                session.StartAsync,
                async (conversationId, activity, ct) =>
                    await ReadAnswerAsync(session.Client.ExecuteAsync(conversationId, activity, ct), ct),
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
            CopilotProbeSession.CheckActivity(activity);
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

}
