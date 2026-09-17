using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FoundryCopilotA2A.Adapter;

internal static class AdapterTelemetry
{
    public const string ActivitySourceName = "FoundryCopilotA2A.Adapter";
    public const string MeterName = "FoundryCopilotA2A.Adapter";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> CopilotStudioInvocationDuration =
        Meter.CreateHistogram<double>(
            "copilot_studio.client.invoke.duration",
            "s",
            "Copilot Studio invocation duration through the adapter.");
    private static readonly Histogram<double> CopilotStudioConversationStartDuration =
        Meter.CreateHistogram<double>(
            "copilot_studio.client.conversation.start.duration",
            "s",
            "Time required to create a Copilot Studio conversation.");
    private static readonly Histogram<double> CopilotStudioAnswerStreamDuration =
        Meter.CreateHistogram<double>(
            "copilot_studio.client.answer_stream.duration",
            "s",
            "Duration of a Copilot Studio answer activity stream.");
    private static readonly Histogram<double> CopilotStudioTimeToFirstActivity =
        Meter.CreateHistogram<double>(
            "copilot_studio.client.answer_stream.time_to_first_activity",
            "s",
            "Time from starting an answer stream to receiving its first activity.");
    private static readonly Histogram<double> CopilotStudioTimeToFirstProgress =
        Meter.CreateHistogram<double>(
            "copilot_studio.client.answer_stream.time_to_first_progress",
            "s",
            "Time from starting an answer stream to forwarding its first progress text.");
    private static readonly Histogram<double> CopilotStudioTimeToFirstAnswer =
        Meter.CreateHistogram<double>(
            "copilot_studio.client.answer_stream.time_to_first_answer",
            "s",
            "Time from starting an answer stream to forwarding its first answer text.");
    private static readonly Histogram<double> ApiManagementDiscoveryResolveDuration =
        Meter.CreateHistogram<double>(
            "apim.discovery.resolve.duration",
            "s",
            "Duration of resolving the cached API Management agent catalog.");
    private static readonly Histogram<double> ApiManagementDiscoveryLockWaitDuration =
        Meter.CreateHistogram<double>(
            "apim.discovery.lock_wait.duration",
            "s",
            "Time spent waiting for another API Management catalog refresh.");
    private static readonly Histogram<double> ApiManagementDiscoveryRefreshDuration =
        Meter.CreateHistogram<double>(
            "apim.discovery.refresh.duration",
            "s",
            "Duration of an API Management agent catalog refresh.");
    private static readonly Histogram<double> ApiManagementDiscoveryStageDuration =
        Meter.CreateHistogram<double>(
            "apim.discovery.stage.duration",
            "s",
            "Duration of a stage within an API Management agent catalog refresh.");
    private static readonly Histogram<double> ApiManagementA2AInvocationDuration =
        Meter.CreateHistogram<double>(
            "apim.a2a.invoke.duration",
            "s",
            "Duration of an A2A invocation through API Management.");

    public enum StreamMilestone
    {
        FirstActivity,
        FirstProgress,
        FirstAnswer
    }

    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal) =>
        Source.StartActivity(name, kind);

    public static void RecordCopilotStudioInvocation(
        Activity? activity,
        TimeSpan elapsed,
        string agentId,
        bool completed)
    {
        activity?.SetTag("copilot_studio.invoke.completed", completed);
        var tags = AgentTags(agentId);
        tags.Add("copilot_studio.invoke.completed", completed);
        CopilotStudioInvocationDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordCopilotStudioConversationStart(
        Activity? activity,
        TimeSpan elapsed,
        string agentId,
        bool created)
    {
        activity?.SetTag("copilot_studio.conversation.start.duration_ms", elapsed.TotalMilliseconds);
        activity?.SetTag("copilot_studio.conversation.created", created);
        var tags = AgentTags(agentId);
        tags.Add("copilot_studio.conversation.created", created);
        CopilotStudioConversationStartDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordCopilotStudioStreamMilestone(
        Activity? activity,
        StreamMilestone milestone,
        TimeSpan elapsed,
        string agentId)
    {
        var (eventName, attributeName, histogram) = milestone switch
        {
            StreamMilestone.FirstActivity => (
                "copilot_studio.stream.first_activity",
                "copilot_studio.stream.time_to_first_activity_ms",
                CopilotStudioTimeToFirstActivity),
            StreamMilestone.FirstProgress => (
                "copilot_studio.stream.first_progress",
                "copilot_studio.stream.time_to_first_progress_ms",
                CopilotStudioTimeToFirstProgress),
            StreamMilestone.FirstAnswer => (
                "copilot_studio.stream.first_answer",
                "copilot_studio.stream.time_to_first_answer_ms",
                CopilotStudioTimeToFirstAnswer),
            _ => throw new ArgumentOutOfRangeException(nameof(milestone), milestone, null)
        };

        activity?.SetTag(attributeName, elapsed.TotalMilliseconds);
        activity?.AddEvent(new ActivityEvent(
            eventName,
            tags: new ActivityTagsCollection
            {
                { "copilot_studio.stream.elapsed_ms", elapsed.TotalMilliseconds }
            }));
        histogram.Record(elapsed.TotalSeconds, AgentTags(agentId));
    }

    public static void RecordCopilotStudioAnswerStream(
        Activity? activity,
        TimeSpan elapsed,
        string agentId,
        bool completed)
    {
        activity?.SetTag("copilot_studio.stream.duration_ms", elapsed.TotalMilliseconds);
        activity?.SetTag("copilot_studio.stream.completed", completed);
        var tags = AgentTags(agentId);
        tags.Add("copilot_studio.stream.completed", completed);
        CopilotStudioAnswerStreamDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordApiManagementDiscoveryResolve(
        Activity? activity,
        TimeSpan elapsed,
        string result)
    {
        activity?.SetTag("apim.discovery.cache.result", result);
        var tags = new TagList { { "apim.discovery.cache.result", result } };
        ApiManagementDiscoveryResolveDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordApiManagementDiscoveryLockWait(
        Activity? activity,
        TimeSpan elapsed)
    {
        activity?.SetTag("apim.discovery.lock_wait_ms", elapsed.TotalMilliseconds);
        activity?.AddEvent(new ActivityEvent(
            "apim.discovery.lock_acquired",
            tags: new ActivityTagsCollection
            {
                { "apim.discovery.lock_wait_ms", elapsed.TotalMilliseconds }
            }));
        ApiManagementDiscoveryLockWaitDuration.Record(elapsed.TotalSeconds);
    }

    public static void RecordApiManagementDiscoveryRefresh(
        Activity? activity,
        TimeSpan elapsed,
        int candidateCount,
        int discoveredCount,
        bool completed)
    {
        activity?.SetTag("apim.discovery.candidate.count", candidateCount);
        activity?.SetTag("apim.discovery.agent.count", discoveredCount);
        activity?.SetTag("apim.discovery.skipped.count", candidateCount - discoveredCount);
        activity?.SetTag("apim.discovery.refresh.completed", completed);
        var tags = new TagList { { "apim.discovery.refresh.completed", completed } };
        ApiManagementDiscoveryRefreshDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordApiManagementDiscoveryStage(
        Activity? activity,
        TimeSpan elapsed,
        string stage,
        bool completed)
    {
        activity?.SetTag("apim.discovery.stage", stage);
        activity?.SetTag("apim.discovery.stage.duration_ms", elapsed.TotalMilliseconds);
        activity?.SetTag("apim.discovery.stage.completed", completed);
        var tags = new TagList
        {
            { "apim.discovery.stage", stage },
            { "apim.discovery.stage.completed", completed }
        };
        ApiManagementDiscoveryStageDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordApiManagementA2AInvocation(
        Activity? activity,
        TimeSpan elapsed,
        string? apiId,
        bool completed)
    {
        activity?.SetTag("apim.a2a.invoke.completed", completed);
        var tags = new TagList { { "apim.a2a.invoke.completed", completed } };
        if (!string.IsNullOrWhiteSpace(apiId))
        {
            tags.Add("apim.api.id", apiId);
        }

        ApiManagementA2AInvocationDuration.Record(elapsed.TotalSeconds, tags);
    }

    public static void RecordFailure(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", exception.GetType().FullName);
    }

    /// <summary>
    /// Records a human-readable cause for a failed turn. The A2A host reports a handler that threw
    /// as a generic "no response events" error, which hides why the turn actually failed, so the
    /// adapter keeps the real reason on the span. Bounded, because the text originates from a
    /// backend and must never become an unbounded export channel.
    /// </summary>
    public static void RecordFailureReason(Activity? activity, string? reason)
    {
        if (activity is null || string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        activity.SetTag(
            "adapter.failure.reason",
            reason.Length <= MaximumReasonCharacters
                ? reason
                : reason[..MaximumReasonCharacters] + "...");
    }

    private const int MaximumReasonCharacters = 400;

    private static TagList AgentTags(string agentId)
    {
        var tags = new TagList();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            tags.Add("copilot_studio.agent.id", agentId);
        }

        return tags;
    }
}
