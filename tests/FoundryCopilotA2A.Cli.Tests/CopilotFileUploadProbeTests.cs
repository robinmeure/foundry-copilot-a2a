using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryCopilotA2A.Cli.Tests;

public class CopilotFileUploadProbeTests
{
    [Fact]
    public async Task CommandHelpDescribesProbeAndAuthentication()
    {
        using var output = new StringWriter();
        using var client = new HttpClient();
        var application = new CliApplication(new CliContext(
            output, TextWriter.Null, new ProcessRunner(), client));

        Assert.Equal(0, await application.RunAsync(
            ["test-copilot-file-upload", "--help"], CancellationToken.None));
        Assert.Contains("--direct-connect-url-env", output.ToString());
        Assert.Contains("--obo", output.ToString());
        Assert.Contains("1.8.77", output.ToString());
    }

    [Theory]
    [InlineData("9")]
    [InlineData("601")]
    [InlineData("invalid")]
    public async Task InvalidTimeoutFailsBeforeReadingConfiguration(string value)
    {
        using var error = new StringWriter();
        using var client = new HttpClient();
        var application = new CliApplication(new CliContext(
            TextWriter.Null, error, new ProcessRunner(), client));
        var exit = await application.RunAsync(
            ["test-copilot-file-upload", "--timeout-seconds", value], CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("--timeout-seconds", error.ToString());
    }

    [Fact]
    public void FixtureKeepsHiddenFactsInTheFileOnly()
    {
        var fixture = FileProbeFixture.Create();
        var activity = fixture.CreateActivity();
        var attachment = Assert.Single(activity.Attachments);

        Assert.Equal("message", activity.Type);
        Assert.Equal("text/plain", attachment.ContentType);
        Assert.Equal("shipment.txt", attachment.Name);
        Assert.StartsWith("data:text/plain;base64,", attachment.ContentUrl);
        var content = DecodeFile(activity);
        foreach (var fact in new[] { fixture.Project, fixture.Destination, fixture.Approval })
        {
            Assert.Contains(fact, content);
            Assert.DoesNotContain(fact, activity.Text);
            Assert.DoesNotContain(fact, FileProbeFixture.FollowUpQuestion);
            Assert.DoesNotContain(fact, attachment.Name);
        }
        Assert.NotEqual(fixture.Project, FileProbeFixture.Create().Project);
    }

    [Fact]
    public async Task PinnedSdkSerializesAttachmentThroughExecuteAsync()
    {
        var fixture = FileProbeFixture.Create();
        var activity = fixture.CreateActivity();
        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler);
        var client = new CopilotClient(
            CopilotProbeSession.CreateSettings(
                "https://example.api.powerplatform.com/copilotstudio/dataverse-backed/authenticated/bots/test/conversations?api-version=2022-03-01-preview"),
            new ProbeHttpClientFactory(httpClient),
            _ => Task.FromResult("synthetic-test-token"), NullLogger.Instance, "test");

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.ExecuteAsync("test-conversation", activity, CancellationToken.None))
            {
            }
        });

        Assert.NotNull(handler.Body);
        using var document = JsonDocument.Parse(handler.Body);
        var sent = document.RootElement.TryGetProperty("activity", out var wrapped)
            ? wrapped : document.RootElement;
        Assert.Equal(FileProbeFixture.SummaryQuestion, sent.GetProperty("text").GetString());
        var attachment = Assert.Single(sent.GetProperty("attachments").EnumerateArray());
        Assert.Equal(activity.Attachments[0].ContentUrl, attachment.GetProperty("contentUrl").GetString());
        Assert.Equal("text/plain", attachment.GetProperty("contentType").GetString());
        Assert.Equal("test-conversation", sent.GetProperty("conversation").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ProbeRequiresSeparateControlSummaryAndFileFreeFollowUp()
    {
        var starts = 0;
        var calls = 0;
        var facts = Array.Empty<string>();
        using var output = new StringWriter();

        var result = await CopilotFileUploadProbe.ProbeAsync(
            _ => Task.FromResult($"conversation-{++starts}"),
            (conversation, activity, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    Assert.Equal("conversation-1", conversation);
                    Assert.Empty(activity.Attachments);
                    Assert.Equal(FileProbeFixture.SummaryQuestion, activity.Text);
                    return Task.FromResult("There is no attached file.");
                }
                Assert.Equal("conversation-2", conversation);
                if (calls == 2)
                {
                    facts = ReadFacts(activity);
                    return Task.FromResult(
                        $"The shipment is {FileProbeFixture.Cargo} for {facts[0]}, bound for {facts[1]}.");
                }
                Assert.Equal(3, calls);
                Assert.Empty(activity.Attachments);
                Assert.Equal(FileProbeFixture.FollowUpQuestion, activity.Text);
                return Task.FromResult(facts[2]);
            }, output, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(2, starts);
        Assert.Equal(3, calls);
        Assert.Contains("VERIFIED:", output.ToString());
        foreach (var fact in facts)
        {
            Assert.DoesNotContain(fact, output.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeRejectsGenericSummaryOrMissingFollowUpFacts(bool validSummary)
    {
        var starts = 0;
        var calls = 0;
        var facts = Array.Empty<string>();
        using var output = new StringWriter();
        var exception = await Assert.ThrowsAsync<CliException>(() =>
            CopilotFileUploadProbe.ProbeAsync(
                _ => Task.FromResult($"conversation-{++starts}"),
                (_, activity, _) =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        return Task.FromResult("No file attached.");
                    }
                    if (calls == 2 && validSummary)
                    {
                        facts = ReadFacts(activity);
                        return Task.FromResult(
                            $"{FileProbeFixture.Cargo} / {facts[0]} / {facts[1]}");
                    }
                    return Task.FromResult("Here is a generic summary with no file facts.");
                }, output, CancellationToken.None));

        Assert.Contains(validSummary ? "follow-up" : "file summary", exception.Message);
        Assert.Contains("NOT VERIFIED:", exception.Message);
        Assert.DoesNotContain("VERIFIED:", output.ToString());
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ProbeRejectsReusedConversation()
    {
        var calls = 0;
        var exception = await Assert.ThrowsAsync<CliException>(() =>
            CopilotFileUploadProbe.ProbeAsync(
                _ => Task.FromResult("same"),
                (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult("No file.");
                }, TextWriter.Null, CancellationToken.None));

        Assert.Contains("not isolated", exception.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProbePropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CopilotFileUploadProbe.ProbeAsync(
                ct => Task.FromCanceled<string>(ct),
                (_, _, _) => throw new InvalidOperationException("Must not send."),
                TextWriter.Null, cancellation.Token));
    }

    [Theory]
    [InlineData("http://example.api.powerplatform.com/path")]
    [InlineData("https://user:secret@example.api.powerplatform.com/path")]
    [InlineData("https://example.api.powerplatform.com/path#fragment")]
    [InlineData("https://example.api.powerplatform.com.evil.example/path")]
    [InlineData("https://example.org/path")]
    [InlineData("not-a-url")]
    public void SettingsRejectUnsafeConnectionWithoutEchoingIt(string url)
    {
        var exception = Assert.Throws<CliException>(() => CopilotProbeSession.CreateSettings(url));
        Assert.DoesNotContain(url, exception.Message);
    }

    [Fact]
    public void SettingsAcceptPublicCloudConnection()
    {
        var settings = CopilotProbeSession.CreateSettings(
            "https://example.api.powerplatform.com/copilotstudio/dataverse-backed/authenticated/bots/test/conversations?api-version=2022-03-01-preview");
        Assert.NotNull(settings.DirectConnectUrl);
    }

    [Theory]
    [InlineData("""{"scp":"CopilotStudio.Copilots.Invoke","idtyp":"user"}""", true)]
    [InlineData("""{"scp":"access_as_user"}""", true)]
    [InlineData("""{"roles":["invoke"],"idtyp":"app"}""", false)]
    [InlineData("""{"scp":"invoke","idtyp":"app"}""", false)]
    [InlineData("""{"scp":""}""", false)]
    [InlineData("""{"scp":42}""", false)]
    [InlineData("[]", false)]
    public void TokensMustBeDelegated(string payload, bool accepted)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"header.{encoded}.signature";
        if (accepted)
        {
            CopilotProbeSession.RequireDelegatedToken(token);
        }
        else
        {
            var exception = Assert.Throws<CliException>(() =>
                CopilotProbeSession.RequireDelegatedToken(token));
            Assert.DoesNotContain(token, exception.Message);
        }
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("a.!.c")]
    [InlineData("a.aaaa.c")]
    public void MalformedTokensFailWithoutEchoingThem(string token)
    {
        var exception = Assert.Throws<CliException>(() =>
            CopilotProbeSession.RequireDelegatedToken(token));
        Assert.DoesNotContain(token, exception.Message);
    }

    [Fact]
    public async Task AnswersRetainTextButNotUserEchoes()
    {
        var answer = await CopilotFileUploadProbe.ReadAnswerAsync(Activities(
            new Activity { Type = ActivityTypes.Typing },
            new Activity
            {
                Type = ActivityTypes.Message, Text = "user echo",
                From = new ChannelAccount { Role = "user" }
            },
            new Activity { Type = ActivityTypes.Message, Text = "first" },
            new Activity { Type = ActivityTypes.Message, Text = "second" }),
            CancellationToken.None);
        Assert.Contains("first", answer);
        Assert.Contains("second", answer);
        Assert.DoesNotContain("user echo", answer);
    }

    [Fact]
    public async Task SignInCardsAreExplicitBlockers()
    {
        var exception = await Assert.ThrowsAsync<CliException>(() =>
            CopilotFileUploadProbe.ReadAnswerAsync(Activities(new Activity
            {
                Type = ActivityTypes.Message,
                Attachments = [new Attachment { ContentType = OAuthCard.ContentType }]
            }), CancellationToken.None));
        Assert.Contains("OAuth", exception.Message);
        Assert.Contains("inconclusive", exception.Message);
    }

    [Fact]
    public async Task ProviderErrorEventsAreExplicitBlockers()
    {
        var exception = await Assert.ThrowsAsync<CliException>(() =>
            CopilotFileUploadProbe.ReadAnswerAsync(Activities(new Activity
            {
                Type = ActivityTypes.Event, Name = "error", Value = "provider-secret"
            }), CancellationToken.None));
        Assert.Contains("error event", exception.Message);
        Assert.DoesNotContain("provider-secret", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyAnswersAreNotSuccess(string text)
    {
        await Assert.ThrowsAsync<CliException>(() =>
            CopilotFileUploadProbe.ReadAnswerAsync(Activities(
                new Activity { Type = ActivityTypes.Message, Text = text }), CancellationToken.None));
    }

    [Fact]
    public async Task AnswerCaptureIsBounded()
    {
        var exception = await Assert.ThrowsAsync<CliException>(() =>
            CopilotFileUploadProbe.ReadAnswerAsync(Activities(
                new Activity { Type = ActivityTypes.Message, Text = new string('a', 32_769) }),
                CancellationToken.None));
        Assert.Contains("bound", exception.Message);
    }

    private static string DecodeFile(Activity activity) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(
            Assert.Single(activity.Attachments).ContentUrl.Split(',', 2)[1]));

    private static string[] ReadFacts(Activity activity) =>
        DecodeFile(activity).Split('\n').Skip(2).Take(3)
            .Select(line => line.Split(": ", 2)[1]).ToArray();

    private static async IAsyncEnumerable<IActivity> Activities(
        params Activity[] activities)
    {
        await Task.Yield();
        foreach (var activity in activities)
        {
            yield return activity;
        }
    }

    private sealed class ProbeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CaptureRequestHandler : HttpMessageHandler
    {
        internal string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException("Captured synthetic request; no network call made.");
        }
    }
}
