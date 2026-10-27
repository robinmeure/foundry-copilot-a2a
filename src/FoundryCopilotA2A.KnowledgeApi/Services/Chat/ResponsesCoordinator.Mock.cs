using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using Microsoft.Agents.AI;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Chat;

// Keep deterministic orchestration separate while sharing the live turn's evidence rules.
public sealed partial class ResponsesCoordinator
{
    private static readonly Regex MockTaskRequest = new(@"\b(create|add)\b.*\btask\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MockTimeQuestion = new(@"\b(time|date|today|day)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Exercises tools, history and streaming deterministically without model or Azure calls.</summary>
    public async Task<GroundedAnswer> RunMock(
        HttpContext context,
        string question,
        Passage[] initial,
        string[] sources,
        string[] initialWarnings,
        CancellationToken ct,
        JsonElement? sessionState = null,
        UserContext? user = null,
        ChatProgress? progress = null,
        IReadOnlyCollection<string>? selectedAgents = null)
    {
        progress ??= ChatProgress.None;
        var userContext = user ?? UserContext.Utc(DateTimeOffset.UtcNow);
        var localNow = TimeZoneInfo.ConvertTime(userContext.Now, userContext.TimeZone);
        var evidence = initial.ToList();
        var enabledSkillSources = sources
            .Where(GraphSkill.Sources.Contains)
            .Distinct();

        async Task<GraphToolResult> ExecuteGraphAsync(string toolName, JsonElement arguments)
        {
            await progress.ReportAsync(new ChatStatus(
                ToolStatus.Started(toolName, arguments, userContext.TimeZone), ChatStatus.Started, toolName), ct);
            var result = await graph.Execute(context, toolName, arguments, sources, ct, userContext.GraphTimeZone);
            await progress.ReportAsync(new ChatStatus(
                ToolStatus.Completed(toolName, result.Items.Length), ChatStatus.Completed, toolName), ct);
            return result;
        }

        foreach (var source in enabledSkillSources)
        {
            var toolName = source switch
            {
                "mail" => "graph_search_mail",
                "calendar" => "graph_list_calendar",
                "todo" => "graph_list_todo_lists",
                "teams" => "graph_search_teams",
                "people" => "graph_search_people",
                _ => "graph_list_my_planner_tasks"
            };

            object arguments = source switch
            {
                "mail" or "teams" or "people" => new { query = question[..Math.Min(question.Length, 200)] },
                "calendar" => new
                {
                    start = localNow.ToString("O", CultureInfo.InvariantCulture),
                    end = localNow.AddDays(7).ToString("O", CultureInfo.InvariantCulture)
                },
                _ => new { }
            };

            var serializedArguments = JsonSerializer.SerializeToElement(arguments);
            AppendEvidence(toolName, await ExecuteGraphAsync(toolName, serializedArguments), evidence);

            if (source == "todo")
            {
                var todoTasksArguments = JsonSerializer.SerializeToElement(new { listId = "mock-list" });
                AppendEvidence("graph_list_todo_tasks",
                    await ExecuteGraphAsync("graph_list_todo_tasks", todoTasksArguments), evidence);
            }
        }

        if (sources.Contains(KamervragenSkill.Source))
        {
            var arguments = JsonSerializer.SerializeToElement(new { query = question[..Math.Min(question.Length, 2000)] });
            await progress.ReportAsync(new ChatStatus(
                ToolStatus.Started(KamervragenSkill.ToolName, arguments, userContext.TimeZone),
                ChatStatus.Started, KamervragenSkill.ToolName), ct);
            var passages = await kamervragen.SearchAsync(arguments, sources, ct);
            evidence.AddRange(passages);
            await progress.ReportAsync(new ChatStatus(
                ToolStatus.Completed(KamervragenSkill.ToolName, passages.Length), ChatStatus.Completed, KamervragenSkill.ToolName), ct);
        }

        if (sources.Contains("a2a"))
        {
            var listArguments = JsonSerializer.SerializeToElement(new { });
            await progress.ReportAsync(new ChatStatus(
                ToolStatus.Started(A2aKnowledgeService.ListToolName, listArguments, userContext.TimeZone),
                ChatStatus.Started, A2aKnowledgeService.ListToolName), ct);
            var agents = await a2a.ListAsync(context, listArguments, ct, selectedAgents);
            await progress.ReportAsync(new ChatStatus(
                ToolStatus.Completed(A2aKnowledgeService.ListToolName, agents.Length),
                ChatStatus.Completed, A2aKnowledgeService.ListToolName), ct);
            if (agents.Length > 0)
            {
                var arguments = JsonSerializer.SerializeToElement(new { agentId = agents[0].Id, question });
                await progress.ReportAsync(new ChatStatus(
                    ToolStatus.Started(A2aKnowledgeService.AskToolName, arguments, userContext.TimeZone),
                    ChatStatus.Started, A2aKnowledgeService.AskToolName), ct);
                var passage = await a2a.AskAsync(context, arguments, ct, selectedAgents);
                evidence.Add(passage);
                await progress.ReportAsync(new ChatStatus(
                    ToolStatus.Answered(passage.OriginatingAgent?.Name ?? "The APIM agent"),
                    ChatStatus.Completed, A2aKnowledgeService.AskToolName), ct);
            }
        }

        var proposals = new List<GraphActionProposal>();
        if (sources.Contains("todo") && MockTaskRequest.IsMatch(question))
        {
            var arguments = JsonSerializer.SerializeToElement(new
            {
                listId = "mock-list",
                title = question.Length > 120 ? question[..120] : question,
                dueDate = "",
                body = ""
            });
            proposals.Add(await graphActions.ProposeAsync(
                context, "graph_propose_todo_task", arguments, sources, ct, userContext.GraphTimeZone));
            await progress.ReportAsync(new ChatStatus(
                ToolStatus.Proposed(proposals[^1].Title), ChatStatus.Completed, "graph_propose_todo_task"), ct);
        }

        var answer = proposals.Count > 0
            ? "Mock proposal (no model call): review the staged To Do task below and confirm it to apply the change."
            : evidence.Count == 0 && MockTimeQuestion.IsMatch(question)
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Mock answer (no model call): it is {localNow:dddd d MMMM yyyy, HH:mm} in {userContext.TimeZone.Id} (UTC{localNow:zzz}, from {userContext.TimeZoneSource}).")
            : evidence.Count == 0
                ? NoEvidence
                : $"Mock grounded answer (no model call): {evidence[0].Content} [1]";

        var history = new ConversationHistory();
        using var mockClient = new MockChatClient(answer);
        var agent = new ChatClientAgent(mockClient, new ChatClientAgentOptions
        {
            Name = "local-knowledge-orchestrator",
            ChatHistoryProvider = history.Provider
        });
        var session = await history.LoadAsync(agent, sessionState, ct);
        var resultText = await AgentTurnRunner.RunAsync(agent, question, session, progress, ct);
        return new GroundedAnswer(resultText, evidence.ToArray(), initialWarnings)
        {
            SessionState = await history.CompleteAsync(agent, question, resultText, evidence.Count > 0, ct),
            Actions = proposals.ToArray()
        };
    }

}
