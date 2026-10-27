using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;

/// <summary>Lists the approved external agents currently available for source selection.</summary>
public sealed record KnowledgeAgentsResponse(KnowledgeAgentDescriptor[] Agents);
