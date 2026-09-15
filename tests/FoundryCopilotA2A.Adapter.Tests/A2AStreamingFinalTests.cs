using System.Net.Http.Json;
using System.Text.Json;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class A2AStreamingFinalTests(A2AAdapterFactory factory)
    : IClassFixture<A2AAdapterFactory>
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("rejected")]
    public void TerminalStatusUpdateIsMarkedFinal(string state)
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"kind\":\"status-update\"," +
                   "\"status\":{\"state\":\"" + state + "\"},\"final\":false}}";

        Assert.True(A2AFinalStatus.TryMarkFinal(json, out var rewritten));
        Assert.True(JsonDocument.Parse(rewritten).RootElement
            .GetProperty("result").GetProperty("final").GetBoolean());
    }

    /// <summary>
    /// A stream that is still running must not be marked final, otherwise a client stops reading
    /// before the answer arrives.
    /// </summary>
    [Theory]
    [InlineData("working")]
    [InlineData("submitted")]
    public void NonTerminalStatusUpdateIsLeftAlone(string state)
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"kind\":\"status-update\"," +
                   "\"status\":{\"state\":\"" + state + "\"},\"final\":false}}";

        Assert.False(A2AFinalStatus.TryMarkFinal(json, out _));
    }

    /// <summary>A2A 1.0 has no "final" field, so its payloads must pass through untouched.</summary>
    [Fact]
    public void ProtocolOneZeroStatusUpdateIsLeftAlone()
    {
        var json = """
            {"jsonrpc":"2.0","id":"1","result":{"statusUpdate":{"status":{"state":"TASK_STATE_COMPLETED"}}}}
            """;

        Assert.False(A2AFinalStatus.TryMarkFinal(json, out _));
    }

    [Fact]
    public void ArtifactUpdatesAndNonDataLinesAreLeftAlone()
    {
        const string artifact =
            """data: {"jsonrpc":"2.0","result":{"kind":"artifact-update","lastChunk":true}}""" + "\n";
        Assert.Equal(artifact, A2AFinalStatus.TransformLine(artifact));
        Assert.Equal("\n", A2AFinalStatus.TransformLine("\n"));
        Assert.Equal("event: message\n", A2AFinalStatus.TransformLine("event: message\n"));
    }

    [Fact]
    public void TransformPreservesServerSentEventFraming()
    {
        const string line =
            """data: {"jsonrpc":"2.0","result":{"kind":"status-update","status":{"state":"completed"},"final":false}}""" +
            "\r\n";

        var transformed = A2AFinalStatus.TransformLine(line);

        Assert.StartsWith("data: {", transformed);
        Assert.EndsWith("\r\n", transformed);
        Assert.Contains("\"final\":true", transformed);
    }

    /// <summary>Untouched fields must not be re-escaped, so timestamps keep their '+' offset.</summary>
    [Fact]
    public void RewriteLeavesOtherFieldsByteFaithful()
    {
        const string json =
            """{"jsonrpc":"2.0","result":{"kind":"status-update","status":{"state":"completed","timestamp":"2026-09-15T11:49:14+00:00"},"final":false}}""";

        Assert.True(A2AFinalStatus.TryMarkFinal(json, out var rewritten));
        Assert.Contains("2026-09-15T11:49:14+00:00", rewritten);
        Assert.DoesNotContain("\\u002B", rewritten);
    }

    [Fact]
    public async Task StreamedTerminalStatusUpdateReachesTheClientAsFinal()
    {
        var content = await StreamAsync("message/stream", "0.3");

        var terminal = DataPayloads(content)
            .Select(payload => JsonDocument.Parse(payload).RootElement)
            .Where(root => root.TryGetProperty("result", out var result) &&
                           result.TryGetProperty("kind", out var kind) &&
                           kind.GetString() == "status-update" &&
                           result.GetProperty("status").GetProperty("state").GetString() == "completed")
            .ToList();

        var final = Assert.Single(terminal);
        Assert.True(final.GetProperty("result").GetProperty("final").GetBoolean());
    }

    /// <summary>The answer itself must survive the rewrite unchanged.</summary>
    [Fact]
    public async Task StreamingStillDeliversTheWholeAnswerInOrder()
    {
        var content = await StreamAsync(
            "message/stream", "0.3", MockCopilotStudioInvoker.StreamProgressPrompt);

        var answer = string.Concat(DataPayloads(content)
            .Select(payload => JsonDocument.Parse(payload).RootElement)
            .Where(root => root.GetProperty("result").TryGetProperty("kind", out var kind) &&
                           kind.GetString() == "artifact-update")
            .SelectMany(root => root.GetProperty("result").GetProperty("artifact")
                .GetProperty("parts").EnumerateArray())
            .Where(part => !part.TryGetProperty("metadata", out var metadata) ||
                           !metadata.TryGetProperty("isInformative", out var informative) ||
                           !informative.GetBoolean())
            .Select(part => part.GetProperty("text").GetString()));

        Assert.Contains(string.Concat(MockCopilotStudioInvoker.StreamedAnswerDeltas), answer);
    }

    [Fact]
    public async Task ProtocolOneZeroStreamIsUnaffected()
    {
        var content = await StreamAsync("SendStreamingMessage", "1.0");

        Assert.Contains("TASK_STATE_COMPLETED", content);
        Assert.DoesNotContain("\"final\"", content);
    }

    private async Task<string> StreamAsync(string method, string version, string? prompt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = new
                    {
                        message = new
                        {
                            kind = "message",
                            role = version == "1.0" ? "ROLE_USER" : "user",
                            parts = new[] { new { kind = "text", text = prompt ?? "final marker" } },
                            messageId = $"m-{id}",
                            contextId = $"c-{id}"
                        }
                    }
                },
                options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        request.Headers.Add("A2A-Version", version);

        using var response = await factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    private static IEnumerable<string> DataPayloads(string content) => content
        .Split('\n')
        .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
        .Select(line => line["data:".Length..].Trim())
        .Where(payload => payload.Length > 0);
}
