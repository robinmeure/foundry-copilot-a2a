using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.Adapter;

public sealed record CitationSource(
    string Id,
    string Title,
    AgentResponder OriginatingAgent,
    string? Url = null,
    string? OriginatingMessageId = null);

public sealed record CitationReference(
    string SourceId,
    string? Marker = null,
    string? Locator = null,
    string? Quote = null);

public sealed record CitationBundle(
    IReadOnlyList<CitationSource> Sources,
    IReadOnlyList<CitationReference> Citations)
{
    public string SchemaVersion => "1";
}

internal sealed class CitationFormatException(string message) : InvalidOperationException(message);

internal static class AgentCitations
{
    public const string ExtensionUri = "urn:foundry-copilot-a2a:citations:v1";
    public const string Description =
        "Optional structured sources and citation references accompanying answer parts. " +
        "Source provenance is provider-reported, not independent verification.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AIContent ToContent(CitationBundle bundle)
    {
        Validate(bundle);
        return Part.FromData(JsonSerializer.SerializeToElement(
            new Dictionary<string, CitationBundle> { [ExtensionUri] = bundle },
            JsonOptions)).ToAIContent();
    }

    public static CitationBundle? ReadContainer(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(ExtensionUri, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object ||
            RequiredString(value, "schemaVersion") != "1")
        {
            throw Invalid("Unsupported citation schema version.");
        }

        var sources = RequiredArray(value, "sources", 100).EnumerateArray()
            .Select(source =>
            {
                if (source.ValueKind != JsonValueKind.Object ||
                    !source.TryGetProperty("originatingAgent", out var agent) ||
                    agent.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid("A citation source must identify its originating agent.");
                }

                return new CitationSource(
                    RequiredString(source, "id", 256),
                    RequiredString(source, "title"),
                    new AgentResponder(
                        RequiredString(agent, "id", 256), RequiredString(agent, "name")),
                    OptionalString(source, "url"),
                    OptionalString(source, "originatingMessageId", 256));
            }).ToArray();
        var citations = RequiredArray(value, "citations", 200).EnumerateArray()
            .Select(citation => new CitationReference(
                RequiredString(citation, "sourceId", 256),
                OptionalString(citation, "marker"),
                OptionalString(citation, "locator"),
                OptionalString(citation, "quote")))
            .ToArray();
        return Merge(null, new CitationBundle(sources, citations));
    }

    public static CitationBundle? Merge(CitationBundle? current, CitationBundle? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        Validate(incoming);
        var sources = new Dictionary<string, CitationSource>(StringComparer.Ordinal);
        var references = new HashSet<CitationReference>();
        foreach (var bundle in new[] { current, incoming })
        {
            if (bundle is null)
            {
                continue;
            }
            foreach (var source in bundle.Sources)
            {
                if (sources.TryGetValue(source.Id, out var previous) && previous != source)
                {
                    throw Invalid("Conflicting definitions for a citation source ID.");
                }
                sources[source.Id] = source;
            }
            references.UnionWith(bundle.Citations);
        }

        var result = new CitationBundle(sources.Values.ToArray(), references.ToArray());
        Validate(result);
        return result;
    }

    public static CitationBundle FromProviderSource(
        AgentResponder agent, string? messageId, string title, string? url,
        string? marker = null, string? quote = null)
    {
        // Include the provider response scope: two agents' [1] markers are not the same citation.
        var key = JsonSerializer.SerializeToUtf8Bytes(new { agent.Id, messageId, title, url });
        var id = $"source-{Convert.ToHexStringLower(SHA256.HashData(key))}";
        var result = new CitationBundle(
            [new CitationSource(id, title, agent, url, messageId)],
            [new CitationReference(id, marker, Quote: quote)]);
        Validate(result);
        return result;
    }

    public static void Validate(CitationBundle bundle)
    {
        if (bundle.Sources.Count > 100 || bundle.Citations.Count > 200)
        {
            throw Invalid("Citation payload exceeds 100 sources or 200 references.");
        }
        var sources = new Dictionary<string, CitationSource>(StringComparer.Ordinal);
        foreach (var source in bundle.Sources)
        {
            CheckString(source.Id, 256);
            CheckString(source.Title);
            CheckString(source.OriginatingAgent.Id, 256);
            CheckString(source.OriginatingAgent.Name);
            if (source.OriginatingMessageId is not null)
            {
                CheckString(source.OriginatingMessageId, 256);
            }
            if (source.Url is not null)
            {
                CheckString(source.Url);
                if (source.Url.Any(char.IsControl) || source.Url.Contains('\\') ||
                    !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                    string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    throw Invalid("Citation URLs must be absolute HTTP(S) URLs without credentials.");
                }
                if (uri.Query.TrimStart('?').Split('&').Any(parameter =>
                    IsCredentialParameter(Uri.UnescapeDataString(parameter.Split('=', 2)[0]))))
                {
                    throw Invalid("Citation URLs must not contain access tokens or signed access credentials.");
                }
            }
            if (sources.TryGetValue(source.Id, out var previous) && previous != source)
            {
                throw Invalid("Conflicting definitions for a citation source ID.");
            }
            sources[source.Id] = source;
        }
        foreach (var reference in bundle.Citations)
        {
            CheckString(reference.SourceId, 256);
            if (!sources.ContainsKey(reference.SourceId))
            {
                throw Invalid("A citation reference points to an unknown source.");
            }
            foreach (var text in new[] { reference.Marker, reference.Locator, reference.Quote })
            {
                if (text is not null)
                {
                    CheckString(text);
                }
            }
        }
    }

    internal static string RequiredString(JsonElement value, string name, int maxLength = 8192) =>
        OptionalString(value, name, maxLength) ??
        throw Invalid($"The citation field '{name}' is required.");

    internal static string? OptionalString(JsonElement value, string name, int maxLength = 8192)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Expected a citation object.");
        }
        if (!value.TryGetProperty(name, out var field))
        {
            return null;
        }
        if (field.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"The citation field '{name}' must be a string.");
        }
        var text = field.GetString()!;
        CheckString(text, maxLength);
        return text;
    }

    private static JsonElement RequiredArray(JsonElement value, string name, int limit)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() > limit)
        {
            throw Invalid($"Expected a citation '{name}' array with at most {limit} items.");
        }
        return array;
    }

    private static void CheckString(string text, int maxLength = 8192)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
        {
            throw Invalid($"Citation strings must be non-empty and at most {maxLength} characters.");
        }
    }

    internal static CitationFormatException Invalid(string reason) =>
        new($"Invalid structured citations: {reason}");

    private static bool IsCredentialParameter(string name) =>
        name.ToLowerInvariant() is "access_token" or "id_token" or "client_secret" or
            "token" or "sig" or "api_key" or "apikey" or "x-amz-signature" or "x-goog-signature";
}
