using System.Text;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.A2A;
using FoundryCopilotA2A.KnowledgeApi.Services.Chat.Tools;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.Retrieval;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Chat;

// Tool registration, dispatch and per-tool handlers for a single turn.
public sealed partial class ResponsesCoordinator
{
    private const int MaxDocumentRetrievalArgumentPayloadLength = 32768;
    private const string KamervragenCoverage =
        "Top matching Kamervragen excerpts; results are not exhaustive and text/metadata may be shortened.";

    /// <summary>Mutable state shared by every tool call within one turn.</summary>
    private sealed class TurnState
    {
        public required HttpContext Context { get; init; }
        public required string[] Sources { get; init; }
        public required UserContext User { get; init; }
        public required ChatProgress Progress { get; init; }
        public IReadOnlyCollection<string>? SelectedAgents { get; init; }
        public Func<string, CancellationToken, Task<Passage[]>>? SearchDocuments { get; init; }
        public Func<DocumentRetrievalRequest, CancellationToken, Task<DocumentRetrievalResult>>? RetrieveDocuments { get; init; }

        public required List<Passage> Evidence { get; init; }
        public required List<string> Warnings { get; init; }
        public HashSet<string> DocumentEvidenceIds { get; } = new(StringComparer.Ordinal);
        public List<GraphActionProposal> Proposals { get; } = [];
        public int DocumentResultBytes { get; set; }
        public bool HadPrivateContext { get; set; }
        public ApiException? FatalFailure { get; set; }

        // Document retrieval has its own budget, so it extends the general evidence capacity.
        public int EvidenceCapacity => MaxEvidence + DocumentEvidenceIds.Count;

        public ValueTask ReportAsync(string tool, string message, string state, CancellationToken ct) =>
            Progress.ReportAsync(new ChatStatus(message, state, tool), ct);

        public void ThrowIfFatal()
        {
            if (FatalFailure is not null)
            {
                throw FatalFailure;
            }
        }
    }

    private List<AITool> CreateTools(TurnState turn, bool hasDocumentSearchTool)
    {
        var tools = new List<AITool>();
        foreach (var definition in BuildToolDefinitions(turn, hasDocumentSearchTool))
        {
            var json = JsonSerializer.SerializeToElement(definition);
            var name = json.GetProperty("name").GetString()!;
            tools.Add(new KnowledgeFunction(name,
                json.GetProperty("description").GetString()!,
                json.GetProperty("parameters"),
                (arguments, token) => ExecuteToolAsync(turn, name, arguments, token)));
        }
        return tools;
    }

    private IEnumerable<object> BuildToolDefinitions(TurnState turn, bool hasDocumentSearchTool)
    {
        foreach (var tool in GraphSkill.Enabled(turn.Sources))
        {
            yield return tool.Definition;
        }
        foreach (var tool in GraphActions.Enabled(turn.Sources))
        {
            yield return tool.Definition;
        }
        if (hasDocumentSearchTool)
        {
            yield return SearchDocumentsToolDefinition;
            if (turn.RetrieveDocuments is not null)
            {
                yield return DocumentRetrieval.ToolDefinition;
            }
        }
        if (turn.Sources.Contains(KamervragenSkill.Source) && kamervragen.Enabled)
        {
            yield return KamervragenSkill.ToolDefinition;
        }
        if (turn.Sources.Contains("a2a") && a2a.Enabled)
        {
            foreach (var definition in A2aKnowledgeService.ToolDefinitions)
            {
                yield return definition;
            }
        }
    }

    private async Task<object> ExecuteToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        turn.HadPrivateContext = true;
        try
        {
            ValidateToolCall(turn, name, arguments);
            await turn.ReportAsync(name, ToolStatus.Started(name, arguments, turn.User.TimeZone), ChatStatus.Started, ct);
            return await DispatchToolAsync(turn, name, arguments, ct);
        }
        catch (ApiException exception) when (exception.Status is 400 or 403 or 404 or 409 or 429)
        {
            logger.LogWarning("Tool {Tool} failed with {Code}", name, exception.Code);
            turn.Warnings.Add($"{name}: {exception.Message}");
            await turn.ReportAsync(name, ToolStatus.Failed(name), ChatStatus.Failed, ct);
            return new { error = exception.Code, message = exception.Message, isError = true };
        }
        catch (ApiException exception)
        {
            // The framework normally reports function exceptions to the model. Auth and
            // upstream failures must instead escape to the HTTP middleware.
            turn.FatalFailure = exception;
            await turn.ReportAsync(name, ToolStatus.Failed(name), ChatStatus.Failed, ct);
            throw;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Knowledge tool {Tool} failed with HTTP {Status}", name, exception.StatusCode);
            turn.FatalFailure = new ApiException(502, "knowledge_tool_unavailable",
                "A knowledge source could not be reached. Check service connectivity.");
            throw turn.FatalFailure;
        }
        catch (JsonException)
        {
            logger.LogWarning("Knowledge tool {Tool} returned invalid JSON", name);
            turn.FatalFailure = new ApiException(502, "invalid_tool_response",
                "A knowledge source returned an invalid response.");
            throw turn.FatalFailure;
        }
    }

    private static void ValidateToolCall(TurnState turn, string name, JsonElement arguments)
    {
        if (FunctionInvokingChatClient.CurrentContext?.CallContent.Exception is not null)
        {
            throw new ApiException(400, "invalid_tool_arguments", "The tool received malformed JSON arguments.");
        }

        var isDocumentRetrieval = name == DocumentRetrieval.ToolName;
        var argumentLimit = isDocumentRetrieval ? MaxDocumentRetrievalArgumentPayloadLength : MaxToolArgumentPayloadLength;
        if (arguments.GetRawText().Length > argumentLimit)
        {
            throw new ApiException(400, "invalid_tool_arguments", "Tool arguments exceed the allowed size.");
        }

        var generalEvidenceCount = turn.Evidence.Count - turn.DocumentEvidenceIds.Count;
        if (!isDocumentRetrieval && generalEvidenceCount >= MaxEvidence)
        {
            throw new ApiException(429, "evidence_budget", "The turn's evidence budget is full.");
        }
    }

    private Task<object> DispatchToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        if (name == DocumentRetrieval.ToolName)
        {
            return RetrieveDocumentsToolAsync(turn, name, arguments, ct);
        }
        if (name == SearchDocumentsToolName)
        {
            return SearchDocumentsToolAsync(turn, name, arguments, ct);
        }
        if (name == KamervragenSkill.ToolName)
        {
            return SearchKamervragenToolAsync(turn, name, arguments, ct);
        }
        if (name == A2aKnowledgeService.ListToolName)
        {
            return ListA2aAgentsToolAsync(turn, name, arguments, ct);
        }
        if (name == A2aKnowledgeService.AskToolName)
        {
            return AskA2aAgentToolAsync(turn, arguments, name, ct);
        }
        if (GraphActions.IsActionTool(name))
        {
            return ProposeGraphActionToolAsync(turn, name, arguments, ct);
        }
        return ReadGraphToolAsync(turn, name, arguments, ct);
    }

    private async Task<object> SearchDocumentsToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        var passages = await turn.SearchDocuments!(ReadQuery(arguments), ct);
        await turn.ReportAsync(name, ToolStatus.Completed(name, passages.Length), ChatStatus.Completed, ct);
        return AppendDocumentPassages(passages, turn.Evidence, capacity: turn.EvidenceCapacity);
    }

    private async Task<object> SearchKamervragenToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        var passages = await kamervragen.SearchAsync(arguments, turn.Sources, ct);
        await turn.ReportAsync(name, ToolStatus.Completed(name, passages.Length), ChatStatus.Completed, ct);
        return AppendDocumentPassages(passages, turn.Evidence, KamervragenCoverage,
            passages.Length >= KamervragenSkill.MaxResults, turn.EvidenceCapacity);
    }

    private async Task<object> ListA2aAgentsToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        var agents = await a2a.ListAsync(turn.Context, arguments, ct, turn.SelectedAgents);
        await turn.ReportAsync(name, ToolStatus.Completed(name, agents.Length), ChatStatus.Completed, ct);
        return agents;
    }

    private async Task<object> AskA2aAgentToolAsync(TurnState turn, JsonElement arguments, string name, CancellationToken ct)
    {
        var passage = await a2a.AskAsync(turn.Context, arguments, ct, turn.SelectedAgents);
        await turn.ReportAsync(name, ToolStatus.Answered(passage.OriginatingAgent?.Name ?? "The APIM agent"),
            ChatStatus.Completed, ct);
        return AppendDocumentPassages([passage], turn.Evidence, capacity: turn.EvidenceCapacity);
    }

    private async Task<object> ProposeGraphActionToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        if (turn.Proposals.Count >= GraphActions.MaxProposalsPerTurn)
        {
            throw new ApiException(429, "action_budget",
                $"At most {GraphActions.MaxProposalsPerTurn} changes can be proposed per turn.");
        }

        var proposal = await graphActions.ProposeAsync(
            turn.Context, name, arguments, turn.Sources, ct, turn.User.GraphTimeZone);
        turn.Proposals.Add(proposal);
        await turn.ReportAsync(name, ToolStatus.Proposed(proposal.Title), ChatStatus.Completed, ct);
        return new
        {
            status = "awaiting_user_confirmation",
            title = proposal.Title,
            summary = proposal.Summary,
            note = "Nothing has changed yet. The user sees a confirmation card and must confirm this exact change."
        };
    }

    private async Task<object> ReadGraphToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        var result = await graph.Execute(turn.Context, name, arguments, turn.Sources, ct, turn.User.GraphTimeZone);
        await turn.ReportAsync(name, ToolStatus.Completed(name, result.Items.Length), ChatStatus.Completed, ct);
        return AppendEvidence(name, result, turn.Evidence, turn.EvidenceCapacity);
    }

    private async Task<object> RetrieveDocumentsToolAsync(TurnState turn, string name, JsonElement arguments, CancellationToken ct)
    {
        var request = DocumentRetrievalRequest.Parse(arguments);
        var retrieved = await turn.RetrieveDocuments!(request, ct);

        var retrievedIds = retrieved.Passages.Select(passage => passage.Id).ToHashSet(StringComparer.Ordinal);
        if (turn.DocumentEvidenceIds.Union(retrievedIds).Count() > DocumentRetrieval.MaxChunks)
        {
            throw new ApiException(413, "document_retrieval_budget",
                "The turn exceeds the 2,000-chunk document evidence budget. Read fewer documents.");
        }

        var citationNumbers = AssignCitationNumbers(turn.Evidence, retrieved.Passages, out var newPassages);
        var output = BuildDocumentRetrievalOutput(retrieved, citationNumbers);

        var outputBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(output));
        if (turn.DocumentResultBytes + outputBytes > DocumentRetrieval.MaxResultBytes)
        {
            throw new ApiException(413, "document_analysis_budget",
                "Document evidence exceeds the turn's answer context budget. Narrow the question; evidence was not truncated.");
        }

        turn.DocumentResultBytes += outputBytes;
        turn.Evidence.AddRange(newPassages);
        turn.DocumentEvidenceIds.UnionWith(retrievedIds);
        await turn.ReportAsync(name,
            $"Read {retrieved.Passages.Length} document passages ({retrieved.Mode.Replace('_', ' ')})",
            ChatStatus.Completed, ct);
        return output;
    }

    /// <summary>Maps passage ids to 1-based citation numbers, numbering not-yet-cited passages after existing evidence.</summary>
    private static Dictionary<string, int> AssignCitationNumbers(
        List<Passage> evidence, Passage[] retrieved, out Passage[] newPassages)
    {
        var numbers = evidence
            .Select((passage, index) => (passage.Id, Number: index + 1))
            .ToDictionary(entry => entry.Id, entry => entry.Number, StringComparer.Ordinal);

        newPassages = retrieved.Where(passage => !numbers.ContainsKey(passage.Id)).ToArray();
        foreach (var passage in newPassages)
        {
            numbers.Add(passage.Id, numbers.Count + 1);
        }
        return numbers;
    }

    private static object BuildDocumentRetrievalOutput(DocumentRetrievalResult retrieved, Dictionary<string, int> citationNumbers)
    {
        var passagesById = retrieved.Passages.ToDictionary(passage => passage.Id, StringComparer.Ordinal);
        return new
        {
            mode = retrieved.Mode,
            documents = retrieved.Coverage.Select(document => new
            {
                id = document.DocumentId,
                title = document.Title,
                expectedChunks = document.ExpectedChunks,
                readChunks = document.ReadChunks
            }).ToArray(),
            complete = retrieved.Coverage.All(document => document.ExpectedChunks == document.ReadChunks),
            synthesized = retrieved.Synthesized,
            coverage = retrieved.Synthesized
                ? "All reported chunks were analyzed in batches. These are derived findings, not a verbatim transcript. Cite the supporting original passages."
                : "Counts describe extracted chunks supplied as evidence. Focused/contextual modes are not full-document reads.",
            items = retrieved.Findings.Select(finding => new
            {
                content = finding.Text,
                sources = finding.PassageIds.Select(id => new
                {
                    citation = CitationFor(citationNumbers[id]),
                    documentId = passagesById[id].DocumentId,
                    title = passagesById[id].Title,
                    locator = passagesById[id].Locator,
                    pageNumber = passagesById[id].PageNumber,
                    passageNumber = passagesById[id].PassageNumber
                }).ToArray()
            }).ToArray()
        };
    }

    private static string ReadQuery(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(property => property.Name != "query") ||
            !arguments.TryGetProperty("query", out var query) ||
            query.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(query.GetString()) ||
            query.GetString()!.Length > 2000)
        {
            throw new ApiException(400, "invalid_tool_arguments",
                "Document search requires only a non-empty 'query' string of at most 2,000 characters.");
        }
        return query.GetString()!;
    }

    private static string CitationFor(int number) => $"[[cite:{number}]]";

    /// <summary>Returns the 1-based citation number for the passage, adding it to the evidence when new.</summary>
    private static int CitationNumberFor(List<Passage> evidence, string passageId, Func<Passage> createPassage)
    {
        var index = evidence.FindIndex(entry => entry.Id == passageId);
        if (index < 0)
        {
            evidence.Add(createPassage());
            index = evidence.Count - 1;
        }
        return index + 1;
    }

    private static object AppendDocumentPassages(
        Passage[] passages,
        List<Passage> evidence,
        string coverage = "Authorized evidence returned by this knowledge tool.",
        bool truncated = false,
        int capacity = MaxEvidence)
    {
        var selected = passages.Take(capacity - evidence.Count).ToArray();
        var items = selected.Select(passage => new
        {
            citation = CitationFor(CitationNumberFor(evidence, passage.Id, () => passage)),
            title = passage.Title,
            locator = passage.Locator,
            pageNumber = passage.PageNumber,
            passageNumber = passage.PassageNumber,
            content = passage.Content,
            url = passage.Url
        }).ToArray();

        return new
        {
            items,
            truncated = truncated || selected.Length < passages.Length,
            coverage
        };
    }

    private static object AppendEvidence(
        string toolName,
        GraphToolResult result,
        List<Passage> evidence,
        int capacity = MaxEvidence)
    {
        var selected = result.Items.Take(capacity - evidence.Count).ToArray();
        var items = selected.Select(item =>
        {
            var passageId = $"{toolName}:{item.GetProperty("id").GetString()}";
            return new
            {
                citation = CitationFor(CitationNumberFor(evidence, passageId, () => ToGraphPassage(toolName, passageId, item))),
                data = item
            };
        }).ToArray();

        return new
        {
            items,
            truncated = result.Truncated || selected.Length < result.Items.Length,
            coverage = result.Coverage
        };
    }

    private static Passage ToGraphPassage(string toolName, string passageId, JsonElement item)
    {
        var title = CandidateTitleProperties
            .Select(propertyName => item.TryGetProperty(propertyName, out var value) ? value.GetString() : null)
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)) ?? toolName;
        var webLink = item.TryGetProperty("webLink", out var linkElement) ? linkElement.GetString() : null;

        return new Passage(
            Id: passageId,
            DocumentId: toolName,
            Title: title,
            Content: item.GetRawText(),
            Locator: $"Live Graph / {toolName}",
            Url: webLink);
    }
}
