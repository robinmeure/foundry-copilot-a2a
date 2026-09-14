using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BotActivity = Microsoft.Agents.Core.Models.Activity;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class CitationTests
{
    private static readonly AgentResponder Specialist = new("specialist", "Research specialist");

    [Fact]
    public void NativeActivityEntitiesBecomeStructuredSources()
    {
        var bundle = CopilotStudioCitations.Read(CitedActivity(), Specialist);

        var source = Assert.Single(Assert.IsType<CitationBundle>(bundle).Sources);
        Assert.Equal("Committee report", source.Title);
        Assert.Equal("https://example.org/report", source.Url);
        Assert.Equal("answer-1", source.OriginatingMessageId);
        Assert.Equal(Specialist, source.OriginatingAgent);
        var reference = Assert.Single(bundle.Citations);
        Assert.Equal(source.Id, reference.SourceId);
        Assert.Equal("[1]", reference.Marker);
        Assert.Equal("The committee discussed the proposal.", reference.Quote);
    }

    [Fact]
    public void DeserializedProviderActivityPreservesCitationEntities()
    {
        const string json = """
            {
              "type":"message","id":"answer-1","text":"The answer [1].",
              "entities":[{
                "type":"https://schema.org/Message",
                "@type":"Message","@context":"https://schema.org",
                "additionalType":["AIGeneratedContent"],
                "citation":[{
                  "@type":"Claim","position":1,
                  "appearance":{
                    "@type":"DigitalDocument","name":"Committee report",
                    "url":"https://example.org/report","abstract":"A provider excerpt."
                  }
                }]
              }]
            }
            """;
        var activity = ProtocolJsonSerializer.ToObject<BotActivity>(json);
        var bundle = CopilotStudioCitations.Read(activity, Specialist);

        Assert.Equal("Committee report", Assert.Single(bundle!.Sources).Title);
        Assert.Equal("[1]", Assert.Single(bundle.Citations).Marker);
    }

    [Fact]
    public void LateFinalCitationsSurviveSuppressedDuplicateText()
    {
        var stream = new CopilotStudioAnswerStream();
        var delta = new BotActivity
        {
            Id = "answer-1",
            Type = "typing",
            Text = "The answer [1].",
            ChannelData = new { streamType = "streaming" }
        };
        var first = stream.NextUpdate(delta, null, "conversation", Specialist);
        Assert.Equal("The answer [1].", first!.Text);
        Assert.Null(first.Citations);

        var final = CitedActivity();
        final.Text = delta.Text;
        final.ChannelData = new { streamType = "final", streamId = "answer-1" };
        var last = stream.NextUpdate(final, final.Text, "conversation", Specialist);
        Assert.Equal(string.Empty, last!.Text);
        Assert.Single(last.Citations!.Sources);
        Assert.Equal(1, stream.SuppressedFinalCount);
        Assert.Equal("answer-1", last.Citations.Sources[0].OriginatingMessageId);
    }

    [Fact]
    public void InformativeActivitiesCannotSupplyFinalAnswerCitations()
    {
        var activity = CitedActivity();
        activity.Type = "typing";
        activity.ChannelData = new { streamType = "informative" };

        var update = new CopilotStudioAnswerStream()
            .NextUpdate(activity, null, "conversation", Specialist);

        Assert.True(update!.IsInformative);
        Assert.Null(update.Citations);
    }

    [Fact]
    public void PlainTextLinksAreNotPromotedIntoVerifiedSourceData()
    {
        var activity = new BotActivity { Type = "message", Text = "See https://example.org" };
        Assert.Null(CopilotStudioCitations.Read(activity, Specialist));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/secret")]
    [InlineData("https://user:password@example.org/report")]
    [InlineData("/relative/document")]
    [InlineData("https://example.org\\report")]
    [InlineData("https://example.org/report?access_token=secret")]
    [InlineData("https://example.org/report?sig=secret")]
    [InlineData("https://example.org/report?%74oken=secret")]
    public void UnsafeProviderSourceUrlsFailExplicitly(string url)
    {
        var activity = CitedActivity(url);
        var exception = Assert.Throws<CitationFormatException>(
            () => CopilotStudioCitations.Read(activity, Specialist));
        Assert.Contains("Invalid structured citations", exception.Message);
        Assert.DoesNotContain(url, exception.Message);
    }

    [Fact]
    public void SourcesWithoutPublicUrlsArePreservedWithoutInventingLinks()
    {
        var activity = CitedActivity(null);
        var citations = CopilotStudioCitations.Read(activity, Specialist);
        Assert.Null(Assert.Single(citations!.Sources).Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CitationsAloneCannotTurnAnEmptyAnswerIntoSuccess(bool streaming)
    {
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICopilotStudioInvoker>();
                services.AddSingleton<ICopilotStudioInvoker, CitationOnlyInvoker>();
            }));
        using var client = factory.CreateClient();
        using var request = Request("only-citations", false, streaming, false);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Responding agent:", body);
        Assert.Contains(streaming ? "no text response" : "\"error\"", body);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("v03-message")]
    [InlineData("task")]
    [InlineData("v03-task")]
    public void UpstreamAnswerShapesPreserveSourceProvenance(string shape)
    {
        var content = AgentCitations.ToContent(Sample());
        var data = JsonSerializer.SerializeToNode(Assert.IsType<A2A.Part>(content.RawRepresentation),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var parts = new JsonArray(new JsonObject { ["text"] = "Answer [1]." }, data);
        var message = new JsonObject { ["parts"] = parts };
        var task = new JsonObject
        {
            ["artifacts"] = new JsonArray(new JsonObject { ["parts"] = parts.DeepClone() })
        };
        JsonNode result = shape switch
        {
            "message" => new JsonObject { ["message"] = message },
            "v03-message" => message,
            "task" => new JsonObject { ["task"] = task },
            _ => task
        };
        using var document = JsonDocument.Parse(new JsonObject { ["result"] = result }.ToJsonString());

        var answer = A2AResponseText.Read(document.RootElement);

        Assert.Equal("Answer [1].", answer.Text);
        Assert.Equal(Specialist, Assert.Single(answer.Citations!.Sources).OriginatingAgent);
        Assert.Equal("[1]", Assert.Single(answer.Citations.Citations).Marker);
    }

    [Fact]
    public void DuplicatesMergeButSourceConflictsAndDanglingReferencesFail()
    {
        var bundle = Sample();
        var merged = AgentCitations.Merge(bundle, bundle)!;
        Assert.Single(merged.Sources);
        Assert.Single(merged.Citations);
        Assert.Throws<CitationFormatException>(() => AgentCitations.Merge(bundle,
            bundle with { Sources = [bundle.Sources[0] with { Title = "Conflicting title" }] }));
        Assert.Throws<CitationFormatException>(() => AgentCitations.Merge(null,
            bundle with { Citations = [new CitationReference("missing")] }));
    }

    [Fact]
    public void SourceIdentifiersAreStableButScopedToTheirOriginalResponse()
    {
        Assert.Equal(Sample().Sources[0].Id, Sample().Sources[0].Id);
        var another = AgentCitations.FromProviderSource(
            new AgentResponder("other-agent", "Other agent"),
            "answer-1", "Committee report", "https://example.org/report", "[1]");
        Assert.NotEqual(Sample().Sources[0].Id, another.Sources[0].Id);
        Assert.Equal(2, AgentCitations.Merge(Sample(), another)!.Sources.Count);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"2","sources":[],"citations":[]}""")]
    [InlineData("""{"schemaVersion":"1","sources":[{}],"citations":[]}""")]
    [InlineData("""{"schemaVersion":"1","sources":[],"citations":[{"sourceId":"unknown"}]}""")]
    [InlineData("""{"schemaVersion":"1","sources":[],"citations":null}""")]
    public void MalformedRecognizedPayloadIsNotSilentlyDiscarded(string json)
    {
        using var document = JsonDocument.Parse(new JsonObject
        {
            [AgentCitations.ExtensionUri] = JsonNode.Parse(json)
        }.ToJsonString());
        Assert.Throws<CitationFormatException>(() => AgentCitations.ReadContainer(document.RootElement));
    }

    [Fact]
    public void UnrelatedStructuredDataRemainsOptional()
    {
        using var document = JsonDocument.Parse("""{"some-other-extension":{"value":1}}""");
        Assert.Null(AgentCitations.ReadContainer(document.RootElement));
    }

    [Fact]
    public void LimitsAreEnforcedAtMergeNotJustOnIndividualPayloads()
    {
        var sources = Enumerable.Range(0, 100)
            .Select(index => Sample().Sources[0] with { Id = $"source-{index}" }).ToArray();
        var current = new CitationBundle(sources, []);
        AgentCitations.Validate(current);
        Assert.Throws<CitationFormatException>(() => AgentCitations.Merge(current, Sample()));
        Assert.Throws<CitationFormatException>(() => AgentCitations.Validate(
            new CitationBundle([sources[0]],
                Enumerable.Range(0, 201).Select(index =>
                    new CitationReference(sources[0].Id, $"[{index}]")).ToArray())));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WireResponsesAndReplayCarryLateSourcesWithoutDuplicatingText(bool v03, bool agentRoute)
    {
        using var factory = new A2AAdapterFactory();
        using var client = factory.CreateClient();
        foreach (var messageId in new[] { "citation-turn-1", "citation-turn-2" })
        {
            foreach (var streaming in new[] { true, false, true })
            {
                using var request = Request(messageId, v03, streaming, agentRoute);
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                var payloads = streaming
                    ? body.Split('\n').Select(line => line.Trim())
                        .Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
                        .Select(line => line["data: ".Length..])
                    : [body];
                var text = new List<string>();
                CitationBundle? citations = null;
                foreach (var payload in payloads)
                {
                    using var document = JsonDocument.Parse(payload);
                    Assert.False(document.RootElement.TryGetProperty("error", out _), payload);
                    var result = document.RootElement.GetProperty("result");
                    var container = result.TryGetProperty("artifactUpdate", out var update)
                        ? update.GetProperty("artifact")
                        : result.TryGetProperty("artifact", out var artifact)
                            ? artifact
                            : result.TryGetProperty("message", out var message) ? message : result;
                    if (!container.TryGetProperty("parts", out var parts))
                    {
                        continue;
                    }
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var answer))
                        {
                            text.Add(answer.GetString()!);
                        }
                        if (part.TryGetProperty("data", out var dataPart))
                        {
                            citations = AgentCitations.Merge(citations, AgentCitations.ReadContainer(dataPart));
                        }
                    }
                }
                Assert.Equal(
                    "Responding agent: Mock Copilot Studio\n\nThis is a mock answer with a source [1].",
                    string.Concat(text));
                Assert.Equal("https://example.org/", Assert.Single(citations!.Sources).Url);
                Assert.Single(citations.Citations);
            }
        }
    }

    [Theory]
    [InlineData("/.well-known/agent-card.json", "1.0")]
    [InlineData("/.well-known/agent-card.json", "0.3")]
    [InlineData("/.well-known/agent-card.json", "")]
    [InlineData("/", "0.3")]
    [InlineData("/a2a-agents/mock/.well-known/agent-card.json", "0.3")]
    public async Task AgentCardsAdvertiseOptionalCitationData(string path, string version)
    {
        using var factory = new A2AAdapterFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("A2A-Version", version);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        Assert.Contains(AgentCitations.ExtensionUri, body);
        Assert.Contains("application/json", body);
        Assert.DoesNotContain("\"required\":true", body);
    }

    internal static CitationBundle Sample() => AgentCitations.FromProviderSource(
        Specialist, "answer-1", "Committee report", "https://example.org/report", "[1]");

    internal static string UpstreamResponse(string text)
    {
        var part = Assert.IsType<A2A.Part>(AgentCitations.ToContent(Sample()).RawRepresentation);
        return new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["parts"] = new JsonArray(
                        new JsonObject { ["text"] = text },
                        JsonSerializer.SerializeToNode(part, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                }
            }
        }.ToJsonString();
    }

    private static BotActivity CitedActivity(string? url = "https://example.org/report") => new()
    {
        Type = "message",
        Id = "answer-1",
        Text = "The answer [1].",
        Entities =
        [
            new AIEntity
            {
                Citation =
                [
                    new ClientCitation
                    {
                        Position = 1,
                        Appearance = new ClientCitationAppearance
                        {
                            Name = "Committee report",
                            Url = url,
                            Abstract = "The committee discussed the proposal."
                        }
                    }
                ]
            }
        ]
    };

    internal static HttpRequestMessage Request(string messageId, bool v03, bool streaming, bool agentRoute)
    {
        var part = new JsonObject { ["text"] = MockCopilotStudioInvoker.CitationPrompt };
        var message = new JsonObject
        {
            ["role"] = v03 ? "user" : "ROLE_USER",
            ["messageId"] = messageId,
            ["contextId"] = "citation-conversation",
            ["parts"] = new JsonArray(part)
        };
        if (v03)
        {
            message["kind"] = "message";
            part["kind"] = "text";
        }
        var request = new HttpRequestMessage(HttpMethod.Post,
            agentRoute ? AdapterConstants.ChainAgentRuntimePath("mock") : AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = messageId,
                method = v03
                    ? streaming ? "message/stream" : "message/send"
                    : streaming ? "SendStreamingMessage" : "SendMessage",
                @params = new { message }
            })
        };
        if (!v03)
        {
            request.Headers.Add("A2A-Version", "1.0");
        }
        return request;
    }

    private sealed class CitationOnlyInvoker : ICopilotStudioInvoker
    {
        public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
            string prompt, A2ARequestMetadata metadata,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new CopilotInvocationUpdate(string.Empty, "conversation", "response")
            {
                Citations = Sample()
            };
        }
    }
}
