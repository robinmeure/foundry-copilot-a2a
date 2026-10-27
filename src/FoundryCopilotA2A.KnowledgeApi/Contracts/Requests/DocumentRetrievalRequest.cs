using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;

/// <summary>Arguments supplied to the focused, contextual, or exhaustive document reader tool.</summary>
public sealed record DocumentRetrievalRequest(string Mode, string Query, string[] DocumentIds)
{
    public static readonly string[] Modes =
        ["focused", "contextual", "whole_document", "exhaustive", "comparison"];

    public static DocumentRetrievalRequest Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(property => property.Name is not ("mode" or "query" or "documentIds")) ||
            !arguments.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String ||
            !Modes.Contains(mode.GetString(), StringComparer.Ordinal) ||
            !arguments.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(query.GetString()) || query.GetString()!.Length > 4000 ||
            !arguments.TryGetProperty("documentIds", out var ids) || ids.ValueKind != JsonValueKind.Array ||
            ids.GetArrayLength() is < 1 or > 4 ||
            ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 128))
        {
            throw new ApiException(
                400,
                "invalid_tool_arguments",
                "Document retrieval requires a supported mode, a question of 1-4,000 characters, and 1-4 available document IDs.");
        }

        var documentIds = ids.EnumerateArray().Select(id => id.GetString()!).ToArray();
        if (documentIds.Distinct(StringComparer.Ordinal).Count() != documentIds.Length ||
            (mode.GetString() == "comparison" && documentIds.Length < 2))
        {
            throw new ApiException(
                400,
                "invalid_tool_arguments",
                "Use distinct document IDs; comparison requires at least two documents.");
        }

        return new(mode.GetString()!, query.GetString()!, documentIds);
    }
}
