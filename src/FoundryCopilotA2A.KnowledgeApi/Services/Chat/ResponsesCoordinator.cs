using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Infrastructure;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Chat.Tools;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001 // Explicitly disable server-side storage on Responses requests.

namespace FoundryCopilotA2A.KnowledgeApi.Services.Chat;

/// <summary>
/// Runs bounded tool-assisted turns and validates citations against authorized evidence.
/// Split across partial files: prompts (.Instructions), tool execution and evidence (.Tools), mock turns (.Mock).
/// </summary>
public sealed partial class ResponsesCoordinator(
    IAgentModelClient model,
    GraphSkill graph,
    ILogger<ResponsesCoordinator> logger,
    IA2aKnowledgeService a2a,
    GraphActions graphActions,
    KamervragenSkill kamervragen) : IResponsesCoordinator
{
    public const int MaxToolCalls = 8;
    public const int MaxRounds = 6;
    private const int MaxEvidence = 80;
    private const int MaxToolArgumentPayloadLength = 4096;
    private const int DefaultMaxOutputTokens = 3000;
    private const int DocumentRetrievalMaxOutputTokens = 12000;
    private const string SearchDocumentsToolName = "search_documents";
    private static readonly Regex CitationMarker = new(@"\[\[cite:([^\]\r\n]*)\]\]",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public const string NoEvidence =
        "I could not find authorized evidence. Import a document or enable a Microsoft 365 source.";

    private static readonly string[] CandidateTitleProperties = ["subject", "title", "displayName"];

    private static readonly object SearchDocumentsToolDefinition = new
    {
        type = "function",
        name = SearchDocumentsToolName,
        description = "Search the user's uploaded and SharePoint-imported documents in this conversation using hybrid vector and keyword search. Call this when the user asks about uploaded files, project documents, or facts that could be in their conversation documents.",
        parameters = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["query"] = new
                {
                    type = "string",
                    description = "Search query or keywords to look up in the conversation's indexed documents."
                }
            },
            required = new[] { "query" },
            additionalProperties = false
        }),
        strict = true
    };

    public async Task<GroundedAnswer> Run(
        HttpContext context,
        string question,
        Passage[] initial,
        string[] sources,
        string[] initialWarnings,
        CancellationToken ct,
        IReadOnlyList<string>? availableDocumentNames = null,
        Func<string, CancellationToken, Task<Passage[]>>? searchDocumentsAsync = null,
        JsonElement? sessionState = null,
        UserContext? user = null,
        ChatProgress? progress = null,
        IReadOnlyCollection<string>? selectedAgents = null,
        Func<DocumentRetrievalRequest, CancellationToken, Task<DocumentRetrievalResult>>? retrieveDocumentsAsync = null)
    {
        EnsureSourcesConfigured(sources);
        progress ??= ChatProgress.None;
        var userContext = user ?? UserContext.Utc(DateTimeOffset.UtcNow);

        var hasDocumentSearchTool = searchDocumentsAsync is not null &&
            (availableDocumentNames is null || availableDocumentNames.Count > 0);
        var turn = new TurnState
        {
            Context = context,
            Sources = sources,
            User = userContext,
            Progress = progress,
            SelectedAgents = selectedAgents,
            SearchDocuments = searchDocumentsAsync,
            RetrieveDocuments = retrieveDocumentsAsync,
            Evidence = initial.ToList(),
            Warnings = initialWarnings.ToList(),
            HadPrivateContext = initial.Length > 0 || initialWarnings.Length > 0 || availableDocumentNames is { Count: > 0 }
        };

        var tools = CreateTools(turn, hasDocumentSearchTool);
        var history = new ConversationHistory();
        using var bounded = new BoundedChatClient(model.Create(),
            tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal),
            turn.ThrowIfFatal);
        using var functions = new FunctionInvokingChatClient(bounded)
        {
            AllowConcurrentInvocation = false,
            MaximumIterationsPerRequest = MaxRounds,
            IncludeDetailedErrors = false
        };
        var allowsDocumentRetrieval = retrieveDocumentsAsync is not null && hasDocumentSearchTool;
        var agent = CreateAgent(functions, history, BuildInstructions(userContext, sources), tools, allowsDocumentRetrieval);

        var session = await history.LoadAsync(agent, sessionState, ct);
        await progress.ReportAsync(new ChatStatus("Working out what you need"), ct);
        var resultText = await AgentTurnRunner.RunAsync(
            agent,
            BuildInitialUserPrompt(question, initial, initialWarnings, availableDocumentNames, sources),
            session,
            progress,
            ct);
        turn.ThrowIfFatal();

        var answer = SelectAnswer(turn, initialWarnings.Length, resultText);
        ValidateCitations(answer, turn.Evidence.Count);
        answer = CitationMarker.Replace(answer, marker => $"[{marker.Groups[1].Value}]");
        return new GroundedAnswer(answer, turn.Evidence.ToArray(), turn.Warnings.ToArray())
        {
            SessionState = await history.CompleteAsync(agent, question, answer, turn.HadPrivateContext, ct),
            Actions = turn.Proposals.ToArray()
        };
    }

    public static void ValidateCitations(string answer, int count)
    {
        foreach (Match marker in CitationMarker.Matches(answer))
        {
            var isValidCitation =
                int.TryParse(marker.Groups[1].Value, out var citationNumber) &&
                citationNumber >= 1 &&
                citationNumber <= count &&
                marker.Groups[1].Value == citationNumber.ToString(CultureInfo.InvariantCulture);

            if (!isValidCitation)
            {
                throw new ApiException(
                    StatusCodes.Status502BadGateway,
                    "invalid_citation",
                    "The model returned a citation outside the authorized evidence set.");
            }
        }
    }

    private void EnsureSourcesConfigured(string[] sources)
    {
        if (sources.Contains("a2a") && !a2a.Enabled)
        {
            throw new ApiException(400, "a2a_disabled", "APIM knowledge agents are not configured.");
        }
        if (sources.Contains(KamervragenSkill.Source) && !kamervragen.Enabled)
        {
            throw new ApiException(400, "kamervragen_disabled", "Kamervragen search is not configured.");
        }
    }

    private static AIAgent CreateAgent(
        FunctionInvokingChatClient functions,
        ConversationHistory history,
        string instructions,
        List<AITool> tools,
        bool allowsDocumentRetrieval)
    {
        var agent = new ChatClientAgent(functions, new ChatClientAgentOptions
        {
            Name = "local-knowledge-orchestrator",
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = history.Provider,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools,
                AllowMultipleToolCalls = false,
                MaxOutputTokens = allowsDocumentRetrieval ? DocumentRetrievalMaxOutputTokens : DefaultMaxOutputTokens,
                RawRepresentationFactory = _ => new CreateResponseOptions
                {
                    StoredOutputEnabled = false,
                    IncludedProperties = { "reasoning.encrypted_content" }
                }
            }
        });

        return agent.AsBuilder()
            .UseOpenTelemetry("FoundryCopilotA2A.KnowledgeApi", telemetry => telemetry.EnableSensitiveData = false)
            .Build();
    }

    private static string SelectAnswer(TurnState turn, int initialWarningCount, string resultText)
    {
        var onlyToolFailures = turn.Evidence.Count == 0 &&
            turn.Proposals.Count == 0 &&
            turn.Warnings.Count > initialWarningCount;
        var answer = onlyToolFailures
            ? "Knowledge retrieval returned no authorized evidence. Some tools failed; review the source warnings."
            : resultText;

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ApiException(502, "empty_response", "The model returned no answer text.");
        }
        return answer;
    }
}
