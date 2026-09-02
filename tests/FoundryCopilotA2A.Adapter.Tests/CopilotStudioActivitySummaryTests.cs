using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using BotActivity = Microsoft.Agents.Core.Models.Activity;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class CopilotStudioActivitySummaryTests
{
    [Fact]
    public void EmptyCollectionExplainsThatNoActivitiesWereReturned()
    {
        var summary = new CopilotStudioActivitySummary();

        var message = summary.CreateEmptyResponseMessage();

        Assert.Contains("without returning any activities", message);
        Assert.Contains("final text response", message);
    }

    [Fact]
    public void NonMessageActivitiesAreCountedWithoutExposingTheirPayload()
    {
        var summary = new CopilotStudioActivitySummary();
        summary.Observe(new BotActivity { Type = "typing", Text = "sensitive" }, false);
        summary.Observe(new BotActivity { Type = "custom-secret-type" }, false);

        var message = summary.CreateEmptyResponseMessage();

        Assert.Equal(2, summary.ActivityCount);
        Assert.Equal("other,typing", summary.ActivityTypes);
        Assert.Contains("none was a message", message);
        Assert.DoesNotContain("sensitive", message);
        Assert.DoesNotContain("custom-secret-type", message);
    }

    [Fact]
    public void BlankMessageIsNotTreatedAsAResponse()
    {
        var summary = new CopilotStudioActivitySummary();
        summary.Observe(new BotActivity { Type = "message", Text = "  " }, false);

        Assert.Equal(1, summary.MessageCount);
        Assert.Equal(0, summary.TextMessageCount);
        Assert.False(summary.HasTextResponse);
        Assert.Contains("but no text", summary.CreateEmptyResponseMessage());
    }

    [Fact]
    public void AttachmentOnlyMessageReportsItsShape()
    {
        var summary = new CopilotStudioActivitySummary();
        summary.Observe(
            new BotActivity
            {
                Type = "message",
                Attachments = [new Attachment { ContentType = "application/vnd.example" }]
            },
            false);

        var message = summary.CreateEmptyResponseMessage();

        Assert.Equal(1, summary.AttachmentCount);
        Assert.Contains("with 1 attachment, but no text", message);
    }

    [Fact]
    public void ConnectionManagerCardProvidesAUsableTextResponse()
    {
        var card = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "AdaptiveCard",
              "version": "1.3",
              "body": [
                {
                  "type": "TextBlock",
                  "text": "Let's get you connected first. [Open connection manager](https://example.test/connections)",
                  "wrap": true
                },
                {
                  "type": "TextBlock",
                  "text": "Once the connection is ready, retry your request.",
                  "wrap": true
                }
              ]
            }
            """);
        var activity = new BotActivity
        {
            Type = "message",
            Name = "connectors/connectionManagerCard",
            Attachments =
            [
                new Attachment
                {
                    ContentType = "application/vnd.microsoft.card.adaptive",
                    Content = card
                }
            ]
        };

        var text = CopilotStudioAttachmentText.ExtractConnectionManagerCardText(activity);
        var summary = new CopilotStudioActivitySummary();
        summary.Observe(activity, oauthCardPresent: false, text, extractedFromAdaptiveCard: true);

        Assert.Equal(
            "Let's get you connected first. " +
            "[Open connection manager](https://example.test/connections)" +
            Environment.NewLine + Environment.NewLine +
            "Once the connection is ready, retry your request.",
            text);
        Assert.True(summary.HasTextResponse);
        Assert.Equal(1, summary.AdaptiveCardTextMessageCount);
    }

    [Fact]
    public void ArbitraryAdaptiveCardRemainsAnAttachmentOnlyResponse()
    {
        var activity = new BotActivity
        {
            Type = "message",
            Name = "unrelated/card",
            Attachments =
            [
                new Attachment
                {
                    ContentType = "application/vnd.microsoft.card.adaptive",
                    Content = new
                    {
                        type = "AdaptiveCard",
                        body = new[] { new { type = "TextBlock", text = "not extracted" } }
                    }
                }
            ]
        };

        var text = CopilotStudioAttachmentText.ExtractConnectionManagerCardText(activity);

        Assert.Null(text);
    }

    [Fact]
    public void InformativeTypingActivityProvidesAStreamingProgressUpdate()
    {
        var activity = new BotActivity
        {
            Type = "typing",
            Text = "Generating plan...",
            ChannelData = new { streamType = "informative", streamSequence = 1 }
        };

        var chunk = new CopilotStudioAnswerStream().Next(activity, messageText: null);

        Assert.Equal("Generating plan...", chunk?.Text);
        Assert.True(chunk?.IsInformative);
    }

    [Theory]
    [InlineData("typing", null)]
    [InlineData("event", "informative")]
    public void ActivityOutsideTheStreamingProtocolContributesNothing(
        string activityType,
        string? streamType)
    {
        var activity = new BotActivity
        {
            Type = activityType,
            Text = "Do not stream",
            ChannelData = streamType is null ? null : new { streamType }
        };

        var chunk = new CopilotStudioAnswerStream().Next(activity, messageText: null);

        Assert.Null(chunk);
    }

    /// <summary>
    /// Copilot Studio sends the answer twice: once as typing deltas and again as the final
    /// message. Forwarding both would return the answer doubled.
    /// </summary>
    [Fact]
    public void StreamedAnswerIsForwardedAsDeltasWithoutRepeatingTheFinalMessage()
    {
        const string streamId = "stream-1";
        var stream = new CopilotStudioAnswerStream();
        var deltas = new[] { "**Kamervragen**", "\n\nEr zijn", " vragen", "." };
        var answer = string.Concat(deltas);

        var emitted = new List<CopilotStudioAnswerChunk>();
        Add(emitted, stream, Typing("Generating plan...", "informative", streamId));
        foreach (var delta in deltas)
        {
            Add(emitted, stream, Typing(delta, "streaming", streamId));
        }

        Add(emitted, stream, FinalMessage(answer, streamId));

        Assert.Equal(4, stream.DeltaCount);
        Assert.Equal(1, stream.SuppressedFinalCount);
        Assert.Equal(
            "Generating plan...",
            Assert.Single(emitted, chunk => chunk.IsInformative).Text);
        Assert.Equal(
            answer,
            string.Concat(emitted.Where(chunk => !chunk.IsInformative).Select(chunk => chunk.Text)));
    }

    /// <summary>A delta can be a lone space or newline, which must survive verbatim.</summary>
    [Fact]
    public void WhitespaceOnlyDeltaIsPreservedInTheAnswer()
    {
        var stream = new CopilotStudioAnswerStream();
        var deltas = new[] { "Regel", " ", "\n", "twee" };

        var emitted = new List<CopilotStudioAnswerChunk>();
        foreach (var delta in deltas)
        {
            Add(emitted, stream, Typing(delta, "streaming", "stream-1"));
        }

        Assert.Equal("Regel \ntwee", string.Concat(emitted.Select(chunk => chunk.Text)));
    }

    /// <summary>
    /// A stream that only ever produced whitespace is not a usable answer, so it must not be
    /// reported as one. The caller decides that from the forwarded text.
    /// </summary>
    [Fact]
    public void StreamOfOnlyWhitespaceProducesNoUsableAnswerText()
    {
        var stream = new CopilotStudioAnswerStream();
        var emitted = new List<CopilotStudioAnswerChunk>();

        Add(emitted, stream, Typing(" ", "streaming", "stream-1"));
        Add(emitted, stream, Typing("\n", "streaming", "stream-1"));
        Add(emitted, stream, FinalMessage(" \n", "stream-1"), " \n");

        Assert.All(emitted, chunk => Assert.True(string.IsNullOrWhiteSpace(chunk.Text)));
    }

    /// <summary>
    /// An agent that never streams sends only a message, which must still be forwarded.
    /// </summary>
    [Fact]
    public void FinalMessageIsForwardedWhenNoDeltasPrecededIt()
    {
        var stream = new CopilotStudioAnswerStream();

        var chunk = stream.Next(FinalMessage("The whole answer.", "stream-1"), "The whole answer.");

        Assert.Equal("The whole answer.", chunk?.Text);
        Assert.False(chunk?.IsInformative);
        Assert.Equal(0, stream.SuppressedFinalCount);
    }

    /// <summary>Empty deltas carry no answer, so the final message is still the only source.</summary>
    [Fact]
    public void FinalMessageIsForwardedWhenEveryDeltaWasEmpty()
    {
        var stream = new CopilotStudioAnswerStream();
        var emitted = new List<CopilotStudioAnswerChunk>();

        Add(emitted, stream, Typing(string.Empty, "streaming", "stream-1"));
        Add(emitted, stream, FinalMessage("Recovered answer.", "stream-1"), "Recovered answer.");

        Assert.Equal(0, stream.DeltaCount);
        Assert.Equal(0, stream.SuppressedFinalCount);
        Assert.Equal("Recovered answer.", Assert.Single(emitted).Text);
    }

    [Fact]
    public void ProgressUpdatesDoNotSeparateTheAnswerTheyPrecede()
    {
        var stream = new CopilotStudioAnswerStream();
        var emitted = new List<CopilotStudioAnswerChunk>();

        Add(emitted, stream, Typing("Analyzing data...", "informative", "stream-1"));
        Add(emitted, stream, FinalMessage("Answer.", "stream-1"), "Answer.");

        Assert.Equal("Answer.", emitted.Single(chunk => !chunk.IsInformative).Text);
    }

    private static void Add(
        List<CopilotStudioAnswerChunk> emitted,
        CopilotStudioAnswerStream stream,
        BotActivity activity,
        string? messageText = null)
    {
        if (stream.Next(activity, messageText) is { } chunk)
        {
            emitted.Add(chunk);
        }
    }

    private static BotActivity Typing(string text, string streamType, string streamId) =>
        new()
        {
            Type = "typing",
            Id = $"{streamId}-{Guid.NewGuid():N}",
            Text = text,
            ChannelData = new { streamType, streamId }
        };

    private static BotActivity FinalMessage(string text, string streamId) =>
        new()
        {
            Type = "message",
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            ChannelData = new { streamType = "final", streamId }
        };

    [Fact]
    public void TextMessageProducesSuccessfulSafeTelemetry()
    {
        var summary = new CopilotStudioActivitySummary();
        summary.Observe(
            new BotActivity
            {
                Type = "message",
                Text = "do not record this",
                Attachments = [new Attachment { ContentType = "application/vnd.example" }]
            },
            false);
        using var activity = new System.Diagnostics.Activity("test").Start();

        summary.RecordTelemetry(activity, allowOAuthChallenge: false);

        Assert.True(summary.HasTextResponse);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(1, activity.GetTagItem("copilot_studio.activity.count"));
        Assert.Equal("message", activity.GetTagItem("copilot_studio.activity.types"));
        Assert.Equal(1, activity.GetTagItem("copilot_studio.message.count"));
        Assert.Equal(1, activity.GetTagItem("copilot_studio.message.text.count"));
        Assert.Equal(
            0,
            activity.GetTagItem("copilot_studio.message.adaptive_card_text.count"));
        Assert.Equal(1, activity.GetTagItem("copilot_studio.attachment.count"));
        Assert.Null(activity.GetTagItem("adapter.failure.reason"));
        Assert.DoesNotContain(
            activity.TagObjects,
            tag => string.Equals(tag.Value?.ToString(), "do not record this", StringComparison.Ordinal));
    }

    [Fact]
    public void ShapeTelemetrySurvivesTraceSanitizationWithoutMessageContent()
    {
        var summary = new CopilotStudioActivitySummary();
        summary.Observe(
            new BotActivity
            {
                Type = "message",
                Text = "do not expose this",
                Attachments = [new Attachment { ContentType = "application/vnd.example" }]
            },
            false);
        using var activity = new System.Diagnostics.Activity("test").Start();
        summary.RecordTelemetry(activity, allowOAuthChallenge: false);

        var attributes = TraceSanitizer.SanitizeAttributes(activity, "orchestrator");

        Assert.Equal("1", attributes["copilot_studio.activity.count"]);
        Assert.Equal("message", attributes["copilot_studio.activity.types"]);
        Assert.Equal("1", attributes["copilot_studio.message.count"]);
        Assert.Equal("1", attributes["copilot_studio.message.text.count"]);
        Assert.Equal(
            "0",
            attributes["copilot_studio.message.adaptive_card_text.count"]);
        Assert.Equal("1", attributes["copilot_studio.attachment.count"]);
        Assert.DoesNotContain("do not expose this", attributes.Values);
    }

    [Fact]
    public void EmptyCollectionProducesErrorTelemetry()
    {
        var summary = new CopilotStudioActivitySummary();
        using var activity = new System.Diagnostics.Activity("test").Start();

        summary.RecordTelemetry(activity, allowOAuthChallenge: false);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            typeof(CopilotStudioResponseException).FullName,
            activity.GetTagItem("error.type"));
        Assert.Contains(
            "without returning any activities",
            Assert.IsType<string>(activity.GetTagItem("adapter.failure.reason")));
    }
}
