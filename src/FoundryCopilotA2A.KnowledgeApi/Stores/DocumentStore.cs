using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Stores;

/// <summary>
/// Single-instance document metadata store. Search is authoritative only for documents committed here.
/// </summary>
public sealed class DocumentStore(IConfiguration configuration, IWebHostEnvironment environment) : IDocumentStore
{
    private const int ScopeHexLength = 64;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string rootDirectory = Path.GetFullPath(
        configuration["Knowledge:DataDirectory"] ?? throw new InvalidOperationException("Knowledge:DataDirectory is required."),
        environment.ContentRootPath);

    public async Task<DocumentRecord[]> List(string scope, CancellationToken ct)
    {
        var directory = ResolveScopeDirectory(scope);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var records = new List<DocumentRecord>();
        foreach (var filePath in Directory.EnumerateFiles(directory, "*.document.json"))
        {
            await using var stream = File.OpenRead(filePath);
            var record = await JsonSerializer.DeserializeAsync<DocumentRecord>(stream, SerializerOptions, ct)
                ?? throw new InvalidDataException("Invalid document metadata.");

            records.Add(record);
        }

        return records.ToArray();
    }

    public async Task Commit(DocumentRecord document, CancellationToken ct)
    {
        var directory = ResolveScopeDirectory(document.Scope);
        Directory.CreateDirectory(directory);

        var targetPath = GetDocumentFilePath(directory, document.Id);
        var temporaryPath = $"{targetPath}.tmp";

        try
        {
            var serialized = JsonSerializer.Serialize(document, SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, serialized, ct);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<DocumentRecord> Get(string scope, string id, CancellationToken ct)
    {
        var documents = await List(scope, ct);
        return documents.SingleOrDefault(document => document.Id == id)
            ?? throw new ApiException(
                StatusCodes.Status404NotFound,
                "document_not_found",
                "Document not found in this conversation.");
    }

    public void Remove(DocumentRecord document)
    {
        var directory = ResolveScopeDirectory(document.Scope);
        var filePath = GetDocumentFilePath(directory, document.Id);
        File.Delete(filePath);
    }

    public async Task<DocumentRecord[]> All(CancellationToken ct)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return [];
        }

        var allDocuments = new List<DocumentRecord>();
        foreach (var scopeDirectory in Directory.EnumerateDirectories(rootDirectory))
        {
            var scope = Path.GetFileName(scopeDirectory);
            var scopeDocuments = await List(scope, ct);
            allDocuments.AddRange(scopeDocuments);
        }

        return allDocuments.ToArray();
    }

    private string ResolveScopeDirectory(string scope)
    {
        if (scope.Length != ScopeHexLength || scope.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Invalid scope.", nameof(scope));
        }

        return Path.Combine(rootDirectory, scope);
    }

    private static string GetDocumentFilePath(string scopeDirectory, string documentId) =>
        Path.Combine(scopeDirectory, $"{documentId}.document.json");
}
