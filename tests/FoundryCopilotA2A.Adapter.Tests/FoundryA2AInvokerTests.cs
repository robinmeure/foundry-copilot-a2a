using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter.Tests;

public sealed class FoundryA2AInvokerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterAttributesFoundryRepliesToTheConfiguredAgent(bool withCitations)
    {
        using var outboundClient = new HttpClient(new StubHandler(
            withCitations
                ? CitationTests.UpstreamResponse("Direct response.")
                : """{"result":{"message":{"parts":[{"text":"Direct response."}]}}}"""));
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Foundry:Agents:web-research:DisplayName", "Foundry Web Research");
            builder.UseSetting(
                "Foundry:Agents:web-research:Endpoint",
                "https://foundry.example/agents/agent/endpoint/protocols/a2a");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TokenCredential>();
                services.AddSingleton<TokenCredential, StubCredential>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(outboundClient));
            });
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, AdapterConstants.ChainAgentRuntimePath("web-research"))
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = "foundry-response",
                method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER",
                        messageId = "foundry-response",
                        contextId = "foundry-context",
                        parts = new[] { new { text = "Research this" } }
                    }
                }
            })
        };
        request.Headers.Add("A2A-Version", "1.0");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var part = body.RootElement.GetProperty("result").GetProperty("message").GetProperty("parts")[0];
        Assert.Equal(
            "Responding agent: Foundry Web Research\n\nDirect response.",
            part.GetProperty("text").GetString());
        var metadata = part.GetProperty("metadata");
        Assert.Equal("web-research", metadata.GetProperty("agentId").GetString());
        Assert.Equal("Foundry Web Research", metadata.GetProperty("agentName").GetString());
        var citations = A2AResponseText.Read(body.RootElement).Citations;
        if (withCitations)
        {
            Assert.Equal(CitationTests.Sample().Sources[0], Assert.Single(citations!.Sources));
        }
        else
        {
            Assert.Null(citations);
        }
    }

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

        var result = await CollectAsync(invoker.StreamAsync(
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
            CancellationToken.None));

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

        var result = await CollectAsync(invoker.StreamAsync(
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
            CancellationToken.None));

        Assert.Equal("Direct response.", result.Text);
    }

    private static async Task<CopilotInvocationResult> CollectAsync(
        IAsyncEnumerable<CopilotInvocationUpdate> updates)
    {
        var text = new StringBuilder();
        string? conversationId = null;
        string? responseId = null;
        await foreach (var update in updates)
        {
            text.Append(update.Text);
            conversationId = update.ConversationId ?? conversationId;
            responseId = update.ResponseId ?? responseId;
        }

        return new CopilotInvocationResult(text.ToString(), conversationId, responseId);
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
            new StubHttpClientFactory(client),
            new UnusedTokenBroker(),
            Options.Create(new AuthenticationOptions { Enabled = false }));
    }

    private sealed class UnusedTokenBroker : IOboTokenBroker
    {
        public Task<string> AcquireAsync(
            string scope, A2ARequestMetadata metadata, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Anonymous development does not perform OBO.");
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
