using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using Microsoft.Agents.Core.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class PlanUpdateTests
{
    [Theory]
    [InlineData("/.well-known/agent-card.json", "1.0")]
    [InlineData("/.well-known/agent-card.json", "0.3")]
    [InlineData("/a2a-agents/mock/.well-known/agent-card.json", "0.3")]
    public async Task CardsAdvertiseOptionalPlanExtension(string path, string version)
    {
        using var factory = new A2AAdapterFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("A2A-Version", version);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var extension = Assert.Single(json.RootElement.GetProperty("capabilities").GetProperty("extensions")
            .EnumerateArray(), extension => extension.GetProperty("uri").GetString() == AgentPlanUpdates.ExtensionUri);
        Assert.False(extension.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task BrowserPreflightAllowsNegotiation()
    {
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Adapter:AllowedOrigins:0", "http://localhost:5173"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, AdapterConstants.RuntimePath);
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "A2A-Extensions,X-A2A-Extensions,Content-Type");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        Assert.Contains("a2a-extensions", allowed);
        Assert.Contains("x-a2a-extensions", allowed);
    }

    [Fact]
    public async Task PlanArrivesBeforeBackendProducesAnotherUpdate()
    {
        var invoker = new ControlledInvoker();
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICopilotStudioInvoker>();
                services.AddSingleton<ICopilotStudioInvoker>(invoker);
            }));
        using var client = factory.CreateClient();
        using var request = Request("immediate-plan", false, false, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var received = false;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.Contains(AgentPlanUpdates.ExtensionUri, StringComparison.Ordinal)) continue;
                received = true;
                break;
            }
            Assert.True(received, "The plan must arrive while the backend is still blocked.");
            Assert.False(invoker.Release.Task.IsCompleted);
            invoker.Release.TrySetResult();
            await reader.ReadToEndAsync(timeout.Token);
        }
        finally
        {
            invoker.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task PartialAnswerIsFlushedBeforeAStreamFailure()
    {
        var invoker = new ControlledInvoker { Fail = true };
        invoker.Release.SetResult();
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICopilotStudioInvoker>();
                services.AddSingleton<ICopilotStudioInvoker>(invoker);
            }));
        using var client = factory.CreateClient();
        using var request = Request("failed-plan", false, false, true);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Partial answer", body);
        Assert.Contains("TASK_STATE_FAILED", body);
        Assert.DoesNotContain("TASK_STATE_COMPLETED", body);
        Assert.True(body.IndexOf("Partial answer", StringComparison.Ordinal) <
            body.IndexOf("TASK_STATE_FAILED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NegotiatedEventsSurviveReplayAndLateFinalPlan(bool legacy, bool agentRoute)
    {
        using var factory = new A2AAdapterFactory();
        using var client = factory.CreateClient();
        string[]? originalIds = null;
        foreach (var active in new[] { false, true, false, true })
        {
            using var request = Request("plan-message", legacy, agentRoute, active);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            Assert.Equal(active, response.Headers.Contains(legacy ? AgentPlanUpdates.LegacyHeader : AgentPlanUpdates.Header));
            var body = await response.Content.ReadAsStringAsync();
            var plans = new List<AgentPlanUpdate>();
            var summaries = new List<string>();
            var answer = "";
            var completed = false;
            foreach (var payload in body.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal)))
            {
                using var document = JsonDocument.Parse(payload["data: ".Length..]);
                var root = document.RootElement;
                Assert.False(root.TryGetProperty("error", out _), payload);
                var result = root.GetProperty("result");
                var artifactEvent = result.TryGetProperty("artifactUpdate", out var artifact) ? artifact : result;
                if (artifactEvent.TryGetProperty("artifact", out artifact))
                {
                    foreach (var part in artifact.GetProperty("parts").EnumerateArray())
                    {
                        Assert.False(part.TryGetProperty("data", out var data) &&
                            data.TryGetProperty(AgentPlanUpdates.ExtensionUri, out _));
                        if (part.TryGetProperty("text", out var text)) answer += text.GetString();
                    }
                }
                var statusEvent = result.TryGetProperty("statusUpdate", out var statusUpdate) ? statusUpdate : result;
                if (!statusEvent.TryGetProperty("status", out var status)) continue;
                if (status.GetProperty("state").GetString() is "completed" or "TASK_STATE_COMPLETED")
                {
                    Assert.Equal(5, summaries.Count);
                    completed = true;
                    if (legacy) Assert.True(statusEvent.GetProperty("final").GetBoolean());
                }
                if (!status.TryGetProperty("message", out var message)) continue;
                Assert.False(completed);
                Assert.Equal(legacy ? "working" : "TASK_STATE_WORKING", status.GetProperty("state").GetString());
                if (legacy) Assert.False(statusEvent.GetProperty("final").GetBoolean());
                foreach (var part in message.GetProperty("parts").EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text)) summaries.Add(text.GetString()!);
                    if (part.TryGetProperty("data", out var data))
                    {
                        var plan = AgentPlanUpdates.ReadContainer(data)!;
                        Assert.NotNull(plan);
                        Assert.Equal(plans.Count + 1, plan.Sequence);
                        Assert.DoesNotContain("mock-secret", data.GetRawText());
                        if (plan.Value.GetProperty("isFinalPlan").GetBoolean())
                        {
                            Assert.Contains("Here is the mock research answer.", answer);
                        }
                        plans.Add(plan);
                    }
                }
            }
            Assert.True(completed);
            Assert.Equal("Responding agent: Mock Copilot Studio\n\nHere is the mock research answer.", answer);
            Assert.Equal(active ? 5 : 0, plans.Count);
            if (active)
            {
                var ids = plans.Select(plan => plan.EventId).ToArray();
                if (originalIds is not null) Assert.Equal(originalIds, ids);
                originalIds = ids;
                Assert.Equal("DynamicPlanStepBindUpdate", plans[2].Name);
                Assert.Equal("Example research question",
                    plans[2].Value.GetProperty("arguments").GetProperty("search_query").GetString());
                Assert.True(plans[2].DetailsFiltered);
            }
        }
        using var nonstreaming = Request("plan-message", legacy, agentRoute, true, streaming: false);
        using var final = await client.SendAsync(nonstreaming);
        var finalBody = await final.Content.ReadAsStringAsync();
        Assert.Contains("Here is the mock research answer.", finalBody);
        Assert.DoesNotContain(AgentPlanUpdates.ExtensionUri, finalBody);
        Assert.DoesNotContain("Plan updated.", finalBody);
    }

    [Fact]
    public void DetailsAreBoundedAndRedactedWithoutLosingCorrelation()
    {
        var plan = Activity(new
        {
            planIdentifier = "plan-1", stepId = "step-1", thought = "private reasoning",
            arguments = new { password = "sensitive", query = "Contact person@example.org Bearer abc123" },
            observation = new
            {
                accessToken = "abc", text = new string('x', 20000),
                url = "https://user:password@example.org/record?secret=abc#fragment"
            }
        });
        var data = plan.Value.GetRawText();
        Assert.Equal("step-1", plan.Value.GetProperty("stepId").GetString());
        Assert.DoesNotContain("sensitive", data);
        Assert.DoesNotContain("private reasoning", data);
        Assert.DoesNotContain("person@example.org", data);
        Assert.DoesNotContain("abc123", data);
        Assert.DoesNotContain("user:password", data);
        Assert.DoesNotContain("secret=abc", data);
        Assert.True(plan.DetailsFiltered);
        Assert.True(data.Length < 5000);
        Assert.Contains("truncated", data);
    }

    [Fact]
    public void InvalidEventsAreExplicitErrors()
    {
        Assert.Throws<AdapterRequestException>(() => Activity(new { planIdentifier = 42 }));
        Assert.Throws<AdapterRequestException>(() => Activity(new { planIdentifier = "plan", isFinalPlan = "yes" }));
        Assert.Throws<AdapterRequestException>(() => Activity(new { planIdentifier = "plan" }, 257));
        Assert.Throws<AdapterRequestException>(() => AgentPlanUpdates.ReadContainer(
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { [AgentPlanUpdates.ExtensionUri] = new { schemaVersion = "2" } })));
    }

    [Fact]
    public void RelayRetainsWorkingPlanDataWithoutCreatingAnAnswer()
    {
        var plan = Activity(new { planIdentifier = "plan", stepId = "step" });
        var data = AgentPlanUpdates.ToPart(plan).Data;
        var accumulator = new A2AStreamAccumulator();
        var root = JsonSerializer.SerializeToElement(new
        {
            result = new { statusUpdate = new { status = new
            {
                state = "TASK_STATE_WORKING", message = new { parts = new[] { new { data } } }
            } } }
        });
        var delta = Assert.Single(accumulator.Consume(root));
        Assert.Equal(plan.EventId, delta.PlanUpdate!.EventId);
        Assert.True(delta.IsInformative);
        Assert.False(accumulator.HasAnswer);
    }

    [Fact]
    public void AccumulatorTurnsADivergentReplacingArtifactIntoAnAnswerReplacement()
    {
        var accumulator = new A2AStreamAccumulator();
        JsonElement Artifact(string text, bool append) => JsonSerializer.SerializeToElement(new
        {
            result = new { kind = "artifact-update", append, artifact = new
            {
                artifactId = "answer", parts = new[] { new { kind = "text", text } }
            } }
        });

        Assert.Equal("Raw \uE200cite\uE201", Assert.Single(accumulator.Consume(Artifact("Raw \uE200cite\uE201", false))).Text);
        Assert.Equal(".", Assert.Single(accumulator.Consume(Artifact(".", true))).Text);
        Assert.Empty(accumulator.Consume(Artifact("Raw \uE200cite\uE201.", false)));
        var replacement = Assert.Single(accumulator.Consume(Artifact("Raw [1].", false)));

        Assert.True(replacement.ReplacesAnswer);
        Assert.Equal("Raw [1].", replacement.Text);
        Assert.Equal(1, accumulator.ReplacedCount);
    }

    internal static HttpRequestMessage Request(string id, bool legacy, bool agentRoute, bool active, bool streaming = true)
    {
        var request = CitationTests.Request(id, legacy, streaming, agentRoute);
        var part = new JsonObject { ["text"] = MockCopilotStudioInvoker.PlanPrompt };
        var message = new JsonObject
        {
            ["role"] = legacy ? "user" : "ROLE_USER",
            ["messageId"] = id, ["contextId"] = "plan-context", ["parts"] = new JsonArray(part)
        };
        if (legacy) { part["kind"] = "text"; message["kind"] = "message"; }
        request.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0", id,
            method = legacy ? streaming ? "message/stream" : "message/send"
                : streaming ? "SendStreamingMessage" : "SendMessage",
            @params = new { message }
        });
        if (active) request.Headers.Add(legacy ? AgentPlanUpdates.LegacyHeader : AgentPlanUpdates.Header, AgentPlanUpdates.ExtensionUri);
        return request;
    }

    private static AgentPlanUpdate Activity(object value, int sequence = 1) =>
        AgentPlanUpdates.ReadActivity(new Activity
        {
            Type = "event", Name = "DynamicPlanStepBindUpdate", Id = "event-1",
            Value = JsonSerializer.SerializeToElement(value)
        }, new AgentResponder("test", "Test agent"), sequence)!;

    private sealed class ControlledInvoker : ICopilotStudioInvoker
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }

        public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
            string prompt, A2ARequestMetadata metadata,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new CopilotInvocationUpdate("", metadata.ContextId, "event-1", true)
            {
                PlanUpdate = Activity(new { planIdentifier = "plan", stepId = "step" })
            };
            await Release.Task.WaitAsync(cancellationToken);
            yield return new CopilotInvocationUpdate("Partial answer", metadata.ContextId, "answer");
            if (Fail) throw new InvalidOperationException("Synthetic failure.");
        }
    }
}
