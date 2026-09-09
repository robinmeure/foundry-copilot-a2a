using System.Net;
using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class CitadelSmokeTestTests
{
    [Theory]
    [InlineData("*", true, true)]
    [InlineData("*", false, false)]
    [InlineData("http://localhost:5137", true, true)]
    [InlineData("http://localhost:5137", false, true)]
    [InlineData("https://other.example", true, false)]
    [InlineData("*,http://localhost:5137", true, false)]
    public void WildcardCorsIsAcceptedOnlyForPublicDiscovery(string allowedOrigin, bool publicCard, bool expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Access-Control-Allow-Origin", allowedOrigin);
        var error = Record.Exception(() =>
            CitadelSmokeTest.RequireCors(response, "http://localhost:5137", publicCard));
        if (expected)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.IsType<CliException>(error);
        }
    }

    [Fact]
    public void PublicDiscoveryCannotCombineWildcardCorsAndCredentials()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Credentials", "true");
        Assert.Throws<CliException>(() =>
            CitadelSmokeTest.RequireCors(response, "http://localhost:5137", publicCard: true));
    }

    [Fact]
    public void StreamingAnswerIgnoresProgressAndAppliesArtifactReplacementAndAppend()
    {
        var answer = new StringBuilder();
        CitadelSmokeTest.ConsumeEvent(
            """{"result":{"artifactUpdate":{"artifact":{"parts":[{"text":"Generating plan...","metadata":{"isInformative":true}}]}}}}""",
            answer);
        Assert.Empty(answer.ToString());
        CitadelSmokeTest.ConsumeEvent(
            """{"result":{"artifactUpdate":{"artifact":{"parts":[{"text":"first"}]}}}}""", answer);
        CitadelSmokeTest.ConsumeEvent(
            """{"result":{"artifactUpdate":{"artifact":{"parts":[{"text":"second"}]},"append":false}}}""", answer);
        CitadelSmokeTest.ConsumeEvent(
            """{"result":{"artifactUpdate":{"artifact":{"parts":[{"text":" chunk"}]},"append":true}}}""", answer);
        Assert.Equal("second chunk", answer.ToString());
    }

    [Theory]
    [InlineData("""{"error":{"code":-32603,"message":"backend failed"}}""")]
    [InlineData("""{"result":{"statusUpdate":{"status":{"state":"TASK_STATE_FAILED"}}}}""")]
    public void ProtocolFailuresAfterAnAnswerAreNotSuccessfulSmokeTests(string data)
    {
        var answer = new StringBuilder("partial answer");
        Assert.Throws<CliException>(() => CitadelSmokeTest.ConsumeEvent(data, answer));
    }

    [Theory]
    [InlineData("on_behalf_of", true, 0, "direct", true, false)]
    [InlineData("client_credentials", true, 2, "direct", true, false)]
    [InlineData("on_behalf_of", false, 2, "direct", true, false)]
    [InlineData("on_behalf_of", true, 0, "specialist", true, false)]
    [InlineData("on_behalf_of", false, 2, "specialist", true, false)]
    [InlineData("on_behalf_of", true, 0, "chain-foundry", true, false)]
    [InlineData("on_behalf_of", true, 0, "chain-copilotStudio", true, false)]
    [InlineData("on_behalf_of", true, 2, "chain-copilotStudio", false, false)]
    [InlineData("on_behalf_of", true, 2, "direct", true, true)]
    [InlineData("on_behalf_of", true, 2, "chain-foundry", true, true)]
    public async Task GatewaySmokeUsesTheDelegatedTokenAndRequiresGovernedDiscovery(
        string authFlow, bool governedCard, int expectedExit, string mode, bool observedCallback, bool truncated)
    {
        const string baseUrl = "https://gateway.example/copilot-studio-local";
        const string origin = "http://localhost:5137";
        var chain = mode.StartsWith("chain-", StringComparison.Ordinal);
        var agentId = chain ? "orchestrator" : "reverser";
        var runtimePath = mode == "specialist" ? "/a2a-agents/reverser/a2a" : "/a2a/copilot-studio";
        var variable = $"CITADEL_TEST_TOKEN_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "user-delegated-token");
        var authenticatedRuntimeCalls = 0;
        using var handler = new CitadelTestHandler(async request =>
        {
            Assert.StartsWith(baseUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal(origin, Assert.Single(request.Headers.GetValues("Origin")));
            var path = request.RequestUri.AbsolutePath;
            HttpResponseMessage response;
            if (request.Method == HttpMethod.Options)
            {
                Assert.Null(request.Headers.Authorization);
                response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("Access-Control-Allow-Methods", "GET,POST");
                response.Headers.Add("Access-Control-Allow-Headers",
                    "authorization,content-type,a2a-version,x-copilot-agent,x-a2a-chain-target");
            }
            else if (path.EndsWith("/.well-known/agent-card.json", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);
                var advertisedUrl = $"{(governedCard ? baseUrl : "https://tunnel.example")}{runtimePath}";
                response = mode == "specialist"
                    ? CitadelCommandsTests.Json(new { url = advertisedUrl, protocolVersion = "0.3.0" })
                    : CitadelCommandsTests.Json(new
                    {
                        supportedInterfaces = new[] { new { url = advertisedUrl } }
                    });
            }
            else if (path.EndsWith("/api/agents", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);
                response = CitadelCommandsTests.Json(new
                {
                    agents = new[]
                    {
                        new
                        {
                            id = agentId, supported = true,
                            provider = chain ? mode["chain-".Length..] : "copilotStudio",
                            canOrchestrate = chain,
                            chainTargets = chain ? new[] { "reverser" } : []
                        }
                    }
                });
            }
            else if (request.Headers.Authorization is null)
            {
                response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            else if (request.Method == HttpMethod.Post)
            {
                authenticatedRuntimeCalls++;
                Assert.Equal("user-delegated-token", request.Headers.Authorization.Parameter);
                Assert.Equal(baseUrl + runtimePath, request.RequestUri.AbsoluteUri);
                Assert.Equal(agentId, Assert.Single(request.Headers.GetValues("X-Copilot-Agent")));
                if (chain)
                {
                    Assert.Equal("reverser", Assert.Single(request.Headers.GetValues("X-A2A-Chain-Target")));
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("SendStreamingMessage", body.RootElement.GetProperty("method").GetString());
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"result\":{\"artifactUpdate\":{\"artifact\":{\"parts\":[{\"text\":\"ledatic\"}]}}}}\n\n",
                        Encoding.UTF8, "text/event-stream")
                };
                response.Headers.Add("X-Trace-Id", new string('a', 32));
                response.Headers.Add("Access-Control-Expose-Headers", "X-Trace-Id,X-Correlation-ID");
            }
            else
            {
                Assert.Equal("user-delegated-token", request.Headers.Authorization.Parameter);
                response = CitadelCommandsTests.Json(new
                {
                    complete = true,
                    truncated,
                    spans = new[]
                    {
                        new
                        {
                            name = "entra.obo.acquire_token",
                            status = "Ok",
                            attributes = new Dictionary<string, string> { ["auth.flow"] = authFlow }
                        },
                        new
                        {
                            name = observedCallback ? "execute_tool Reverser" : "invoke_workflow orchestrator->reverser",
                            status = "Ok",
                            attributes = observedCallback
                                ? new Dictionary<string, string> { ["copilot_studio.agent.id"] = "reverser" }
                                : new Dictionary<string, string> { ["a2a.chain.target_agent"] = "reverser" }
                        }
                    }
                });
            }
            response.Headers.Add("Access-Control-Allow-Origin",
                path.EndsWith("/.well-known/agent-card.json", StringComparison.Ordinal) ? "*" : origin);
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var output = new StringWriter();
        var error = new StringWriter();
        var application = new CliApplication(new CliContext(output, error, new ProcessRunner(), httpClient));
        try
        {
            List<string> arguments =
                [
                    "test-citadel", "--base-url", baseUrl, "--allowed-origin", origin,
                    "--bearer-token-env", variable, "--agent-id", agentId,
                    "--prompt", "citadel", "--expected-output-pattern", "^ledatic$", "--require-obo"
                ];
            if (mode == "specialist")
            {
                arguments.Add("--specialist-route");
            }
            if (chain)
            {
                arguments.AddRange(["--chain-target", "reverser", "--require-native-callback"]);
            }
            var exitCode = await application.RunAsync(arguments.ToArray(), CancellationToken.None);
            Assert.Equal(expectedExit, exitCode);
            Assert.Equal(governedCard ? 1 : 0, authenticatedRuntimeCalls);
            Assert.Equal(expectedExit == 0, output.ToString().Contains("round trip passed", StringComparison.Ordinal));
            Assert.DoesNotContain("user-delegated-token", output.ToString() + error);
            if (governedCard)
            {
                Assert.Contains($"Trace: {new string('a', 32)}", output.ToString());
            }
            if (truncated)
            {
                Assert.Contains("truncated", error.ToString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
