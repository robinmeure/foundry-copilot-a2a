using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class ResponderIdentityTests
{
    private const string Header = "Responding agent: Mock Copilot Studio\n\n";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task EveryTurnAndReplayExposesIdentity(
        bool v03, bool streaming, bool agentRoute)
    {
        using var factory = new A2AAdapterFactory();
        using var client = factory.CreateClient();
        var invoker = Assert.IsType<MockCopilotStudioInvoker>(
            factory.Services.GetRequiredService<ICopilotStudioInvoker>());

        foreach (var messageId in new[] { "first-turn", "follow-up" })
        {
            var original = await SendAsync(client, messageId, v03, streaming, agentRoute);
            AssertResponse(original, streaming);

            var replay = await SendAsync(client, messageId, v03, streaming, agentRoute);
            Assert.Equal(original, replay);

            var alternateReplay = await SendAsync(client, messageId, !v03, !streaming, agentRoute);
            AssertResponse(alternateReplay, !streaming);
        }

        Assert.Equal(2, invoker.InvocationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEmptyAnswerDoesNotBecomeASuccessfulIdentityHeader(bool streaming)
    {
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICopilotStudioInvoker>();
                services.AddSingleton<ICopilotStudioInvoker, EmptyAnswerInvoker>();
            }));
        using var client = factory.CreateClient();
        using var request = CreateRequest("empty-answer", false, streaming, false);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Responding agent:", body);
        if (streaming)
        {
            Assert.Contains("no text response", body, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            using var document = JsonDocument.Parse(body);
            Assert.False(document.RootElement.TryGetProperty("result", out _));
            Assert.Equal(
                (int)A2AErrorCode.InvalidAgentResponse,
                document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    private static void AssertResponse(ResponsePart[] parts, bool streaming)
    {
        Assert.NotEmpty(parts);
        Assert.All(parts, part =>
        {
            Assert.Equal("mock", part.AgentId);
            Assert.Equal("Mock Copilot Studio", part.AgentName);
        });
        var answers = parts.Where(part => !part.IsInformative).ToArray();
        Assert.Equal(
            Header + string.Concat(MockCopilotStudioInvoker.StreamedAnswerDeltas),
            string.Concat(answers.Select(part => part.Text)));
        Assert.Equal(streaming ? MockCopilotStudioInvoker.StreamedAnswerDeltas.Length : 1, answers.Length);
        if (streaming)
        {
            Assert.Equal(
                Header + "Generating plan...",
                Assert.Single(parts, part => part.IsInformative).Text);
        }
        else
        {
            Assert.DoesNotContain(parts, part => part.IsInformative);
        }
    }

    private static async Task<ResponsePart[]> SendAsync(
        HttpClient client, string messageId, bool v03, bool streaming, bool agentRoute)
    {
        using var request = CreateRequest(messageId, v03, streaming, agentRoute);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var payloads = streaming
            ? body.Split('\n').Select(line => line.Trim())
                .Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
                .Select(line => line["data: ".Length..])
            : [body];
        var parts = new List<ResponsePart>();
        foreach (var payload in payloads)
        {
            using var document = JsonDocument.Parse(payload);
            Assert.False(document.RootElement.TryGetProperty("error", out _), payload);
            var result = document.RootElement.GetProperty("result");
            JsonElement container;
            if (result.TryGetProperty("artifactUpdate", out var update))
            {
                container = update.GetProperty("artifact");
            }
            else if (result.TryGetProperty("artifact", out var artifact))
            {
                container = artifact;
            }
            else
            {
                container = result.TryGetProperty("message", out var message) ? message : result;
            }

            if (!container.TryGetProperty("parts", out var contentParts))
            {
                continue;
            }

            foreach (var part in contentParts.EnumerateArray())
            {
                var metadata = part.GetProperty("metadata");
                parts.Add(new ResponsePart(
                    part.GetProperty("text").GetString()!,
                    metadata.GetProperty("agentId").GetString()!,
                    metadata.GetProperty("agentName").GetString()!,
                    metadata.TryGetProperty("isInformative", out var informative) &&
                    informative.GetBoolean()));
            }
        }
        return parts.ToArray();
    }

    private static HttpRequestMessage CreateRequest(
        string messageId, bool v03, bool streaming, bool agentRoute)
    {
        var part = new JsonObject { ["text"] = MockCopilotStudioInvoker.StreamProgressPrompt };
        var message = new JsonObject
        {
            ["role"] = v03 ? "user" : "ROLE_USER",
            ["parts"] = new JsonArray(part),
            ["messageId"] = messageId,
            ["contextId"] = "identity-conversation",
            ["metadata"] = new JsonObject
            {
                ["agentId"] = "spoofed",
                ["agentName"] = "Untrusted name"
            }
        };
        if (v03)
        {
            message["kind"] = "message";
            part["kind"] = "text";
        }

        var method = v03
            ? (streaming ? "message/stream" : "message/send")
            : (streaming ? "SendStreamingMessage" : "SendMessage");
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            agentRoute ? AdapterConstants.ChainAgentRuntimePath("mock") : AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = messageId,
                method,
                @params = new { message }
            })
        };
        if (!v03)
        {
            request.Headers.Add("A2A-Version", "1.0");
        }
        request.Headers.Add(
            AdapterConstants.AgentHeaderName, agentRoute ? "mock-failure" : "MOCK");
        return request;
    }

    private sealed record ResponsePart(string Text, string AgentId, string AgentName, bool IsInformative);

    private sealed class EmptyAnswerInvoker : ICopilotStudioInvoker
    {
        public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
            string prompt,
            A2ARequestMetadata metadata,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new(string.Empty, metadata.ContextId, "empty");
        }
    }
}
