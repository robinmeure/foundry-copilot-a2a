using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Responses;
using Microsoft.AspNetCore.Http.Features;

namespace FoundryCopilotA2A.KnowledgeApi.Streaming;

/// <summary>
/// Writes one chat turn as an AG-UI event stream over server-sent events.
/// </summary>
/// <remarks>
/// <para>
/// The response starts lazily on the first event, so failures before that point still flow
/// through the normal Problem Details middleware. The first frame is always
/// <c>RUN_STARTED</c>; the last is <c>RUN_FINISHED</c> (carrying the validated
/// <see cref="ChatResponse"/> as its result) or <c>RUN_ERROR</c>.
/// </para>
/// <para>
/// AG-UI clients verify that steps are paired by name and that text messages and steps are
/// closed before the run finishes. <see cref="ChatStatus"/> updates do not follow those rules
/// (some phases never complete and parallel tool calls share a name), so this stream keeps
/// the bookkeeping: each user-facing status travels in the step event's <c>metadata</c>, and
/// steps opened or closed only to satisfy the protocol carry no metadata.
/// </para>
/// </remarks>
internal sealed class AgUiEventStream(
    HttpContext context,
    string threadId,
    string runId,
    JsonSerializerOptions jsonOptions) : ChatProgress
{
    private readonly SemaphoreSlim gate = new(1, 1);

    // Open step names per status key (tool name, or message for tool-less phases), oldest first.
    private readonly Dictionary<string, Queue<string>> openSteps = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeStepNames = new(StringComparer.Ordinal);
    private string? messageId;

    /// <summary>True once the SSE response headers and <c>RUN_STARTED</c> have been sent.</summary>
    public bool Started { get; private set; }

    public override bool StreamsText => true;

    public override async ValueTask ReportAsync(ChatStatus status, CancellationToken cancellationToken)
    {
        var key = status.Tool ?? status.Message;
        var metadata = JsonSerializer.SerializeToElement(status, jsonOptions);

        await WithGateAsync(async () =>
        {
            if (status.State == ChatStatus.Started)
            {
                var name = OpenStep(key);
                await WriteEventAsync(new StepStartedEvent { StepName = name, Metadata = metadata }, cancellationToken);
                return;
            }

            if (!openSteps.TryGetValue(key, out var queue) || !queue.TryDequeue(out var stepName))
            {
                // A completion without a matching start still needs a started step to finish.
                stepName = OpenStep(key);
                openSteps[key].Dequeue();
                await WriteEventAsync(new StepStartedEvent { StepName = stepName }, cancellationToken);
            }

            activeStepNames.Remove(stepName);
            await WriteEventAsync(new StepFinishedEvent { StepName = stepName, Metadata = metadata }, cancellationToken);
        }, cancellationToken);
    }

    public override async ValueTask WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        await WithGateAsync(async () =>
        {
            if (messageId is null)
            {
                messageId = Guid.NewGuid().ToString("N");
                await WriteEventAsync(
                    new TextMessageStartEvent { MessageId = messageId, Role = AGUIRoles.Assistant },
                    cancellationToken);
            }

            await WriteEventAsync(new TextMessageContentEvent { MessageId = messageId, Delta = text }, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Closes the streamed answer and any open steps, then sends <c>RUN_FINISHED</c> with the
    /// final response. The result is authoritative; streamed text is only a preview.
    /// </summary>
    public Task FinishAsync(ChatResponse response, CancellationToken cancellationToken) =>
        WithGateAsync(async () =>
        {
            if (messageId is not null)
            {
                await WriteEventAsync(new TextMessageEndEvent { MessageId = messageId }, cancellationToken);
                messageId = null;
            }

            foreach (var stepName in activeStepNames.ToArray())
            {
                await WriteEventAsync(new StepFinishedEvent { StepName = stepName }, cancellationToken);
            }

            activeStepNames.Clear();
            openSteps.Clear();

            await WriteEventAsync(new RunFinishedEvent
            {
                ThreadId = threadId,
                RunId = runId,
                Result = JsonSerializer.SerializeToElement(response, jsonOptions)
            }, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Ends the run with <c>RUN_ERROR</c>. The HTTP status, sign-in challenge and retry delay
    /// travel in <c>metadata</c> so clients can retry exactly as they would for a JSON error.
    /// </summary>
    public Task FailAsync(ChatStreamError failure, CancellationToken cancellationToken) =>
        WithGateAsync(() => WriteEventAsync(new RunErrorEvent
        {
            Message = failure.Message,
            Code = failure.Code,
            Metadata = JsonSerializer.SerializeToElement(failure, jsonOptions)
        }, cancellationToken), cancellationToken);

    /// <summary>Opens a step for <paramref name="key"/> using a name no active step already has.</summary>
    private string OpenStep(string key)
    {
        var name = key;
        for (var suffix = 2; activeStepNames.Contains(name); suffix++)
        {
            name = $"{key} #{suffix}";
        }

        activeStepNames.Add(name);
        if (!openSteps.TryGetValue(key, out var queue))
        {
            openSteps[key] = queue = new Queue<string>();
        }

        queue.Enqueue(name);
        return name;
    }

    private async Task WithGateAsync(Func<Task> write, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await write();
        }
        finally
        {
            gate.Release();
        }
    }

    // Callers must hold the gate.
    private async Task WriteEventAsync(BaseEvent agUiEvent, CancellationToken cancellationToken)
    {
        if (!Started)
        {
            Start();
            await WriteFrameAsync(new RunStartedEvent { ThreadId = threadId, RunId = runId }, cancellationToken);
        }

        await WriteFrameAsync(agUiEvent, cancellationToken);
    }

    private async Task WriteFrameAsync(BaseEvent agUiEvent, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(agUiEvent, AGUIJsonSerializerContext.Default.BaseEvent);
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"), cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private void Start()
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Started = true;
    }
}
