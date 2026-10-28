using System.Globalization;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;

namespace FoundryCopilotA2A.KnowledgeApi.Streaming;

/// <summary>Plain-language status lines for each knowledge tool.</summary>
internal static class ToolStatus
{
    private const int MaxQuoteLength = 60;

    public static string Started(string tool, JsonElement arguments, TimeZoneInfo timeZone) => tool switch
    {
        "graph_list_recent_mail" => ReadBoolean(arguments, "unreadOnly")
            ? "Reading unread messages in your Inbox"
            : "Reading your latest Inbox messages",
        "graph_search_mail" => WithQuery("Searching your mailbox", arguments),
        "graph_list_calendar" => CalendarRange(arguments, timeZone),
        "graph_list_todo_lists" => "Listing your To Do lists",
        "graph_list_todo_tasks" => "Reading tasks in your To Do list",
        "graph_list_my_planner_tasks" => "Checking your Planner tasks",
        "graph_list_plan_tasks" => "Reading tasks in a Planner plan",
        "graph_search_teams" => WithQuery("Searching Teams messages", arguments),
        "graph_search_people" => WithQuery("Looking up people", arguments),
        "search_documents" => WithQuery("Searching your documents", arguments),
        DocumentRetrieval.ToolName => "Reading selected documents",
        A2aKnowledgeService.ListToolName => "Finding APIM knowledge agents",
        A2aKnowledgeService.AskToolName => ReadString(arguments, "agentId") is { } agent
            ? $"Asking APIM agent {Quote(agent)}"
            : "Asking an APIM knowledge agent",
        "graph_propose_mail_draft" => "Drafting an email for your confirmation",
        "graph_propose_mail_reply_draft" => "Drafting a reply for your confirmation",
        "graph_propose_mail_read_state" => "Preparing a read/unread change for your confirmation",
        "graph_propose_calendar_event" => "Preparing a calendar event for your confirmation",
        "graph_propose_todo_task" => "Preparing a To Do task for your confirmation",
        "graph_propose_todo_task_status" => "Preparing a To Do status change for your confirmation",
        "graph_propose_planner_task_progress" => "Preparing a Planner progress change for your confirmation",
        _ => "Consulting a knowledge source"
    };

    public static string Completed(string tool, int count) => tool switch
    {
        "graph_list_recent_mail" => $"Read {Count(count, "Inbox message")}",
        "graph_search_mail" => $"Found {Count(count, "matching message")}",
        "graph_list_calendar" => $"Found {Count(count, "calendar event")}",
        "graph_list_todo_lists" => $"Found {Count(count, "To Do list")}",
        "graph_list_todo_tasks" => $"Found {Count(count, "To Do task")}",
        "graph_list_my_planner_tasks" or "graph_list_plan_tasks" => $"Found {Count(count, "Planner task")}",
        "graph_search_teams" => $"Found {Count(count, "Teams message")}",
        "graph_search_people" => count == 1 ? "Found 1 person" : $"Found {count} people",
        "search_documents" => $"Found {Count(count, "document passage")}",
        A2aKnowledgeService.ListToolName => $"Found {Count(count, "APIM knowledge agent")}",
        _ => $"Received {Count(count, "result")}"
    };

    public static string Proposed(string title) => $"Staged {Quote(title)} for your confirmation";

    public static string Answered(string agent) => $"{agent} answered";

    public static string Failed(string tool) => tool switch
    {
        "graph_list_recent_mail" or "graph_search_mail" => "Mail could not be read",
        "graph_list_calendar" => "Calendar could not be read",
        "graph_list_todo_lists" or "graph_list_todo_tasks" => "To Do could not be read",
        "graph_list_my_planner_tasks" or "graph_list_plan_tasks" => "Planner could not be read",
        "graph_search_teams" => "Teams could not be searched",
        "graph_search_people" => "People could not be searched",
        "search_documents" => "Documents could not be searched",
        DocumentRetrieval.ToolName => "Document reading or analysis could not be completed",
        A2aKnowledgeService.ListToolName or A2aKnowledgeService.AskToolName => "The APIM agent could not answer",
        _ when tool.StartsWith("graph_propose_", StringComparison.Ordinal) => "The change could not be prepared",
        _ => "A knowledge source could not be read"
    };

    private static string CalendarRange(JsonElement arguments, TimeZoneInfo timeZone)
    {
        if (!TryReadDate(arguments, "start", out var start) || !TryReadDate(arguments, "end", out var end) || end <= start)
        {
            return "Reading your calendar";
        }

        var first = TimeZoneInfo.ConvertTime(start, timeZone);
        var last = TimeZoneInfo.ConvertTime(end.AddTicks(-1), timeZone);
        return first.Date == last.Date
            ? $"Reading your calendar for {Day(first)}"
            : $"Reading your calendar for {Day(first)} – {Day(last)}";
    }

    private static string Day(DateTimeOffset value) =>
        value.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    private static bool TryReadDate(JsonElement arguments, string name, out DateTimeOffset value)
    {
        value = default;
        return ReadString(arguments, name) is { } text &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    internal static string WithQuery(string action, JsonElement arguments) =>
        ReadString(arguments, "query") is { } query ? $"{action} for {Quote(query)}" : action;

    internal static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Quote(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"“{(line.Length > MaxQuoteLength ? line[..MaxQuoteLength] + "…" : line)}”";
    }

    private static string? ReadString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;
}
