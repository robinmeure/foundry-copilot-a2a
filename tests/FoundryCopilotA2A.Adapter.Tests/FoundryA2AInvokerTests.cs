using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class FoundryA2AInvokerTests
{
    [Fact]
    public async Task InvokeAsyncExtractsCompletedTaskArtifactText()
    {
        const string responseBody =
            """
            {
              "jsonrpc": "2.0",
              "id": "response-1",
              "result": {
                "task": {
                  "id": "task-1",
                  "contextId": "context-1",
                  "status": {
                    "state": "TASK_STATE_COMPLETED"
                  },
                  "artifacts": [
                    {
                      "artifactId": "artifact-1",
                      "parts": [
                        { "text": "First line." },
                        { "text": "Second line." }
                      ]
                    }
                  ]
                }
              }
            }
            """;
        var invoker = CreateInvoker(responseBody);

        var result = await invoker.InvokeAsync(
            "test prompt",
            new A2ARequestMetadata
            {
                AgentId = "web-research",
                ContextId = "context-1",
                MessageId = "message-1",
                UserId = "test-user",
                PayloadHash = null,
                BearerToken = null
            },
            CancellationToken.None);

        Assert.Equal($"First line.{Environment.NewLine}Second line.", result.Text);
    }

    [Fact]
    public async Task InvokeAsyncStillExtractsDirectMessageText()
    {
        const string responseBody =
            """
            {
              "jsonrpc": "2.0",
              "id": "response-1",
              "result": {
                "message": {
                  "parts": [
                    { "text": "Direct response." }
                  ]
                }
              }
            }
            """;
        var invoker = CreateInvoker(responseBody);

        var result = await invoker.InvokeAsync(
            "test prompt",
            new A2ARequestMetadata
            {
                AgentId = "web-research",
                ContextId = "context-1",
                MessageId = "message-1",
                UserId = "test-user",
                PayloadHash = null,
                BearerToken = null
            },
            CancellationToken.None);

        Assert.Equal("Direct response.", result.Text);
    }

    private static FoundryA2AInvoker CreateInvoker(string responseBody)
    {
        var catalog = new AgentCatalog(
            Options.Create(new AdapterOptions { Backend = "Mock" }),
            Options.Create(new CopilotStudioOptions()),
            Options.Create(new FoundryOptions
            {
                Agents = new Dictionary<string, FoundryAgentOptions>
                {
                    ["web"] = new()
                    {
                        Id = "web-research",
                        DisplayName = "Foundry Web Research",
                        Endpoint =
                            "https://account.services.ai.azure.com/api/projects/project/" +
                            "agents/agent/endpoint/protocols/a2a"
                    }
                }
            }));
        var client = new HttpClient(new StubHandler(responseBody));
        return new FoundryA2AInvoker(
            catalog,
            new StubCredential(),
            new StubHttpClientFactory(client));
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                });
    }
}
