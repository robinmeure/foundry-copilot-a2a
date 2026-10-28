using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoundryCopilotA2A.Skills;

/// <summary>
/// The <c>connector.json</c> contract of an <c>azure-ai-search</c> skill. Everything index-specific lives here so
/// adding another index is a folder, not code; endpoint and index name stay in configuration.
/// </summary>
public sealed class SearchSkillSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const int MaxFilterValueLength = 200;

    /// <summary>Configuration section with <c>Enabled</c>, <c>Endpoint</c> and <c>Index</c>; defaults to <c>Skills:&lt;name&gt;</c>.</summary>
    public string? ConfigurationSection { get; init; }
    public SearchToolSettings Tool { get; init; } = new();
    public string ApiVersion { get; init; } = "2024-07-01";
    public int MaxResults { get; init; } = 8;
    public int MaxContentLength { get; init; } = 3000;
    public int MaxQueryLength { get; init; } = 2000;
    public SearchQuerySettings Search { get; init; } = new();
    public SearchFilterSettings[] Filters { get; init; } = [];
    public SearchResultSettings Result { get; init; } = new();
    public string? Coverage { get; init; }
    public SearchMockSettings? Mock { get; init; }
    public SearchStatusSettings? Status { get; init; }

    public string SectionFor(string skillName) => ConfigurationSection ?? $"Skills:{skillName}";
    public string ToolNameFor(string skillName) => Tool.Name ?? SkillConventions.DefaultSearchToolName(skillName);

    public static SearchSkillSettings Parse(JsonElement json) =>
        json.Deserialize<SearchSkillSettings>(JsonOptions) ?? throw new JsonException("connector.json is empty.");

    public IEnumerable<string> Validate(string skillName)
    {
        if (!SkillConventions.IsValidConfigurationSection(SectionFor(skillName)))
        {
            yield return "configurationSection must be a configuration path such as 'Skills:my-index'.";
        }
        var tool = ToolNameFor(skillName);
        if (!SkillConventions.IsValidToolName(tool))
        {
            yield return $"tool.name '{tool}' must match ^[a-z][a-z0-9_]{{0,63}}$.";
        }
        else if (SkillConventions.IsReservedToolName(tool))
        {
            yield return $"tool.name '{tool}' is reserved.";
        }
        if (string.IsNullOrWhiteSpace(Tool.Description) || Tool.Description.Length > SkillConventions.MaxDescriptionLength)
        {
            yield return $"tool.description is required and must be at most {SkillConventions.MaxDescriptionLength} characters.";
        }
        if (string.IsNullOrWhiteSpace(ApiVersion) || ApiVersion.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            yield return "apiVersion must be an Azure AI Search REST API version such as 2024-07-01.";
        }
        if (MaxResults is < 1 or > 50) yield return "maxResults must be between 1 and 50.";
        if (MaxContentLength is < 200 or > 8000) yield return "maxContentLength must be between 200 and 8000.";
        if (MaxQueryLength is < 10 or > 4000) yield return "maxQueryLength must be between 10 and 4000.";
        if (string.IsNullOrWhiteSpace(Coverage) || Coverage.Length > 300 ||
            !Coverage.Contains("not exhaustive", StringComparison.OrdinalIgnoreCase))
        {
            yield return "coverage is required, at most 300 characters, and must state that results are not exhaustive.";
        }

        foreach (var error in Search.Validate()) yield return error;

        var arguments = new HashSet<string>(StringComparer.Ordinal) { "query" };
        foreach (var filter in Filters)
        {
            if (!SkillConventions.IsValidArgumentName(filter.Argument) || !arguments.Add(filter.Argument!))
            {
                yield return $"filters: argument '{filter.Argument}' must be a unique camelCase name other than 'query'.";
            }
            if (!SkillConventions.IsValidSearchField(filter.Field))
            {
                yield return $"filters: field '{filter.Field}' is not a valid index field name.";
            }
            if (filter.Kind is not ("equals" or "any" or "dateFrom" or "dateTo"))
            {
                yield return $"filters: kind '{filter.Kind}' must be equals, any, dateFrom or dateTo.";
            }
            if (string.IsNullOrWhiteSpace(filter.Description) || filter.Description.Length > 300)
            {
                yield return $"filters: '{filter.Argument}' needs a description of at most 300 characters.";
            }
        }
        if (Filters.Count(filter => filter.Kind == "dateFrom") > 1 || Filters.Count(filter => filter.Kind == "dateTo") > 1)
        {
            yield return "filters: at most one dateFrom and one dateTo filter are supported.";
        }

        foreach (var error in Result.Validate()) yield return error;
        foreach (var field in Result.Fields().Distinct().Where(field => !Search.Select.Contains(field, StringComparer.Ordinal)))
        {
            yield return $"result: '{field}' must also be listed in search.select.";
        }

        if (Mock is not null && (string.IsNullOrWhiteSpace(Mock.Title) || string.IsNullOrWhiteSpace(Mock.Content)))
        {
            yield return "mock requires a title and content.";
        }
    }

    /// <summary>The strict function schema advertised after the skill is loaded.</summary>
    public JsonElement BuildParameters()
    {
        var properties = new Dictionary<string, object>
        {
            ["query"] = new
            {
                type = "string",
                description = $"Non-empty natural-language search query, at most {MaxQueryLength.ToString("N0", CultureInfo.InvariantCulture)} characters."
            }
        };
        foreach (var filter in Filters)
        {
            properties[filter.Argument!] = new { type = new[] { "string", "null" }, description = filter.Description };
        }
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties,
            required = properties.Keys.ToArray(),
            additionalProperties = false
        });
    }
}

public sealed class SearchToolSettings
{
    public string? Name { get; init; }
    public string? Description { get; init; }
}

public sealed class SearchQuerySettings
{
    public string? QueryType { get; init; }
    public string? SemanticConfiguration { get; init; }
    public string? ScoringProfile { get; init; }
    public string[] SearchFields { get; init; } = [];
    public string[] Select { get; init; } = [];
    public SearchVectorSettings? Vector { get; init; }

    internal IEnumerable<string> Validate()
    {
        if (QueryType is not (null or "simple" or "full" or "semantic"))
        {
            yield return "search.queryType must be simple, full or semantic.";
        }
        if (QueryType == "semantic" && string.IsNullOrWhiteSpace(SemanticConfiguration))
        {
            yield return "search.semanticConfiguration is required for semantic queries.";
        }
        foreach (var name in new[] { SemanticConfiguration, ScoringProfile }.Where(value => value is not null))
        {
            if (name!.Length > 128 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            {
                yield return $"search: '{name}' is not a valid semantic configuration or scoring profile name.";
            }
        }
        if (Select.Length == 0)
        {
            yield return "search.select must list the fields the connector may read; never select storage paths or vectors.";
        }
        foreach (var field in SearchFields.Concat(Select).Where(field => !SkillConventions.IsValidSearchField(field)))
        {
            yield return $"search: '{field}' is not a valid index field name.";
        }
        if (Vector is not null)
        {
            if (!SkillConventions.IsValidSearchField(Vector.Fields))
            {
                yield return "search.vector.fields must be one vector field name.";
            }
            if (Vector.K is < 1 or > 1000)
            {
                yield return "search.vector.k must be between 1 and 1000.";
            }
            if (Vector.FilterMode is not (null or "preFilter" or "postFilter"))
            {
                yield return "search.vector.filterMode must be preFilter or postFilter.";
            }
            if (Select.Contains(Vector.Fields, StringComparer.Ordinal))
            {
                yield return "search.select must not include the vector field.";
            }
        }
    }
}

public sealed class SearchVectorSettings
{
    public string? Fields { get; init; }
    public int K { get; init; } = 50;
    public string? FilterMode { get; init; }
}

public sealed class SearchFilterSettings
{
    public string? Argument { get; init; }
    public string? Field { get; init; }
    public string? Kind { get; init; }
    public string? Description { get; init; }
}

public sealed class SearchResultSettings
{
    public string? IdPrefix { get; init; }
    public string Id { get; init; } = "id";
    public string? ParentId { get; init; }
    public string[] Title { get; init; } = [];
    public string? DefaultTitle { get; init; }
    public string[] Content { get; init; } = [];
    public string[] Metadata { get; init; } = [];
    public string[] ListMetadata { get; init; } = [];
    public string? LocatorLabel { get; init; }
    public string[] Locator { get; init; } = [];

    internal IEnumerable<string> Validate()
    {
        if (IdPrefix is not null && !SkillConventions.IsValidName(IdPrefix))
        {
            yield return "result.idPrefix must be lowercase letters, digits and hyphens.";
        }
        if (Content.Length == 0)
        {
            yield return "result.content must list at least one text field.";
        }
        if (string.IsNullOrWhiteSpace(DefaultTitle) || DefaultTitle.Length > 120)
        {
            yield return "result.defaultTitle is required and must be at most 120 characters.";
        }
        if (LocatorLabel is { Length: > 60 })
        {
            yield return "result.locatorLabel must be at most 60 characters.";
        }
        foreach (var field in Fields().Where(field => !SkillConventions.IsValidSearchField(field)))
        {
            yield return $"result: '{field}' is not a valid index field name.";
        }
    }

    internal IEnumerable<string> Fields() => new[] { Id }.Concat(ParentId is null ? [] : [ParentId])
        .Concat(Title).Concat(Content).Concat(Metadata).Concat(ListMetadata).Concat(Locator);
}

public sealed class SearchMockSettings
{
    public string? Title { get; init; }
    public string? Content { get; init; }
    public string? Locator { get; init; }
}

public sealed class SearchStatusSettings
{
    public string? Started { get; init; }
    public string? ResultNoun { get; init; }
    public string? Failed { get; init; }
}
