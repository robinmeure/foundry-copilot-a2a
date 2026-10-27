namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;

/// <summary>Returns browser items with an opaque, caller-bound continuation token.</summary>
public sealed record GraphPage(GraphItem[] Items, string? NextPage = null, string? ResolvedSiteId = null);
