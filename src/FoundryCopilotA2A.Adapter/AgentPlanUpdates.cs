using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using A2A;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.Adapter;

public sealed record AgentPlanUpdate(
    string EventId, int Sequence, string Name, AgentResponder OriginatingAgent,
    JsonElement Value, bool DetailsFiltered = false)
{
    public string SchemaVersion => "1";
}

internal static partial class AgentPlanUpdates
{
    public const string ExtensionUri = "urn:foundry-copilot-a2a:dynamic-plan:v1";
    public const string Header = "A2A-Extensions";
    public const string LegacyHeader = "X-A2A-Extensions";
    public const int MaxEvents = 256;
    public const int MaxPayloadBytes = 16384;
    public const string Description =
        "Optional ordered plan activity with bounded, redacted arguments and observations. Not answer content.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> Names =
    [
        "DynamicPlanReceived", "DynamicPlanStepTriggered",
        "DynamicPlanStepBindUpdate", "DynamicPlanStepFinished"
    ];

    public static AgentPlanUpdate? ReadActivity(IActivity activity, AgentResponder responder, int sequence)
    {
        if (activity.Type != "event" || activity.Name is not { } name || !Names.Contains(name))
        {
            return null;
        }
        var value = activity.Value switch
        {
            JsonElement element => element,
            JsonDocument document => document.RootElement,
            _ => throw new AdapterRequestException("A plan activity must contain a JSON object.")
        };
        var (sanitized, filtered) = SanitizeValue(value);
        var update = new AgentPlanUpdate(
            string.IsNullOrEmpty(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id,
            sequence, name, responder, sanitized, filtered);
        Validate(update);
        return update;
    }

    public static string Summary(AgentPlanUpdate update)
    {
        var tool = update.Value.TryGetProperty("taskDialogId", out var dialog)
            ? dialog.GetString() : "step";
        return update.Name switch
        {
            "DynamicPlanReceived" => update.Value.TryGetProperty("isFinalPlan", out var final) && final.GetBoolean()
                ? "Plan updated." : "Planning next steps...",
            "DynamicPlanStepTriggered" => $"Running {tool}...",
            "DynamicPlanStepBindUpdate" => $"Updating inputs for {tool}...",
            _ => update.Value.TryGetProperty("state", out var state) && state.GetString() == "failed"
                ? $"Step failed: {tool}." : $"Finished {tool}."
        };
    }

    public static Part ToPart(AgentPlanUpdate update)
    {
        Validate(update);
        return Part.FromData(JsonSerializer.SerializeToElement(
            new Dictionary<string, AgentPlanUpdate> { [ExtensionUri] = update }, JsonOptions));
    }

    public static AIContent ToContent(AgentPlanUpdate update) => ToPart(update).ToAIContent();

    public static AgentPlanUpdate? ReadContainer(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(ExtensionUri, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != "1" ||
            value.GetRawText().Length > MaxPayloadBytes)
        {
            throw new AdapterRequestException("Unsupported or oversized plan update.");
        }
        AgentPlanUpdate update;
        try
        {
            update = value.Deserialize<AgentPlanUpdate>(JsonOptions)
                ?? throw new AdapterRequestException("An empty plan update is invalid.");
        }
        catch (JsonException exception)
        {
            throw new AdapterRequestException("The plan update has an invalid shape.", exception);
        }
        Validate(update);
        var (sanitized, filtered) = SanitizeValue(update.Value);
        return update with { Value = sanitized, DetailsFiltered = update.DetailsFiltered || filtered };
    }

    public static void Validate(AgentPlanUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.EventId) || update.EventId.Length > 256 ||
            update.Sequence is < 1 or > MaxEvents || !Names.Contains(update.Name) ||
            update.OriginatingAgent is not { Id.Length: > 0 and <= 256, Name.Length: > 0 and <= 256 } ||
            update.Value.ValueKind != JsonValueKind.Object ||
            !update.Value.TryGetProperty("planIdentifier", out var plan) ||
            plan.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(plan.GetString()) ||
            plan.GetString()!.Length > 256 ||
            JsonSerializer.SerializeToUtf8Bytes(update, JsonOptions).Length > MaxPayloadBytes)
        {
            throw new AdapterRequestException("The plan update is invalid or exceeds the supported limits.");
        }
    }

    private static (JsonElement Value, bool Filtered) SanitizeValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new AdapterRequestException("A plan value must be an object.");
        }
        var filtered = false;
        var budget = 4096;
        var result = new JsonObject();
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "planIdentifier": case "stepId": case "taskDialogId": case "state": case "type":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        property.Value.GetString() is not { Length: > 0 and <= 256 } text)
                    {
                        throw new AdapterRequestException("A plan identifier or state has an invalid value.");
                    }
                    result[property.Name] = text;
                    break;
                case "isFinalPlan":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new AdapterRequestException("A plan final flag must be a boolean.");
                    }
                    result[property.Name] = property.Value.GetBoolean();
                    break;
                case "steps": case "autoFilledArguments": case "arguments": case "observation":
                    result[property.Name] = SanitizeDetail(property.Value, 0, ref budget, ref filtered);
                    break;
                default:
                    filtered = true;
                    break;
            }
        }
        return (JsonSerializer.SerializeToElement(result), filtered);
    }

    private static JsonNode? SanitizeDetail(JsonElement value, int depth, ref int budget, ref bool filtered)
    {
        if (depth > 5 || budget <= 0)
        {
            filtered = true;
            return JsonValue.Create("[truncated]");
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject();
                var count = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (++count > 16 || budget <= 0) { filtered = true; break; }
                    if (SensitiveKey().IsMatch(property.Name) || property.Name.Length > 80)
                    {
                        filtered = true;
                        continue;
                    }
                    budget -= property.Name.Length;
                    result[property.Name] = SanitizeDetail(property.Value, depth + 1, ref budget, ref filtered);
                }
                return result;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    if (array.Count == 8 || budget <= 0) { filtered = true; break; }
                    array.Add(SanitizeDetail(item, depth + 1, ref budget, ref filtered));
                }
                return array;
            case JsonValueKind.String:
                var text = value.GetString()!;
                var safe = SecretValue().Replace(text, "[redacted]");
                safe = UrlValue().Replace(safe, match =>
                {
                    if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[redacted URL]";
                    return uri.GetLeftPart(UriPartial.Path);
                });
                // Never retain URL credentials, even when the URL parser accepts user information.
                safe = Regex.Replace(safe, @"(?i)(https?://)[^/\s@]+@", "$1[redacted]@", RegexOptions.None,
                    TimeSpan.FromMilliseconds(100));
                if (safe != text) filtered = true;
                var length = Math.Min(512, Math.Max(0, budget));
                if (safe.Length > length) { safe = safe[..length] + " [truncated]"; filtered = true; }
                budget -= safe.Length;
                return JsonValue.Create(safe);
            case JsonValueKind.Number:
                budget -= 32;
                return JsonNode.Parse(value.GetRawText());
            case JsonValueKind.True: case JsonValueKind.False:
                budget--;
                return JsonValue.Create(value.GetBoolean());
            default:
                return null;
        }
    }

    [GeneratedRegex("token|secret|password|credential|authorization|cookie|key$|connection.?string|thought|recipient|aadobject|^from$|^sourceid$|email", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKey();

    [GeneratedRegex(@"(?i)\bBearer\s+\S+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|(?:password|secret|api[_-]?key|token)\s*[:=]\s*\S+|[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"https?://[^\s""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlValue();
}
