using System.Globalization;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure.Azure;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;

/// <summary>Queries the configured public parliamentary source without mixing in private document evidence.</summary>
public sealed class KamervragenSkill(AzureServices azure, IConfiguration configuration)
{
    public const string Source = "kamervragen";
    public const string ToolName = "search_kamervragen";
    internal const int MaxResults = 8;
    private const int MaxContentLength = 3000;
    private static readonly string[] ArgumentNames =
        ["query", "thema", "ministerie", "partij", "documentNummer", "fromDate", "toDate"];

    public bool Enabled => configuration["Knowledge:Backend"] == "Mock" || configuration.GetValue<bool>("Kamervragen:Enabled");

    public static readonly object ToolDefinition = new
    {
        type = "function",
        name = ToolName,
        description = "Search the shared Dutch parliamentary questions (Kamervragen) reference index. " +
            "Combines keyword, vector and semantic search. Returns a bounded sample, not an exhaustive list. " +
            "Use Dutch search terms where appropriate. Optional exact metadata filters must use values supplied " +
            "by the user or previous search results; otherwise pass null. Publication dates are inclusive UTC dates.",
        parameters = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["query"] = new { type = "string", description = "Non-empty natural-language search query, at most 2,000 characters." },
                ["thema"] = Optional("Exact thema value, or null."),
                ["ministerie"] = Optional("Exact ministerie value, or null."),
                ["partij"] = Optional("Exact member of partijen, or null."),
                ["documentNummer"] = Optional("Exact documentNummer, or null."),
                ["fromDate"] = Optional("Earliest publication date, yyyy-MM-dd, or null."),
                ["toDate"] = Optional("Latest publication date, yyyy-MM-dd, or null.")
            },
            required = ArgumentNames,
            additionalProperties = false
        }),
        strict = true
    };

    public async Task<Passage[]> SearchAsync(JsonElement arguments, IReadOnlyCollection<string> sources, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new ApiException(400, "kamervragen_disabled", "Kamervragen search is not configured.");
        }
        if (!sources.Contains(Source))
        {
            throw new ApiException(403, "kamervragen_not_selected", "Enable the Kamervragen source before searching it.");
        }
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(property => !ArgumentNames.Contains(property.Name)))
        {
            throw InvalidArguments();
        }

        var query = ReadArgument(arguments, "query", 2000);
        if (query is null)
        {
            throw InvalidArguments();
        }
        var filters = new List<string>();
        foreach (var field in new[] { "thema", "ministerie", "documentNummer" })
        {
            if (ReadArgument(arguments, field) is { } value)
            {
                filters.Add($"{field} eq '{Escape(value)}'");
            }
        }
        if (ReadArgument(arguments, "partij") is { } partij)
        {
            filters.Add($"partijen/any(p: p eq '{Escape(partij)}')");
        }
        var from = ReadDate(arguments, "fromDate");
        var to = ReadDate(arguments, "toDate");
        if (from > to || to == DateOnly.MaxValue)
        {
            throw InvalidArguments();
        }
        if (from is { } start)
        {
            filters.Add($"datum ge {start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00Z");
        }
        if (to is { } end)
        {
            filters.Add($"datum lt {end.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00Z");
        }

        ct.ThrowIfCancellationRequested();
        if (configuration["Knowledge:Backend"] == "Mock")
        {
            return [new Passage("kamervragen:mock", "kamervragen:mock", "Mock Kamervragen",
                "Mock parliamentary reference: questions about responsible use of AI in public services.",
                "Kamervragen | mock data, not an actual parliamentary document")];
        }

        var body = new Dictionary<string, object>
        {
            ["search"] = query,
            ["queryType"] = "semantic",
            ["semanticConfiguration"] = "semantic",
            ["scoringProfile"] = "freshness",
            ["searchFields"] = "content,onderwerp,samenvatting,keyPoints,thema,minister,ministerie,indieners,partijen",
            ["select"] = "id,parentId,level,content,onderwerp,samenvatting,keyPoints,thema,minister,ministerie,indieners,partijen,vergaderjaar,soort,kamer,datum,documentNummer,aanhangselnummer",
            ["top"] = MaxResults,
            ["vectorFilterMode"] = "preFilter",
            ["vectorQueries"] = new[] { new { kind = "text", text = query, fields = "contentVector", k = 50 } }
        };
        if (filters.Count > 0)
        {
            body["filter"] = string.Join(" and ", filters);
        }

        using var response = await azure.Send(
            $"{configuration["Kamervragen:Endpoint"]?.TrimEnd('/')}/indexes/{Uri.EscapeDataString(configuration["Kamervragen:Index"]!)}/docs/search?api-version=2024-07-01",
            "https://search.azure.com/.default", HttpMethod.Post, body, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("value", out var results) || results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() > MaxResults)
        {
            throw InvalidResponse();
        }
        return results.EnumerateArray().Select(ToPassage).ToArray();
    }

    private static Passage ToPassage(JsonElement item)
    {
        var id = Text(item, "id") ?? throw InvalidResponse();
        var parentId = Text(item, "parentId") ?? id;
        if (id.Length > 512 || parentId.Length > 512)
        {
            throw InvalidResponse();
        }
        var content = Text(item, "content") ?? Text(item, "samenvatting") ?? throw InvalidResponse();
        var title = Text(item, "onderwerp") ?? Text(item, "documentNummer") ?? "Kamervraag";
        var metadata = new List<string>();
        foreach (var field in new[] { "thema", "minister", "ministerie", "vergaderjaar", "soort", "aanhangselnummer" })
        {
            if (Text(item, field) is { } value)
            {
                metadata.Add($"{field}: {Shorten(value, 150)}");
            }
        }
        foreach (var field in new[] { "partijen", "indieners", "keyPoints" })
        {
            if (!item.TryGetProperty(field, out var values) || values.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            {
                throw InvalidResponse();
            }
            metadata.Add($"{field}: {string.Join(", ", values.EnumerateArray().Take(8).Select(value => Shorten(value.GetString()!, 150)))}");
        }
        var locator = string.Join(" | ", new[] { "Kamervragen", Text(item, "documentNummer"), Text(item, "datum"), Text(item, "level") }
            .Where(value => value is not null));
        return new Passage(
            $"kamervragen:{id}",
            $"kamervragen:{parentId}",
            Shorten(title, 240),
            Shorten((metadata.Count > 0 ? Shorten(string.Join("\n", metadata), 1500) + "\n\n" : "") + content, MaxContentLength),
            Shorten(locator, 400));
    }

    private static object Optional(string description) => new { type = new[] { "string", "null" }, description };
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string Shorten(string value, int limit) => value.Length <= limit ? value : value[..limit] + "...";

    private static string? ReadArgument(JsonElement arguments, string name, int limit = 200)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > limit)
        {
            throw InvalidArguments();
        }
        return value.GetString()!.Trim();
    }

    private static DateOnly? ReadDate(JsonElement arguments, string name)
    {
        if (ReadArgument(arguments, name) is not { } raw)
        {
            return null;
        }
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw InvalidArguments();
    }

    private static string? Text(JsonElement item, string field)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }
        if (!item.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }
        return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
    }

    private static ApiException InvalidArguments() => new(400, "invalid_tool_arguments",
        "Kamervragen search requires a query of 1-2,000 characters, optional exact metadata filters of at most 200 characters, and valid ordered yyyy-MM-dd dates.");
    private static ApiException InvalidResponse() => new(502, "invalid_kamervragen_response",
        "Kamervragen search returned invalid evidence. Check the configured index schema.");
}
