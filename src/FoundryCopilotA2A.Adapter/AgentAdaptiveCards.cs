using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.Adapter;

public sealed record AdaptiveCardItem(
    string Id,
    int Revision,
    JsonElement Content,
    string FallbackText,
    AgentResponder? OriginatingAgent = null)
{
    public string ContentType => AgentAdaptiveCards.CardContentType;
}

public sealed record AdaptiveCardBundle(IReadOnlyList<AdaptiveCardItem> Cards)
{
    public string SchemaVersion => "1";
}

internal sealed class AdaptiveCardFormatException(string message) : InvalidOperationException(message);

/// <summary>
/// Optional structured Adaptive Card payload carried in an A2A data part. Cards are display
/// content only; the plain-text fallback is the durable, accessible representation.
/// </summary>
internal static class AgentAdaptiveCards
{
    public const string ExtensionUri = "urn:foundry-copilot-a2a:adaptive-cards:v1";
    public const string CardContentType = "application/vnd.microsoft.card.adaptive";
    public const string Description =
        "Optional display-only Adaptive Cards accompanying answer parts. Every card carries a " +
        "plain-text fallback. Card actions are not executed by this adapter.";

    public const int MaxCardBytes = 64 * 1024;
    public const int MaxCardsPerTurn = 5;
    public const int MaxTurnBytes = 256 * 1024;
    public const int MaxDepth = 32;
    public const int MaxFallbackCharacters = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AIContent ToContent(AdaptiveCardBundle bundle)
    {
        Validate(bundle);
        var cards = bundle.Cards.Select(card => new Dictionary<string, object?>
        {
            ["id"] = card.Id,
            ["revision"] = card.Revision,
            ["contentType"] = card.ContentType,
            ["content"] = card.Content,
            ["fallbackText"] = card.FallbackText,
            ["originatingAgent"] = card.OriginatingAgent
        }).ToArray();
        return Part.FromData(JsonSerializer.SerializeToElement(
            new Dictionary<string, object>
            {
                [ExtensionUri] = new { schemaVersion = bundle.SchemaVersion, cards }
            },
            JsonOptions)).ToAIContent();
    }

    /// <summary>Reads a recognised envelope from a data part, or null when it is not present.</summary>
    public static AdaptiveCardBundle? ReadContainer(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(ExtensionUri, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != "1" ||
            !value.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("Unsupported adaptive card envelope.");
        }

        var items = new List<AdaptiveCardItem>();
        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("content", out var content))
            {
                throw Invalid("An adaptive card entry must contain content.");
            }

            AgentResponder? agent = null;
            if (card.TryGetProperty("originatingAgent", out var origin) &&
                origin.ValueKind == JsonValueKind.Object &&
                origin.TryGetProperty("id", out var agentId) && agentId.ValueKind == JsonValueKind.String &&
                origin.TryGetProperty("name", out var agentName) && agentName.ValueKind == JsonValueKind.String)
            {
                agent = new AgentResponder(agentId.GetString()!, agentName.GetString()!);
            }

            items.Add(new AdaptiveCardItem(
                StringProperty(card, "id") ?? throw Invalid("An adaptive card entry must have an id."),
                card.TryGetProperty("revision", out var revision) && revision.TryGetInt32(out var number)
                    ? number
                    : 1,
                content.Clone(),
                StringProperty(card, "fallbackText") ?? string.Empty,
                agent));
        }

        var bundle = new AdaptiveCardBundle(items);
        Validate(bundle);
        return bundle;
    }

    /// <summary>Like <see cref="ReadContainer"/>, but a malformed upstream card is dropped so its text answer survives.</summary>
    public static AdaptiveCardBundle? ReadContainerLenient(JsonElement container)
    {
        try
        {
            return ReadContainer(container);
        }
        catch (AdaptiveCardFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges by card id; a higher revision replaces a lower one, so a card refreshed later in the
    /// turn is not duplicated. Order of first appearance is kept.
    /// </summary>
    public static AdaptiveCardBundle? Merge(AdaptiveCardBundle? current, AdaptiveCardBundle? incoming)
    {
        if (incoming is null)
        {
            return current;
        }

        var cards = new List<AdaptiveCardItem>(current?.Cards ?? []);
        foreach (var card in incoming.Cards)
        {
            var index = cards.FindIndex(existing => existing.Id == card.Id);
            if (index < 0)
            {
                cards.Add(card);
            }
            else if (card.Revision > cards[index].Revision)
            {
                cards[index] = card;
            }
        }

        var result = new AdaptiveCardBundle(cards);
        Validate(result);
        return result;
    }

    public static void Validate(AdaptiveCardBundle bundle)
    {
        if (bundle.Cards.Count > MaxCardsPerTurn)
        {
            throw Invalid($"A turn may carry at most {MaxCardsPerTurn} adaptive cards.");
        }

        long total = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in bundle.Cards)
        {
            if (string.IsNullOrWhiteSpace(card.Id) || card.Id.Length > 256 || !ids.Add(card.Id))
            {
                throw Invalid("Adaptive card ids must be unique, non-empty and at most 256 characters.");
            }
            if (card.Revision < 1)
            {
                throw Invalid("An adaptive card revision must be positive.");
            }
            if (string.IsNullOrWhiteSpace(card.FallbackText) ||
                card.FallbackText.Length > MaxFallbackCharacters)
            {
                throw Invalid("An adaptive card requires a non-empty fallback text within the size limit.");
            }

            ValidateContent(card.Content);
            total += Encoding.UTF8.GetByteCount(card.Content.GetRawText());
            if (total > MaxTurnBytes)
            {
                throw Invalid("The adaptive cards of a turn exceed the size limit.");
            }
        }
    }

    public static void ValidateContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "AdaptiveCard")
        {
            throw Invalid("Adaptive card content must be an AdaptiveCard object.");
        }

        if (Encoding.UTF8.GetByteCount(content.GetRawText()) > MaxCardBytes)
        {
            throw Invalid("An adaptive card exceeds the size limit.");
        }

        CheckDepth(content, 1);
    }

    /// <summary>
    /// Extracts adaptive card attachments from an activity. A card that fails validation is
    /// skipped, because the accompanying text answer is still valid on its own.
    /// </summary>
    public static AdaptiveCardBundle? FromAttachments(
        IEnumerable<(string ContentType, object? Content)> attachments,
        string activityId,
        AgentResponder responder)
    {
        var items = new List<AdaptiveCardItem>();
        var index = 0;
        foreach (var (contentType, raw) in attachments)
        {
            index++;
            if (!string.Equals(contentType, CardContentType, StringComparison.OrdinalIgnoreCase) ||
                raw is null)
            {
                continue;
            }

            try
            {
                var content = raw switch
                {
                    JsonElement element => element.Clone(),
                    JsonDocument document => document.RootElement.Clone(),
                    _ => JsonSerializer.SerializeToElement(raw, raw.GetType())
                };
                ValidateContent(content);
                var fallback = content.TryGetProperty("fallbackText", out var explicitFallback) &&
                               explicitFallback.ValueKind == JsonValueKind.String &&
                               !string.IsNullOrWhiteSpace(explicitFallback.GetString())
                    ? explicitFallback.GetString()!
                    : DeriveFallbackText(content);
                if (string.IsNullOrWhiteSpace(fallback))
                {
                    continue;
                }

                if (fallback.Length > MaxFallbackCharacters)
                {
                    fallback = fallback[..MaxFallbackCharacters];
                }
                items.Add(new AdaptiveCardItem($"{activityId}:{index}", 1, content, fallback, responder));
            }
            catch (Exception exception) when (exception is AdaptiveCardFormatException or JsonException
                                                  or NotSupportedException)
            {
                // Skip malformed cards; the text answer remains.
            }
        }

        if (items.Count == 0)
        {
            return null;
        }

        var bundle = new AdaptiveCardBundle(items.Take(MaxCardsPerTurn).ToArray());
        try
        {
            Validate(bundle);
            return bundle;
        }
        catch (AdaptiveCardFormatException)
        {
            return null;
        }
    }

    public static string DeriveFallbackText(JsonElement content)
    {
        var blocks = new List<string>();
        CollectText(content, blocks, 0);
        return string.Join(Environment.NewLine, blocks);
    }

    private static void CollectText(JsonElement element, List<string> blocks, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectText(item, blocks, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.GetString() == "TextBlock" &&
                element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(text.GetString()))
            {
                blocks.Add(text.GetString()!);
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    CollectText(property.Value, blocks, depth + 1);
                }
            }
        }
    }

    private static void CheckDepth(JsonElement element, int depth)
    {
        if (depth > MaxDepth)
        {
            throw Invalid("An adaptive card is nested too deeply.");
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CheckDepth(item, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                CheckDepth(property.Value, depth + 1);
            }
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static AdaptiveCardFormatException Invalid(string message) => new(message);
}
