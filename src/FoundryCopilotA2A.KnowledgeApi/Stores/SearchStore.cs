using System.Net;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Stores;

/// <summary>Indexes and retrieves scope-isolated passages using local fixtures or Azure AI Search.</summary>
public sealed class SearchStore(
    AzureServices azure,
    IConfiguration config,
    IWebHostEnvironment environment) : ISearchStore
{
    private const string SearchTokenScope = "https://search.azure.com/.default";
    private const string SearchApiVersion = "2024-07-01";
    private const int UploadBatchSize = 16;
    private const int DeleteBatchSize = 100;
    private const int SearchTopResults = 8;

    private static readonly string[] RequiredFields =
    [
        "id",
        "scope",
        "userId",
        "documentId",
        "title",
        "content",
        "locator",
        "url",
        "expiresAt",
        "contentVector"
    ];

    private static readonly string[] RequiredFilterableFields =
    [
        "scope",
        "userId",
        "documentId",
        "expiresAt"
    ];

    private readonly SemaphoreSlim indexValidationLock = new(1);
    private bool isIndexValidated;

    private string IndexBaseUrl =>
        $"{azure.Endpoint("Search")}/indexes/{Uri.EscapeDataString(config["Search:Index"]!)}";

    public async Task Index(DocumentRecord document, Passage[] passages, CancellationToken ct)
    {
        if (config["Knowledge:Backend"] == "Mock")
        {
            await IndexMockPassagesAsync(document, passages, ct);
            return;
        }

        await EnsureIndexValidatedAsync(ct);

        // Small batches keep the JSON/vector payload below Azure AI Search's request limit.
        foreach (var batch in passages.Chunk(UploadBatchSize))
        {
            var actions = new List<Dictionary<string, object?>>(batch.Length);
            foreach (var passage in batch)
            {
                var embedding = await azure.Embed(passage.Content, ct);
                actions.Add(new Dictionary<string, object?>
                {
                    ["@search.action"] = "upload",
                    ["id"] = passage.Id,
                    ["scope"] = document.Scope,
                    ["userId"] = passage.UserId ?? document.UserId,
                    ["documentId"] = document.Id,
                    ["title"] = passage.Title,
                    ["content"] = passage.Content,
                    ["locator"] = passage.Locator,
                    ["url"] = passage.Url,
                    ["expiresAt"] = document.ExpiresAt,
                    ["contentVector"] = embedding
                });
            }

            await WriteIndexBatchAsync(actions, ct);
        }
    }

    public async Task<Passage[]> Search(
        string scope,
        DocumentRecord[] allowed,
        string query,
        CancellationToken ct,
        string? userId = null)
    {
        if (allowed.Length == 0)
        {
            return [];
        }

        if (config["Knowledge:Backend"] == "Mock")
        {
            return await SearchMockPassagesAsync(scope, allowed, query, userId, ct);
        }

        await EnsureIndexValidatedAsync(ct);

        var allowedDocumentIds = allowed.Select(document => document.Id);
        var userFilter = !string.IsNullOrWhiteSpace(userId)
            ? $"and userId eq '{EscapeODataString(userId)}' "
            : string.Empty;

        var filter =
            $"scope eq '{scope}' " +
            userFilter +
            $"and search.in(documentId, '{string.Join(",", allowedDocumentIds)}', ',') " +
            $"and expiresAt gt {DateTimeOffset.UtcNow:O}";

        var queryEmbedding = await azure.Embed(query, ct);
        var searchBody = new
        {
            search = query,
            filter,
            select = "id,userId,documentId,title,content,locator,url",
            top = SearchTopResults,
            vectorQueries = new[]
            {
                new
                {
                    kind = "vector",
                    vector = queryEmbedding,
                    fields = "contentVector",
                    k = 30
                }
            }
        };

        using var response = await azure.Send(
            $"{IndexBaseUrl}/docs/search?api-version={SearchApiVersion}",
            SearchTokenScope,
            HttpMethod.Post,
            searchBody,
            ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        return json.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .Select(element => new Passage(
                Id: element.GetProperty("id").GetString()!,
                DocumentId: element.GetProperty("documentId").GetString()!,
                Title: element.GetProperty("title").GetString()!,
                Content: element.GetProperty("content").GetString()!,
                Locator: element.GetProperty("locator").GetString()!,
                Url: element.GetProperty("url").GetString(),
                UserId: element.TryGetProperty("userId", out var userIdElement)
                    ? userIdElement.GetString()
                    : userId))
            .Where(passage => string.IsNullOrWhiteSpace(userId) ||
                              string.Equals(passage.UserId, userId, StringComparison.Ordinal))
            .ToArray();
    }

    public async Task Delete(DocumentRecord document, CancellationToken ct)
    {
        var chunkIds = Enumerable
            .Range(0, document.Chunks)
            .Select(chunkIndex => $"{document.Id}-{chunkIndex}")
            .ToArray();

        if (config["Knowledge:Backend"] == "Mock")
        {
            var directory = ResolveMockDirectory(document.Scope);
            foreach (var chunkId in chunkIds)
            {
                File.Delete(Path.Combine(directory, $"{chunkId}.json"));
            }

            return;
        }

        foreach (var batch in chunkIds.Chunk(DeleteBatchSize))
        {
            var deleteActions = batch
                .Select(chunkId => new Dictionary<string, object?>
                {
                    ["@search.action"] = "delete",
                    ["id"] = chunkId
                })
                .ToArray();

            await WriteIndexBatchAsync(deleteActions, ct);
        }
    }

    public async Task<Passage[]> ReadDocument(
                string scope, string userId, DocumentRecord document, CancellationToken ct)
    {
        if (document.Scope != scope || document.Status != "Ready" ||
            document.ExpiresAt <= DateTimeOffset.UtcNow ||
            (!string.IsNullOrWhiteSpace(document.UserId) && document.UserId != userId))
        {
            throw new ApiException(404, "document_unavailable", "The document is no longer available.");
        }
        if (document.Chunks is < 1 or > 2000)
        {
            throw new ApiException(409, "document_incomplete", "The document's chunk count is invalid. Reimport it.");
        }

        var passages = new List<Passage>();
        if (config["Knowledge:Backend"] == "Mock")
        {
            for (var index = 0; index < document.Chunks; index++)
            {
                var path = Path.Combine(ResolveMockDirectory(scope), $"{document.Id}-{index}.json");
                if (!File.Exists(path))
                {
                    throw IncompleteDocument();
                }
                var json = await File.ReadAllTextAsync(path, ct);
                passages.Add(JsonSerializer.Deserialize<Passage>(json) ?? throw IncompleteDocument());
            }
        }
        else
        {
            await EnsureIndexValidatedAsync(ct);
            // Validate the complete key set after paging, then restore numeric chunk order.
            for (var skip = 0; skip < document.Chunks; skip += 500)
            {
                var body = new
                {
                    search = "*",
                    filter = $"scope eq '{EscapeODataString(scope)}' and userId eq '{EscapeODataString(userId)}' " +
                        $"and documentId eq '{EscapeODataString(document.Id)}' and expiresAt gt {DateTimeOffset.UtcNow:O}",
                    select = "id,userId,documentId,title,content,locator,url",
                    top = Math.Min(500, document.Chunks - skip),
                    skip
                };
                using var response = await azure.Send(
                    $"{IndexBaseUrl}/docs/search?api-version={SearchApiVersion}",
                    SearchTokenScope, HttpMethod.Post, body, ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    throw InvalidDocumentResponse();
                }
                foreach (var element in values.EnumerateArray())
                {
                    passages.Add(ReadIndexedPassage(element));
                }
            }
        }

        var byId = new Dictionary<string, Passage>(StringComparer.Ordinal);
        foreach (var passage in passages)
        {
            if (string.IsNullOrWhiteSpace(passage.Id) ||
                string.IsNullOrWhiteSpace(passage.Title) || string.IsNullOrWhiteSpace(passage.Locator) ||
                passage.DocumentId != document.Id ||
                (passage.UserId != userId && !(config["Knowledge:Backend"] == "Mock" && string.IsNullOrEmpty(passage.UserId))) ||
                string.IsNullOrWhiteSpace(passage.Content) ||
                !byId.TryAdd(passage.Id, passage))
            {
                throw IncompleteDocument();
            }
        }
        var ordered = new Passage[document.Chunks];
        for (var index = 0; index < ordered.Length; index++)
        {
            if (!byId.TryGetValue($"{document.Id}-{index}", out var passage))
            {
                throw IncompleteDocument();
            }
            ordered[index] = passage;
        }
        if (byId.Count != ordered.Length)
        {
            throw IncompleteDocument();
        }
        return ordered;
    }

    private static ApiException IncompleteDocument() => new(
        409, "document_incomplete",
        "Not all authorized document chunks could be read. The document may have changed or expired; reimport it.");

    private static Passage ReadIndexedPassage(JsonElement element)
    {
        string RequiredString(string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw InvalidDocumentResponse();

        var id = RequiredString("id");
        string? url = null;
        if (element.TryGetProperty("url", out var urlElement) && urlElement.ValueKind != JsonValueKind.Null)
        {
            url = urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : throw InvalidDocumentResponse();
        }
        return new(id, RequiredString("documentId"), RequiredString("title"), RequiredString("content"),
            RequiredString("locator"), url, RequiredString("userId"));
    }

    private static ApiException InvalidDocumentResponse() => new(
        502, "invalid_document_response", "Search returned an invalid document chunk response.");

    private string ResolveMockDirectory(string scope) =>
        Path.Combine(
            Path.GetFullPath(
                config["Knowledge:DataDirectory"] ?? throw new InvalidOperationException("Knowledge:DataDirectory is required."),
                environment.ContentRootPath),
            scope,
            "mock-passages");

    private async Task IndexMockPassagesAsync(
        DocumentRecord document,
        Passage[] passages,
        CancellationToken cancellationToken)
    {
        var directory = ResolveMockDirectory(document.Scope);
        Directory.CreateDirectory(directory);

        foreach (var passage in passages)
        {
            var effectivePassage = passage.UserId is not null
                ? passage
                : passage with { UserId = document.UserId };
            var filePath = Path.Combine(directory, $"{effectivePassage.Id}.json");
            var serialized = JsonSerializer.Serialize(effectivePassage);
            await File.WriteAllTextAsync(filePath, serialized, cancellationToken);
        }
    }

    private async Task<Passage[]> SearchMockPassagesAsync(
        string scope,
        DocumentRecord[] allowedDocuments,
        string query,
        string? userId,
        CancellationToken cancellationToken)
    {
        var passages = new List<Passage>();
        var directory = ResolveMockDirectory(scope);

        foreach (var document in allowedDocuments)
        {
            if (!string.IsNullOrWhiteSpace(userId) &&
                !string.IsNullOrWhiteSpace(document.UserId) &&
                !string.Equals(document.UserId, userId, StringComparison.Ordinal))
            {
                continue;
            }

            for (var chunkIndex = 0; chunkIndex < document.Chunks; chunkIndex++)
            {
                var filePath = Path.Combine(directory, $"{document.Id}-{chunkIndex}.json");
                if (!File.Exists(filePath))
                {
                    throw new ApiException(
                        StatusCodes.Status409Conflict,
                        "mock_document_missing",
                        "Mock passage data is missing. Remove and reimport the document.");
                }

                var rawJson = await File.ReadAllTextAsync(filePath, cancellationToken);
                var passage = JsonSerializer.Deserialize<Passage>(rawJson)
                    ?? throw new InvalidDataException("Invalid mock passage.");

                if (!string.IsNullOrWhiteSpace(userId) &&
                    !string.IsNullOrWhiteSpace(passage.UserId) &&
                    !string.Equals(passage.UserId, userId, StringComparison.Ordinal))
                {
                    continue;
                }

                passages.Add(passage);
            }
        }

        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return passages
            .OrderByDescending(passage =>
                queryTerms.Count(term => passage.Content.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(SearchTopResults)
            .ToArray();
    }

    private static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private async Task EnsureIndexValidatedAsync(CancellationToken cancellationToken)
    {
        if (isIndexValidated)
        {
            return;
        }

        await indexValidationLock.WaitAsync(cancellationToken);
        try
        {
            if (isIndexValidated)
            {
                return;
            }

            using var response = await azure.Send(
                $"{IndexBaseUrl}?api-version={SearchApiVersion}",
                SearchTokenScope,
                HttpMethod.Get,
                body: null,
                ct: cancellationToken,
                allowNotFound: true);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ApiException(
                    StatusCodes.Status503ServiceUnavailable,
                    "index_missing",
                    "Search index does not exist. Create it using the schema in the Knowledge API README; no existing index will be overwritten.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var fields = json.RootElement
                .GetProperty("fields")
                .EnumerateArray()
                .ToDictionary(field => field.GetProperty("name").GetString()!);

            foreach (var fieldName in RequiredFields)
            {
                if (!fields.ContainsKey(fieldName))
                {
                    throw new ApiException(
                        StatusCodes.Status503ServiceUnavailable,
                        "index_incompatible",
                        $"Search index is missing field '{fieldName}'.");
                }
            }

            foreach (var fieldName in RequiredFilterableFields)
            {
                if (!fields[fieldName].GetProperty("filterable").GetBoolean())
                {
                    throw new ApiException(
                        StatusCodes.Status503ServiceUnavailable,
                        "index_incompatible",
                        $"Search field '{fieldName}' must be filterable.");
                }
            }

            var expectedDimensions = config.GetValue("OpenAI:EmbeddingDimensions", 3072);
            var actualDimensions = fields["contentVector"].GetProperty("dimensions").GetInt32();
            if (actualDimensions != expectedDimensions)
            {
                throw new ApiException(
                    StatusCodes.Status503ServiceUnavailable,
                    "index_incompatible",
                    "Search embedding dimensions differ from configuration.");
            }

            isIndexValidated = true;
        }
        finally
        {
            indexValidationLock.Release();
        }
    }

    private async Task WriteIndexBatchAsync(
        IEnumerable<Dictionary<string, object?>> actions,
        CancellationToken cancellationToken)
    {
        using var response = await azure.Send(
            $"{IndexBaseUrl}/docs/index?api-version={SearchApiVersion}",
            SearchTokenScope,
            HttpMethod.Post,
            new { value = actions },
            cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var hasFailedChunk = json.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .Any(entry => !entry.GetProperty("status").GetBoolean());

        if (hasFailedChunk)
        {
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "index_partial_failure",
                "Search rejected one or more chunks. The document has not been committed.");
        }
    }
}
