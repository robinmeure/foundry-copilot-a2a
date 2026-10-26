using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryCopilotA2A.Cli.Tests;

public class AdaptiveCardProbeTests
{
    private const string Marker = "fixture-visible-marker";
    private const string RequestId = "request";

    [Fact]
    public async Task CommandIsRegisteredAndHelpExplainsVerificationBoundaries()
    {
        using var output = new StringWriter();
        using var client = new HttpClient();
        var app = new CliApplication(new CliContext(output, TextWriter.Null, new ProcessRunner(), client));
        Assert.Equal(0, await app.RunAsync(["--help"], CancellationToken.None));
        Assert.Contains("test-adaptive-cards", output.ToString());
        Assert.Equal(0, await app.RunAsync(["test-adaptive-cards", "--help"], CancellationToken.None));
        foreach (var expected in new[] { "--direct-connect-url-env", "--a2a-url-env",
            "--expected-marker", "SEPARATE", "NOT verified", "1.8.77" })
        {
            Assert.Contains(expected, output.ToString());
        }
    }

    [Theory]
    [InlineData()]
    [InlineData("--timeout-seconds", "9")]
    [InlineData("--timeout-seconds", "601")]
    [InlineData("--timeout-seconds", "bad")]
    [InlineData("--a2a-url-env", "UNUSED", "--obo")]
    [InlineData("--direct-connect-url-env", "UNUSED", "--a2a-bearer-token-env", "UNUSED")]
    [InlineData("--a2a-url-env", "UNUSED", "--prompt", "")]
    [InlineData("--a2a-url-env", "UNUSED", "--expected-marker", " ")]
    public async Task InvalidOptionsFailBeforeAnyNetworkCall(params string[] arguments)
    {
        using var error = new StringWriter();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected")));
        var app = new CliApplication(new CliContext(TextWriter.Null, error, new ProcessRunner(), http));
        Assert.Equal(2, await app.RunAsync(["test-adaptive-cards", .. arguments], CancellationToken.None));
        Assert.NotEmpty(error.ToString());
    }

    [Theory]
    [InlineData("http://remote.example/a2a")]
    [InlineData("https://user:private@remote.example/a2a")]
    [InlineData("https://remote.example/a2a?key=private")]
    [InlineData("https://remote.example/a2a#private")]
    [InlineData("not-a-url")]
    public void InvalidEndpointsAreNotEchoed(string endpoint)
    {
        var exception = Assert.Throws<CliException>(() => AdaptiveCardProbe.ReadEndpoint(endpoint));
        Assert.DoesNotContain(endpoint, exception.Message);
        Assert.DoesNotContain("private", exception.Message);
    }

    [Theory]
    [InlineData("http://localhost:5099/a2a/copilot-studio")]
    [InlineData("http://127.0.0.1:5099/a2a-agents/mock/a2a")]
    [InlineData("https://gateway.example/prefix/a2a-agents/specialist/a2a")]
    public void RuntimeUrlsSupportLoopbackAndGatewayPrefixes(string endpoint) =>
        Assert.Equal(endpoint, AdaptiveCardProbe.ReadEndpoint(endpoint).AbsoluteUri);

    [Fact]
    public async Task SdkRequiresAnActualAttachmentAndDoesNotNeedProviderText()
    {
        var content = Content();
        var read = await AdaptiveCardProbe.ReadSdkCardAsync(
            Activities(CardActivity(content)), Marker, CancellationToken.None);
        Assert.True(JsonElement.DeepEquals(Element(content), read));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("user")]
    [InlineData("hidden")]
    [InlineData("fallback")]
    [InlineData("wrong-type")]
    [InlineData("event")]
    public async Task SdkRejectsTextEchoesHiddenMarkersAndNonMessageCards(string scenario)
    {
        var activity = CardActivity(Content());
        switch (scenario)
        {
            case "text": activity.Text = Content().ToJsonString(); activity.Attachments.Clear(); break;
            case "user": activity.From = new ChannelAccount { Role = "user" }; break;
            case "hidden": activity = CardActivity(Content(visible: false)); break;
            case "fallback":
                var card = Content("other");
                card["fallbackText"] = Marker;
                activity = CardActivity(card); break;
            case "wrong-type": activity.Attachments[0].ContentType = "application/json"; break;
            case "event": activity.Type = ActivityTypes.Event; break;
        }
        var exception = await Assert.ThrowsAsync<CliException>(() => AdaptiveCardProbe.ReadSdkCardAsync(
            Activities(activity), Marker, CancellationToken.None));
        Assert.Contains("NOT VERIFIED", exception.Message);
        Assert.DoesNotContain(Marker, exception.Message);
    }

    [Theory]
    [InlineData("oauth")]
    [InlineData("connection")]
    [InlineData("error")]
    public async Task SdkDrainsAfterTheCardAndRejectsLateAuthOrErrors(string kind)
    {
        var activity = kind switch
        {
            "oauth" => new Activity { Type = ActivityTypes.Message,
                Attachments = [new Attachment { ContentType = OAuthCard.ContentType }] },
            "connection" => new Activity { Type = ActivityTypes.Message, Name = "connectors/connectionManagerCard" },
            _ => new Activity { Type = ActivityTypes.Event, Name = "error", Value = "private-provider-data" }
        };
        var exception = await Assert.ThrowsAsync<CliException>(() => AdaptiveCardProbe.ReadSdkCardAsync(
            Activities(CardActivity(Content()), activity), Marker, CancellationToken.None));
        Assert.DoesNotContain("private-provider-data", exception.Message);
    }

    [Fact]
    public async Task SdkCancellationAndActivityBoundAreEnforced()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AdaptiveCardProbe.ReadSdkCardAsync(Activities(CardActivity(Content())), Marker, cancellation.Token));
        await Assert.ThrowsAsync<CliException>(() => AdaptiveCardProbe.ReadSdkCardAsync(
            Activities(Enumerable.Range(0, 257).Select(_ => new Activity { Type = ActivityTypes.Typing }).ToArray()),
            Marker, CancellationToken.None));
    }

    [Fact]
    public async Task SdkRejectsConflictingFixtureCards()
    {
        await Assert.ThrowsAsync<CliException>(() => AdaptiveCardProbe.ReadSdkCardAsync(
            Activities(CardActivity(Content()), CardActivity(Content(Marker + " changed"))),
            Marker, CancellationToken.None));
    }

    [Fact]
    public async Task MarkerInsideANonRenderedPropertyFailsBothPaths()
    {
        var content = Content("Visible text without marker");
        content["body"]![0]!["items"] = new JsonArray(new JsonObject
        {
            ["type"] = "TextBlock", ["text"] = Marker
        });
        await Assert.ThrowsAsync<CliException>(() => AdaptiveCardProbe.ReadSdkCardAsync(
            Activities(CardActivity(content)), Marker, CancellationToken.None));
        Assert.Throws<CliException>(() => Validate(
            Rpc(Message([Text(), CardPart(content: content)])), false));
    }

    [Fact]
    public void MarkerInAVisibleColumnContainerIsAccepted()
    {
        var content = Content();
        var body = content["body"]!.DeepClone();
        content["body"] = new JsonArray(new JsonObject
        {
            ["type"] = "ColumnSet",
            ["columns"] = new JsonArray(new JsonObject
            {
                ["type"] = "Column",
                ["items"] = new JsonArray(new JsonObject { ["type"] = "Container", ["items"] = body })
            })
        });
        Assert.True(AdaptiveCardProbe.HasVisibleMarker(Element(content), Marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedSdkRejectsOversizedJsonBeforeDeserializingIncludingStartup(bool startup)
    {
        using var transport = new CopilotProbeSession.ConversationHeaderHandler
        {
            InnerHandler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', ProbeResponse.MaxBytes + 1), Encoding.UTF8, "application/json")
            }))
        };
        using var http = new HttpClient(transport);
        var sdk = Sdk(http);
        var response = startup
            ? sdk.StartConversationAsync(emitStartConversationEvent: true, cancellationToken: CancellationToken.None)
            : sdk.AskQuestionAsync("fixture trigger", "synthetic-conversation", CancellationToken.None);
        var exception = await Assert.ThrowsAsync<CliException>(() =>
            AdaptiveCardProbe.ReadSdkCardAsync(response, Marker, CancellationToken.None));
        Assert.Contains("2 MiB", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedSdkStalledJsonBodyObservesCancellationIncludingStartup(bool startup)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new StalledStream();
        using var transport = new CopilotProbeSession.ConversationHeaderHandler
        {
            InnerHandler = new Handler(_ =>
            {
                var content = new StreamContent(stream);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            })
        };
        using var http = new HttpClient(transport);
        var sdk = Sdk(http);
        var response = startup
            ? sdk.StartConversationAsync(emitStartConversationEvent: true, cancellationToken: cancellation.Token)
            : sdk.AskQuestionAsync("fixture trigger", "synthetic-conversation", cancellation.Token);
        var read = AdaptiveCardProbe.ReadSdkCardAsync(response, Marker, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(65_536, true)]
    [InlineData(65_537, false)]
    public void CardUtf8ByteBoundIsExact(int bytes, bool accepted)
    {
        var content = SizedContent(bytes);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(content.ToJsonString()));
        if (accepted) AdaptiveCardProbe.ValidateContent(Element(content));
        else Assert.Throws<CliException>(() => AdaptiveCardProbe.ValidateContent(Element(content)));
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void CardDepthBoundIncludesScalars(int arrays, bool accepted)
    {
        var content = Content();
        JsonNode value = JsonValue.Create("leaf")!;
        for (var i = 0; i < arrays; i++) value = new JsonArray(value);
        content["extra"] = value;
        if (accepted) AdaptiveCardProbe.ValidateContent(Element(content));
        else Assert.Throws<CliException>(() => AdaptiveCardProbe.ValidateContent(Element(content)));
    }

    [Fact]
    public async Task MatrixSendsAllFourModesWithSeparateIdentitiesAndSafeOutput()
    {
        var requests = new List<JsonElement>();
        using var output = new StringWriter();
        using var http = new HttpClient(new Handler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement;
            requests.Add(root.Clone());
            var method = root.GetProperty("method").GetString()!;
            var v03 = method.StartsWith("message/");
            var streaming = method is "SendStreamingMessage" or "message/stream";
            Assert.Equal(!v03, request.Headers.Contains("A2A-Version"));
            Assert.Equal("synthetic-a2a-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("specialist", Assert.Single(request.Headers.GetValues("X-Copilot-Agent")));
            var configuration = root.GetProperty("params").GetProperty("configuration");
            Assert.True(configuration.GetProperty("blocking").GetBoolean());
            Assert.Contains(configuration.GetProperty("acceptedOutputModes").EnumerateArray(),
                mode => mode.GetString() == "application/json");
            var message = root.GetProperty("params").GetProperty("message");
            Assert.Equal(v03 ? "user" : "ROLE_USER", message.GetProperty("role").GetString());
            Assert.Equal(v03, message.TryGetProperty("kind", out _));
            Assert.Equal("topic trigger", message.GetProperty("parts")[0].GetProperty("text").GetString());
            var id = root.GetProperty("id").GetString()!;
            var body = streaming
                ? Event(Artifact([Text(), CardPart()], v03: v03), id) +
                  Event(Status(v03: v03), id)
                : Rpc(Message([Text(), CardPart()], v03), id);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }));
        await AdaptiveCardWireProbe.RunAsync(http, new Uri("https://adapter.example/a2a"),
            "synthetic-a2a-token", "specialist", "topic trigger", Marker, Element(Content()),
            output, CancellationToken.None);
        Assert.Equal(4, requests.Count);
        Assert.Equal(4, requests.Select(r => r.GetProperty("id").GetString()).Distinct().Count());
        foreach (var field in new[] { "messageId", "contextId" })
        {
            Assert.Equal(4, requests.Select(r => r.GetProperty("params").GetProperty("message")
                .GetProperty(field).GetString()).Distinct().Count());
        }
        Assert.Equal(4, output.ToString().Split("PASS:").Length - 1);
        foreach (var secret in new[] { "synthetic-a2a-token", "adapter.example", Marker, "specialist", "topic trigger" })
        {
            Assert.DoesNotContain(secret, output.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaskSnapshotsAndLateStreamingCardsAreAccepted(bool v03)
    {
        var task = new JsonObject
        {
            ["status"] = new JsonObject { ["state"] = v03 ? "completed" : "TASK_STATE_COMPLETED" },
            ["artifacts"] = new JsonArray(new JsonObject
            {
                ["artifactId"] = "answer", ["parts"] = new JsonArray(Text(), CardPart())
            })
        };
        if (v03) task["kind"] = "task";
        Assert.Equal(1, Validate(Rpc(v03 ? task : new JsonObject { ["task"] = task }), false));
        var stream = Event(Artifact([Text()], v03: v03)) +
            Event(Status(v03: v03)) +
            Event(Artifact([CardPart()], append: true, v03: v03));
        Assert.Equal(1, Validate(stream, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArtifactStreamWithoutCompletedStatusFailsAtEof(bool v03)
    {
        Assert.Throws<CliException>(() => Validate(Event(Artifact([Text(), CardPart()], v03: v03)), true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextOrCardAloneCannotPass(bool cardOnly)
    {
        var parts = cardOnly ? new[] { CardPart() } : new[] { Text(Marker) };
        Assert.Throws<CliException>(() => Validate(Rpc(Message(parts)), false));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("contentType")]
    [InlineData("revision")]
    [InlineData("fallbackText")]
    [InlineData("id")]
    public void MalformedEnvelopesFailInsteadOfIgnoringBadCards(string field)
    {
        var part = CardPart();
        var envelope = part["data"]![AdaptiveCardProbe.ExtensionUri]!;
        if (field == "schemaVersion") envelope[field] = "9";
        else envelope["cards"]![0]![field] = "";
        Assert.Throws<CliException>(() => Validate(Rpc(Message([Text(), part])), false));
    }

    [Fact]
    public void UserEchoAndUncorrelatedResponsesFail()
    {
        var message = Message([Text(), CardPart()]);
        message["message"]!["role"] = "ROLE_USER";
        Assert.Throws<CliException>(() => Validate(Rpc(message), false));
        Assert.Throws<CliException>(() => Validate(Rpc(Message([Text(), CardPart()]), "other-request"), false));
    }

    [Theory]
    [InlineData("TASK_STATE_FAILED")]
    [InlineData("failed")]
    [InlineData("input-required")]
    [InlineData("auth-required")]
    [InlineData("canceled")]
    [InlineData("rejected")]
    [InlineData("working")]
    public void LateFailureOrIncompleteTaskCannotPass(string state)
    {
        var stream = Event(Artifact([Text(), CardPart()])) + Event(Status(state));
        Assert.Throws<CliException>(() => Validate(stream, true));
    }

    [Fact]
    public void ErrorsAfterCompletionAreNotDiscardedOrPrinted()
    {
        var stream = Event(Artifact([Text(), CardPart()])) + Event(Status()) +
            """data: {"jsonrpc":"2.0","id":"request","error":{"code":-1,"message":"private-provider-data"}}""";
        var exception = Assert.Throws<CliException>(() => Validate(stream, true));
        Assert.DoesNotContain("private-provider-data", exception.Message);
    }

    [Fact]
    public void ReplacementAndRevisionsAreAppliedPerArtifact()
    {
        var first = Event(Artifact([Text(), CardPart()]));
        Assert.Throws<CliException>(() => Validate(first + Event(Artifact([Text()])) + Event(Status()), true));
        var other = Event(Artifact([Text()], artifactId: "other"));
        Assert.Equal(1, Validate(first + other + Event(Status()), true));
        var newer = CardPart(revision: 2, content: Content("removed marker"));
        Assert.Throws<CliException>(() => Validate(first + Event(Artifact([newer], append: true)) + Event(Status()), true));
        var conflict = CardPart(content: Content(Marker + " changed"));
        Assert.Throws<CliException>(() => Validate(first + Event(Artifact([conflict], append: true)) + Event(Status()), true));
        Assert.Equal(1, Validate(first + Event(Artifact([CardPart()], append: true)) + Event(Status()), true));
    }

    [Fact]
    public void ContentComparisonDoesNotAcceptMarkerOnlyOrObjectPropertyOrderDifferences()
    {
        var wire = Rpc(Message([Text(), CardPart()]));
        Assert.Throws<CliException>(() => AdaptiveCardWireProbe.ValidateResponse(
            wire, RequestId, false, Marker, Element(Content(Marker + " different"))));
        var expected = JsonNode.Parse(
            $$"""{"body":[{"text":"{{Marker}}","type":"TextBlock"}],"version":"1.5","type":"AdaptiveCard"}""")!;
        Assert.Equal(1, AdaptiveCardWireProbe.ValidateResponse(wire, RequestId, false, Marker, Element(expected)));
    }

    [Fact]
    public void AccumulatedCardCountAndBytesAreBounded()
    {
        var stream = "";
        for (var i = 0; i < 5; i++)
        {
            stream += Event(Artifact([Text(), CardPart(id: $"card-{i}")], append: true));
        }
        Assert.Equal(5, Validate(stream + Event(Status()), true));
        Assert.Throws<CliException>(() => Validate(stream +
            Event(Artifact([CardPart(id: "six")], append: true)) + Event(Status()), true));

        var large = "";
        for (var i = 0; i < 4; i++)
        {
            large += Event(Artifact([Text(), CardPart(id: $"large-{i}", content: SizedContent(65_536))], append: true));
        }
        Assert.Equal(4, Validate(large + Event(Status()), true));
        Assert.Throws<CliException>(() => Validate(large +
            Event(Artifact([CardPart(id: "extra")], append: true)) + Event(Status()), true));
    }

    [Fact]
    public void SseSupportsCommentsMultilineDataCrLfAndUnterminatedLastEvent()
    {
        var json = Rpc(Message([Text(), CardPart()]));
        var lines = ": heartbeat\r\n\r\ndata: " + json.Replace(",\"result\":", ",\r\ndata: \"result\":");
        Assert.Equal(1, Validate(lines, true));
    }

    [Theory]
    [InlineData(2_097_152, true)]
    [InlineData(2_097_153, false)]
    public async Task ResponseByteBoundIsExact(int bytes, bool accepted)
    {
        using var content = new ByteArrayContent(new byte[bytes]);
        if (accepted)
        {
            Assert.Equal(bytes, (await AdaptiveCardWireProbe.ReadBoundedAsync(content, CancellationToken.None)).Length);
        }
        else
        {
            await Assert.ThrowsAsync<CliException>(() =>
                AdaptiveCardWireProbe.ReadBoundedAsync(content, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ResponseLimitDoesNotTrustDeclaredContentLength()
    {
        using var content = new ByteArrayContent(new byte[ProbeResponse.MaxBytes + 1]);
        content.Headers.ContentLength = 1;
        await Assert.ThrowsAsync<CliException>(() =>
            AdaptiveCardWireProbe.ReadBoundedAsync(content, CancellationToken.None));
    }

    [Theory]
    [InlineData(302, "application/json")]
    [InlineData(401, "application/json")]
    [InlineData(200, "text/html")]
    public async Task HttpFailuresDoNotPrintProviderBodies(int status, string type)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("private-provider-data", Encoding.UTF8, type)
        })));
        var exception = await Assert.ThrowsAsync<CliException>(() => AdaptiveCardWireProbe.RunAsync(
            http, new Uri("https://adapter.example/a2a"), null, null, "trigger", Marker, null,
            TextWriter.Null, CancellationToken.None));
        Assert.DoesNotContain("private-provider-data", exception.Message);
    }

    private static JsonObject Content(string text = Marker, bool visible = true)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text };
        if (!visible) block["isVisible"] = false;
        return new JsonObject
        {
            ["type"] = "AdaptiveCard", ["version"] = "1.5", ["body"] = new JsonArray(block)
        };
    }

    private static JsonObject SizedContent(int bytes)
    {
        var content = Content();
        content["padding"] = "";
        content["padding"] = new string('x', bytes - Encoding.UTF8.GetByteCount(content.ToJsonString()));
        return content;
    }

    private static JsonObject CardPart(string id = "card", int revision = 1, JsonObject? content = null) => new()
    {
        ["data"] = new JsonObject
        {
            [AdaptiveCardProbe.ExtensionUri] = new JsonObject
            {
                ["schemaVersion"] = "1",
                ["cards"] = new JsonArray(new JsonObject
                {
                    ["id"] = id, ["revision"] = revision, ["contentType"] = AdaptiveCardProbe.ContentType,
                    ["fallbackText"] = "Safe fallback", ["content"] = content ?? Content()
                })
            }
        }
    };

    private static JsonObject Text(string value = "Ordinary answer") => new() { ["text"] = value };
    private static JsonObject Message(JsonObject[] parts, bool v03 = false)
    {
        var message = new JsonObject { ["role"] = v03 ? "agent" : "ROLE_AGENT", ["parts"] = new JsonArray(parts) };
        if (v03) message["kind"] = "message";
        return v03 ? message : new JsonObject { ["message"] = message };
    }

    private static JsonObject Artifact(
        JsonObject[] parts, bool append = false, string artifactId = "answer", bool v03 = false)
    {
        var update = new JsonObject
        {
            ["artifact"] = new JsonObject { ["artifactId"] = artifactId, ["parts"] = new JsonArray(parts) },
            ["append"] = append
        };
        if (v03) update["kind"] = "artifact-update";
        return v03 ? update : new JsonObject { ["artifactUpdate"] = update };
    }

    private static JsonObject Status(string? state = null, bool v03 = false)
    {
        var update = new JsonObject
        {
            ["status"] = new JsonObject { ["state"] = state ?? (v03 ? "completed" : "TASK_STATE_COMPLETED") }
        };
        if (v03) update["kind"] = "status-update";
        return v03 ? update : new JsonObject { ["statusUpdate"] = update };
    }

    private static string Rpc(JsonObject result, string id = RequestId) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result
    }.ToJsonString();
    private static string Event(JsonObject result, string id = RequestId) => $"data: {Rpc(result, id)}\n\n";
    private static int Validate(string value, bool stream) =>
        AdaptiveCardWireProbe.ValidateResponse(value, RequestId, stream, Marker);
    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
    private static Activity CardActivity(JsonObject content) => new()
    {
        Type = ActivityTypes.Message,
        Attachments = [new Attachment { ContentType = AdaptiveCardProbe.ContentType, Content = Element(content) }]
    };
    private static async IAsyncEnumerable<IActivity> Activities(params Activity[] activities)
    {
        await Task.Yield();
        foreach (var activity in activities) yield return activity;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }

    private static CopilotClient Sdk(HttpClient http) => new(
        CopilotProbeSession.CreateSettings(
            "https://example.api.powerplatform.com/copilotstudio/dataverse-backed/authenticated/bots/test/conversations?api-version=2022-03-01-preview"),
        new ProbeHttpClientFactory(http), _ => Task.FromResult("synthetic-test-token"), NullLogger.Instance, "test");

    private sealed class ProbeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StalledStream : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("SDK must never perform a synchronous network read.");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
