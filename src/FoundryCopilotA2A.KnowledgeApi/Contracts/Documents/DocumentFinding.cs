using System.Text.Json.Serialization;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;

/// <summary>Associates an extracted finding with the passage IDs that actually support it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentFinding(string Text, string[] PassageIds);
