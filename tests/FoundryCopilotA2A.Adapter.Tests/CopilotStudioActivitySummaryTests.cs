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
