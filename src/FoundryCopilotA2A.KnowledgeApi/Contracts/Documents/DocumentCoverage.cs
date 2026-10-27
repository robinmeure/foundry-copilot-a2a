namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;

/// <summary>Reports expected and read chunk counts so partial document coverage remains explicit.</summary>
public sealed record DocumentCoverage(string DocumentId, string Title, int ExpectedChunks, int ReadChunks);
