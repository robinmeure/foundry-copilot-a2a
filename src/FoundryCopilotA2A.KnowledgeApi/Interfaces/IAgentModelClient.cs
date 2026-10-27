using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.KnowledgeApi.Interfaces;

/// <summary>Creates the configured model chat client for grounded response generation.</summary>
public interface IAgentModelClient
{
    IChatClient Create();
}
