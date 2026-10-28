using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

internal sealed record CopilotStudioPlanTool(string SchemaName, string DisplayName)
{
    private const string PlanReceivedValueType = "DynamicPlanReceived";
    private const int MaximumToolNameCharacters = 256;

    public static IReadOnlyList<CopilotStudioPlanTool> Read(IActivity activity)
    {
        if (!CopilotStudioDynamicPlan.IsEventType(activity, PlanReceivedValueType) ||
            !CopilotStudioDynamicPlan.TryReadValue(activity.Value, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("toolDefinitions", out var definitions) ||
            definitions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tools = new List<CopilotStudioPlanTool>();
        foreach (var definition in definitions.EnumerateArray())
        {
            if (definition.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var schemaName = CopilotStudioDynamicPlan.ReadString(
                definition,
                "schemaName",
                MaximumToolNameCharacters);
            var displayName = CopilotStudioDynamicPlan.ReadString(
                definition,
                "displayName",
                MaximumToolNameCharacters);
            if (schemaName is not null && displayName is not null)
            {
                tools.Add(new CopilotStudioPlanTool(
                    NormalizeInline(schemaName),
                    NormalizeInline(displayName)));
            }
        }

        return tools;
    }

    private static string NormalizeInline(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}

internal static class CopilotStudioDynamicPlan
{
    private const string ValueTypePrefix = "DynamicPlan";

    public static bool IsDynamicPlanEvent(IActivity activity) =>
        string.Equals(activity.Type, "event", StringComparison.OrdinalIgnoreCase) &&
        (StartsWithValueTypePrefix(activity.ValueType) ||
         StartsWithValueTypePrefix(activity.Name));

    public static bool IsEventType(IActivity activity, string expectedType) =>
        string.Equals(activity.Type, "event", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(activity.ValueType, expectedType, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(activity.Name, expectedType, StringComparison.OrdinalIgnoreCase));

    public static bool TryReadValue(object? value, out JsonElement element)
    {
        switch (value)
        {
            case JsonElement jsonElement:
                element = jsonElement;
                return true;
            case JsonDocument document:
                element = document.RootElement;
                return true;
            default:
                element = default;
                return false;
        }
    }

    public static string? ReadString(
        JsonElement parent,
        string propertyName,
        int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return null;
        }

        var value = property.GetString()!.Trim();
        return value.Length <= maximumLength
            ? value
            : value[..maximumLength] + "...";
    }

    private static bool StartsWithValueTypePrefix(string? value) =>
        value?.StartsWith(ValueTypePrefix, StringComparison.OrdinalIgnoreCase) == true;
}
