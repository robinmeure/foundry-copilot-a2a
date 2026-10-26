using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using BotActivity = Microsoft.Agents.Core.Models.Activity;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class AdaptiveCardTests
{
    private static readonly AgentResponder Specialist = new("specialist", "Research specialist");

    private static JsonElement Card(string text = "Hello") => JsonSerializer.SerializeToElement(new
    {
        type = "AdaptiveCard",
        version = "1.5",
        body = new object[] { new { type = "TextBlock", text } }
    });

    private static AdaptiveCardBundle Bundle(string id = "c1", int revision = 1, string text = "Hello") =>
        new([new AdaptiveCardItem(id, revision, Card(text), text, Specialist)]);

    private static BotActivity CardActivity(object? content, string? text = null, string? name = null) => new()
    {
        Type = "message",
        Id = "a1",
        Text = text,
        Name = name,
        Attachments =
        [
            new Attachment { ContentType = AgentAdaptiveCards.CardContentType, Content = content }
        ]
    };

    [Fact]
    public void ActivityAttachmentBecomesCardWithDerivedFallback()
    {
        var bundle = CopilotStudioAdaptiveCards.Read(CardActivity(Card("Order shipped")), Specialist);

        var card = Assert.Single(Assert.IsType<AdaptiveCardBundle>(bundle).Cards);
        Assert.Equal("Order shipped", card.FallbackText);
        Assert.Equal(Specialist, card.OriginatingAgent);
    }

    [Fact]
    public void MalformedOrUnsafeCardsAreSkippedSoTheTextAnswerSurvives()
    {
        Assert.Null(CopilotStudioAdaptiveCards.Read(CardActivity(new { type = "NotACard" }), Specialist));
        Assert.Null(CopilotStudioAdaptiveCards.Read(
            CardActivity(JsonSerializer.SerializeToElement(new { type = "AdaptiveCard", body = Array.Empty<object>() })),
            Specialist));
    }

    [Fact]
    public void ConnectionManagerCardIsNotTreatedAsDisplayCard()
    {
        Assert.Null(CopilotStudioAdaptiveCards.Read(
            CardActivity(Card(), name: "connectors/connectionManagerCard"), Specialist));
    }

    [Fact]
    public void LimitsAreEnforced()
    {
        var oversized = JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard",
            body = new object[] { new { type = "TextBlock", text = new string('x', AgentAdaptiveCards.MaxCardBytes) } }
        });
        Assert.Throws<AdaptiveCardFormatException>(() => AgentAdaptiveCards.ValidateContent(oversized));

        var tooMany = new AdaptiveCardBundle(Enumerable.Range(0, 6)
            .Select(i => new AdaptiveCardItem($"c{i}", 1, Card(), "x")).ToArray());
        Assert.Throws<AdaptiveCardFormatException>(() => AgentAdaptiveCards.Validate(tooMany));

        JsonNode deep = new JsonObject { ["type"] = "AdaptiveCard" };
        var cursor = (JsonObject)deep;
        for (var i = 0; i < 40; i++)
        {
            var next = new JsonObject();
            cursor["child"] = next;
            cursor = next;
        }
        using var document = JsonDocument.Parse(deep.ToJsonString());
        Assert.Throws<AdaptiveCardFormatException>(
            () => AgentAdaptiveCards.ValidateContent(document.RootElement));
    }

    [Fact]
    public void MergeKeepsOrderAndPrefersHigherRevision()
    {
        var merged = AgentAdaptiveCards.Merge(Bundle("a"), Bundle("b"))!;
        merged = AgentAdaptiveCards.Merge(merged, Bundle("a", 2, "Updated"))!;
        merged = AgentAdaptiveCards.Merge(merged, Bundle("b", 1, "Stale again"))!;

        Assert.Equal(["a", "b"], merged.Cards.Select(card => card.Id));
        Assert.Equal("Updated", merged.Cards[0].FallbackText);
        Assert.Equal("Hello", merged.Cards[1].FallbackText);
    }

    [Fact]
    public void EnvelopeRoundTripsThroughTheDataPart()
    {
        var content = AgentAdaptiveCards.ToContent(Bundle());
        var part = Assert.IsType<A2A.Part>(content.RawRepresentation);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(part.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var read = AgentAdaptiveCards.ReadContainer(document.RootElement)!;

        Assert.Equal("c1", Assert.Single(read.Cards).Id);
        Assert.Equal("Hello", read.Cards[0].FallbackText);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"2","cards":[]}""")]
    [InlineData("""{"schemaVersion":"1","cards":null}""")]
    [InlineData("""{"schemaVersion":"1","cards":[{"id":"x","content":{"type":"Other"},"fallbackText":"f"}]}""")]
    [InlineData("""{"schemaVersion":"1","cards":[{"id":"x","content":{"type":"AdaptiveCard"}}]}""")]
    public void MalformedRecognizedEnvelopeIsRejected(string json)
    {
        using var document = JsonDocument.Parse(new JsonObject
        {
            [AgentAdaptiveCards.ExtensionUri] = JsonNode.Parse(json)
        }.ToJsonString());
        Assert.Throws<AdaptiveCardFormatException>(() => AgentAdaptiveCards.ReadContainer(document.RootElement));
    }

    [Fact]
    public void UpstreamAnswerPreservesRecognisedCardsAndDropsMalformedOnes()
    {
        var content = AgentAdaptiveCards.ToContent(Bundle());
        var data = JsonSerializer.SerializeToNode(Assert.IsType<A2A.Part>(content.RawRepresentation),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var broken = new JsonObject
        {
            ["data"] = new JsonObject
            {
                [AgentAdaptiveCards.ExtensionUri] = new JsonObject { ["schemaVersion"] = "9" }
            }
        };
        using var document = JsonDocument.Parse(new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = "Answer." }, data, broken)
                }
            }
        }.ToJsonString());

        var answer = A2AResponseText.Read(document.RootElement);

        Assert.Equal("Answer.", answer.Text);
        Assert.Equal("c1", Assert.Single(answer.Cards!.Cards).Id);
    }

    [Fact]
    public void UnrelatedStructuredDataRemainsOptional()
    {
        using var document = JsonDocument.Parse("""{"other":{"x":1}}""");
        Assert.Null(AgentAdaptiveCards.ReadContainer(document.RootElement));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task FlagControlsWhetherTheEnvelopeReachesTheWire(bool v03, bool enabled)
    {
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Adapter:AdaptiveCardsEnabled", enabled ? "true" : "false"));
        using var client = factory.CreateClient();
        using var request = CardRequest(v03);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Here is a mock card.", body);
        Assert.Equal(enabled, body.Contains(AgentAdaptiveCards.ExtensionUri));
        if (enabled)
        {
            Assert.Contains("Mock order status: Order A-1001", body);
            Assert.Contains(AgentAdaptiveCards.CardContentType, body);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentCardAdvertisesTheExtensionOnlyWhenEnabled(bool enabled)
    {
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Adapter:AdaptiveCardsEnabled", enabled ? "true" : "false"));
        using var client = factory.CreateClient();

        foreach (var path in new[]
                 {
                     "/.well-known/agent-card.json",
                     $"{AdapterConstants.ChainAgentsPath}/mock/.well-known/agent-card.json"
                 })
        {
            var body = await client.GetStringAsync(path);
            Assert.Equal(enabled, body.Contains(AgentAdaptiveCards.ExtensionUri));
            Assert.Contains(AgentCitations.ExtensionUri, body);
        }
    }

    private static HttpRequestMessage CardRequest(bool v03)
    {
        var part = new JsonObject { ["text"] = MockCopilotStudioInvoker.CardPrompt };
        var message = new JsonObject
        {
            ["role"] = v03 ? "user" : "ROLE_USER",
            ["messageId"] = Guid.NewGuid().ToString("N"),
            ["contextId"] = "card-conversation",
            ["parts"] = new JsonArray(part)
        };
        if (v03)
        {
            message["kind"] = "message";
            part["kind"] = "text";
        }
        var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = "1",
                method = v03 ? "message/send" : "SendMessage",
                @params = new { message }
            })
        };
        if (!v03)
        {
            request.Headers.Add("A2A-Version", "1.0");
        }
        return request;
    }
}
