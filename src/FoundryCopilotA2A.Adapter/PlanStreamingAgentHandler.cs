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
    public bool Active { get => _active.Value; set => _active.Value = value; }
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
        var messages = context.IsContinuation
            ? context.Task?.History?.Select(message => message.ToChatMessage()).ToList() ?? []
            : context.Message is { } message ? [message.ToChatMessage()] : new List<ChatMessage>();
        var properties = new AdditionalPropertiesDictionary();
        if (context.Metadata is not null)
        {
            foreach (var pair in context.Metadata) properties[pair.Key] = pair.Value;
        }
        if (context.Configuration is not null) properties["a2a.configuration"] = context.Configuration;
        var options = new AgentRunOptions { AdditionalProperties = properties };
        var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
        var artifactId = Guid.NewGuid().ToString("N");
        var append = false;
        List<Part>? pending = null;
        async Task FlushAsync(bool lastChunk, CancellationToken token)
        {
            if (pending is null && !append) return;
            await updater.AddArtifactAsync(pending ?? [], artifactId: artifactId,
                lastChunk: lastChunk, append: append, cancellationToken: token);
            append = true;
            pending = null;
        }
        try
        {
            if (!context.StreamingResponse)
            {
                var response = await agent.RunAsync(messages, session, options, cancellationToken);
                var parts = response.Messages.SelectMany(message => message.Contents)
                    .Select(ContentConverter.ToPart).OfType<Part>().ToList();
                if (context.IsContinuation)
                {
                    await updater.AddArtifactAsync(parts, lastChunk: true, cancellationToken: cancellationToken);
                    await updater.CompleteAsync(cancellationToken: cancellationToken);
                }
                else
                {
                    await queue.EnqueueMessageAsync(new Message
                    {
                        MessageId = response.ResponseId ?? Guid.NewGuid().ToString("N"),
                        ContextId = context.ContextId, Role = Role.Agent, Parts = parts
                    }, cancellationToken);
                }
                return;
            }

            if (!context.IsContinuation) await updater.SubmitAsync(cancellationToken);
            await updater.StartWorkAsync(cancellationToken: cancellationToken);
            var eventCount = 0;
            await foreach (var update in agent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                var parts = update.Contents.Select(ContentConverter.ToPart).OfType<Part>().ToList();
                var plans = parts.Where(part => part.Data is not null)
                    .Select(part => AgentPlanUpdates.ReadContainer(part.Data!.Value))
                    .OfType<AgentPlanUpdate>().ToArray();
                if (plans.Length > 0)
                {
                    // Flush answer data before a late plan update, without delaying the plan
                    // until another answer chunk arrives.
                    if (pending is not null) await FlushAsync(false, cancellationToken);
                    foreach (var plan in plans)
                    {
                        if (++eventCount > AgentPlanUpdates.MaxEvents)
                        {
                            throw new AdapterRequestException("The plan stream exceeds the event limit.");
                        }
                        List<Part> progress = [Part.FromText(AgentPlanUpdates.Summary(plan))];
                        if (extension.Active) progress.Add(AgentPlanUpdates.ToPart(plan));
                        await updater.StartWorkAsync(new Message
                        {
                            MessageId = plan.EventId, Role = Role.Agent, TaskId = context.TaskId,
                            ContextId = context.ContextId, Parts = progress
                        }, cancellationToken);
                    }
                    parts.RemoveAll(part =>
                        part.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data &&
                            data.TryGetProperty(AgentPlanUpdates.ExtensionUri, out _) ||
                        part.Metadata?.TryGetValue("isInformative", out var informative) == true &&
                            informative.ValueKind == System.Text.Json.JsonValueKind.True);
                }
                if (parts.Count > 0)
                {
                    if (pending is not null) await FlushAsync(false, cancellationToken);
                    pending = parts;
                }
            }
            await FlushAsync(true, cancellationToken);
            await updater.CompleteAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FlushAsync(true, CancellationToken.None);
            await updater.CancelAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A2A execution failed for task {TaskId}.", context.TaskId);
            await FlushAsync(true, CancellationToken.None);
            await updater.FailAsync(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"), Role = Role.Agent,
                Parts = [Part.FromText("The delegated agent could not complete the request.")]
            }, CancellationToken.None);
            throw;
        }
        finally
        {
            await agent.SaveSessionAsync(context.ContextId, session, CancellationToken.None);
        }
    }
}
#pragma warning restore MEAI001
