extern alias A2AV1;

using A2A;
using ContentConverter = A2AV1::Microsoft.Extensions.AI.AIContentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

namespace FoundryCopilotA2A.Adapter;

public sealed class PlanExtensionContext
{
    private readonly AsyncLocal<bool> _active = new();

    public bool Active
    {
        get => _active.Value;
        set => _active.Value = value;
    }
}

#pragma warning disable MEAI001
internal sealed class PlanStreamingAgentHandler(
    AIHostAgent agent,
    PlanExtensionContext extension,
    ILogger<PlanStreamingAgentHandler> logger) : IAgentHandler
{
    public static IAgentHandler Create(IServiceProvider services, object? key)
    {
        var inner = services.GetRequiredKeyedService<AIAgent>(key);
        var isolation = services.GetService<AgentIsolationKeyProvider>();
        var store = services.GetKeyedService<AgentSessionStore>(key) ?? new NoopAgentSessionStore();
        if (store.GetService<IsolationKeyScopedAgentSessionStore>() is null)
        {
            store = new IsolationKeyScopedAgentSessionStore(store, isolation,
                new IsolationKeyScopedAgentSessionStoreOptions { Strict = isolation is not null });
        }
        return new PlanStreamingAgentHandler(new AIHostAgent(inner, store),
            services.GetRequiredService<PlanExtensionContext>(),
            services.GetRequiredService<ILogger<PlanStreamingAgentHandler>>());
    }

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue queue, CancellationToken cancellationToken)
    {
        if (context.Message?.ReferenceTaskIds is { Count: > 0 })
        {
            throw new AdapterRequestException("ReferenceTaskIds is not supported by this adapter.");
        }
        var session = await agent.GetOrCreateSessionAsync(context.ContextId, cancellationToken);
        var messages = ReadMessages(context);
        var options = CreateRunOptions(context);
        var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
        var artifact = new PendingArtifact(updater);
        try
        {
            if (!context.StreamingResponse)
            {
                var response = await agent.RunAsync(messages, session, options, cancellationToken);
                await WriteResponseAsync(context, queue, updater, response, cancellationToken);
                return;
            }

            if (!context.IsContinuation)
            {
                await updater.SubmitAsync(cancellationToken);
            }
            await updater.StartWorkAsync(cancellationToken: cancellationToken);
            var eventCount = 0;
            await foreach (var update in agent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                var parts = update.Contents.Select(ContentConverter.ToPart).OfType<Part>().ToList();
                var plans = ReadPlanUpdates(parts);
                if (plans.Count > 0)
                {
                    // Flush answer data before a late plan update, without delaying the plan
                    // until another answer chunk arrives.
                    if (artifact.HasPending)
                    {
                        await artifact.FlushAsync(lastChunk: false, cancellationToken);
                    }
                    foreach (var plan in plans)
                    {
                        if (++eventCount > AgentPlanUpdates.MaxEvents)
                        {
                            throw new AdapterRequestException("The plan stream exceeds the event limit.");
                        }
                        List<Part> progress = [Part.FromText(AgentPlanUpdates.Summary(plan))];
                        if (extension.Active)
                        {
                            progress.Add(AgentPlanUpdates.ToPart(plan));
                        }
                        await updater.StartWorkAsync(new Message
                        {
                            MessageId = plan.EventId,
                            Role = Role.Agent,
                            TaskId = context.TaskId,
                            ContextId = context.ContextId,
                            Parts = progress
                        }, cancellationToken);
                    }
                    parts.RemoveAll(IsPlanOrInformative);
                }
                if (parts.Count > 0)
                {
                    await artifact.SetNextAsync(parts, cancellationToken);
                }
            }
            await artifact.FlushAsync(lastChunk: true, cancellationToken);
            await updater.CompleteAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await artifact.FlushAsync(lastChunk: true, CancellationToken.None);
            await updater.CancelAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A2A execution failed for task {TaskId}.", context.TaskId);
            await artifact.FlushAsync(lastChunk: true, CancellationToken.None);
            await updater.FailAsync(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Role = Role.Agent,
                Parts = [Part.FromText("The delegated agent could not complete the request.")]
            }, CancellationToken.None);
            throw;
        }
        finally
        {
            await agent.SaveSessionAsync(context.ContextId, session, CancellationToken.None);
        }
    }

    private static List<ChatMessage> ReadMessages(RequestContext context)
    {
        if (context.IsContinuation)
        {
            return context.Task?.History?.Select(message => message.ToChatMessage()).ToList() ?? [];
        }

        if (context.Message is { } message)
        {
            return [message.ToChatMessage()];
        }

        return [];
    }

    private static AgentRunOptions CreateRunOptions(RequestContext context)
    {
        var properties = new AdditionalPropertiesDictionary();
        if (context.Metadata is not null)
        {
            foreach (var pair in context.Metadata)
            {
                properties[pair.Key] = pair.Value;
            }
        }

        if (context.Configuration is not null)
        {
            properties["a2a.configuration"] = context.Configuration;
        }

        return new AgentRunOptions { AdditionalProperties = properties };
    }

    private static async Task WriteResponseAsync(
        RequestContext context,
        AgentEventQueue queue,
        TaskUpdater updater,
        AgentResponse response,
        CancellationToken cancellationToken)
    {
        var parts = response.Messages.SelectMany(message => message.Contents)
            .Select(ContentConverter.ToPart).OfType<Part>().ToList();
        if (context.IsContinuation)
        {
            await updater.AddArtifactAsync(parts, lastChunk: true, cancellationToken: cancellationToken);
            await updater.CompleteAsync(cancellationToken: cancellationToken);
            return;
        }

        await queue.EnqueueMessageAsync(new Message
        {
            MessageId = response.ResponseId ?? Guid.NewGuid().ToString("N"),
            ContextId = context.ContextId,
            Role = Role.Agent,
            Parts = parts
        }, cancellationToken);
    }

    private static List<AgentPlanUpdate> ReadPlanUpdates(IEnumerable<Part> parts)
    {
        var plans = new List<AgentPlanUpdate>();
        foreach (var part in parts)
        {
            if (part.Data is { } data && AgentPlanUpdates.ReadContainer(data) is { } plan)
            {
                plans.Add(plan);
            }
        }

        return plans;
    }

    private static bool IsPlanOrInformative(Part part)
    {
        if (part.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data &&
            data.TryGetProperty(AgentPlanUpdates.ExtensionUri, out _))
        {
            return true;
        }

        return part.Metadata?.TryGetValue("isInformative", out var informative) == true &&
            informative.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static bool IsAnswerReplacement(Part part) =>
        part.Metadata?.TryGetValue(CopilotStudioAdapterChatClient.ReplacesAnswerKey, out var value) == true &&
        value.ValueKind == System.Text.Json.JsonValueKind.True;

    // Keep the last chunk pending so success, cancellation and failure can all close the artifact.
    private sealed class PendingArtifact(TaskUpdater updater)
    {
        private readonly string _id = Guid.NewGuid().ToString("N");
        private List<Part>? _pending;
        private bool _append;

        public bool HasPending => _pending is not null;

        public async Task SetNextAsync(List<Part> parts, CancellationToken cancellationToken)
        {
            if (HasPending)
            {
                await FlushAsync(lastChunk: false, cancellationToken);
            }

            // A canonical final answer must replace, rather than extend, earlier streamed text.
            if (parts.Any(IsAnswerReplacement))
            {
                _append = false;
            }

            _pending = parts;
        }

        public async Task FlushAsync(bool lastChunk, CancellationToken cancellationToken)
        {
            if (_pending is null && !_append)
            {
                return;
            }

            await updater.AddArtifactAsync(
                _pending ?? [],
                artifactId: _id,
                lastChunk: lastChunk,
                append: _append,
                cancellationToken: cancellationToken);
            _append = true;
            _pending = null;
        }
    }
}
#pragma warning restore MEAI001
