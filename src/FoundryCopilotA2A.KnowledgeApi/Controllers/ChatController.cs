using AGUI.Abstractions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Citations;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Streaming;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.KnowledgeApi.Controllers;

[Route("api/chat")]
public sealed class ChatController(
    IConfiguration configuration,
    IKnowledgeService knowledge,
    IOptions<JsonOptions> jsonOptions,
    ILogger<ChatController> logger) : KnowledgeControllerBase(configuration)
{
    private static readonly OriginatingAgentInfo KnowledgeAgent = new(
        Id: "knowledge",
        Name: "Local orchestrator");

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Chat(
        [FromBody] ChatRequest input,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        var result = await knowledge.Chat(HttpContext, caller, input, cancellationToken);
        return Ok(ToResponse(result));
    }

    /// <summary>
    /// Runs the same chat turn as <see cref="Chat"/> as an AG-UI run. The request is an AG-UI
    /// <see cref="RunAgentInput"/> (see <see cref="AgUiRunInput"/>); the response is an AG-UI
    /// event stream whose <c>RUN_FINISHED</c> result is the validated <see cref="ChatResponse"/>.
    /// Failures after streaming starts end the run with <c>RUN_ERROR</c> and a
    /// <see cref="ChatStreamError"/> in its metadata.
    /// </summary>
    [HttpPost("stream")]
    public async Task Stream([FromBody] RunAgentInput? input, CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        var serializerOptions = jsonOptions.Value.JsonSerializerOptions;
        var request = AgUiRunInput.ToChatRequest(input, serializerOptions);
        var runId = string.IsNullOrWhiteSpace(input!.RunId) ? Guid.NewGuid().ToString("N") : input.RunId;
        var stream = new AgUiEventStream(HttpContext, request.ConversationId, runId, serializerOptions);
        try
        {
            var result = await knowledge.Chat(HttpContext, caller, request, cancellationToken, stream);
            await stream.FinishAsync(ToResponse(result), cancellationToken);
        }
        catch (Exception exception) when (stream.Started && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Headers are already sent, so the failure becomes the final event instead of a problem response.
            logger.LogWarning(exception, "Streamed knowledge chat failed");
            var failure = KnowledgeExceptionMiddleware.MapToApiException(exception);
            await stream.FailAsync(new ChatStreamError(
                failure.Status,
                failure.Code,
                failure.Message,
                ResponseSignals.Get(HttpContext, ResponseSignals.Challenge),
                ResponseSignals.Get(HttpContext, ResponseSignals.RetryAfter)), CancellationToken.None);
        }
    }

    private static ChatResponse ToResponse(KnowledgeChatResult result)
    {
        var sources = result.Evidence
            .Select((passage, index) => new CitationSource(
                Id: (index + 1).ToString(),
                Title: passage.Title,
                Url: passage.Url,
                OriginatingAgent: passage.OriginatingAgent ?? KnowledgeAgent))
            .ToArray();

        var citations = result.Evidence
            .Select((passage, index) => new CitationReference(
                SourceId: (index + 1).ToString(),
                Marker: $"[{index + 1}]",
                Locator: passage.Locator))
            .ToArray();

        return new ChatResponse(
            Answer: result.Answer,
            Warnings: result.Warnings,
            Citations: new CitationPayload(
                SchemaVersion: "1",
                Sources: sources,
                Citations: citations))
        {
            Actions = result.Actions,
            Files = result.Files
        };
    }
}
