using System.ClientModel.Primitives;
using Azure.Core;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using Microsoft.Extensions.AI;
using OpenAI;

#pragma warning disable OPENAI001 // The Responses client is still marked experimental by the OpenAI SDK.

namespace FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;

/// <summary>Creates caller-owned Responses clients with server-side storage controlled by the orchestrator.</summary>
public sealed class AgentModelClient(
    IHttpClientFactory clients,
    TokenCredential credential,
    IConfiguration configuration) : IAgentModelClient
{
    public IChatClient Create()
    {
        var client = new OpenAIClient(
            new BearerTokenPolicy(credential, "https://cognitiveservices.azure.com/.default"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri($"{configuration["OpenAI:Endpoint"]!.TrimEnd('/')}/openai/v1"),
                Transport = new HttpClientPipelineTransport(clients.CreateClient("knowledge")),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
            });

        return client.GetResponsesClient().AsIChatClient(configuration["OpenAI:ChatDeployment"]);
    }
}
