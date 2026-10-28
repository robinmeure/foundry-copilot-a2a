namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Files;

/// <summary>A file rendered by a skill tool during one turn; it lives only in server memory.</summary>
public sealed record GeneratedDocument(string Name, string MediaType, byte[] Content);

/// <summary>What the client sees: metadata only; the bytes are downloaded from the files endpoint.</summary>
public sealed record GeneratedFileView(string Id, string Name, string MediaType, long Size, DateTimeOffset ExpiresAt);
