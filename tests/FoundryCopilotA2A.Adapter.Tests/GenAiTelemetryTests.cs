using System.Diagnostics;

namespace FoundryCopilotA2A.Adapter.Tests;

/// <summary>
/// The GenAI semantic conventions are what let the Aspire dashboard and any OTLP backend render
/// the agent chain as a GenAI trace instead of as opaque HTTP calls. The attribute names are
/// still at "Development" stability, so pin them here: a silent rename upstream would otherwise
/// degrade the traces without failing anything.
/// </summary>
public class GenAiTelemetryTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public GenAiTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GenAiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private static string? Tag(Activity activity, string name) =>
        activity.GetTagItem(name) as string;

    [Fact]
    public void InvokeAgentSpanFollowsGenAiConventions()
    {
        using (GenAiTelemetry.StartInvokeAgent(
                   GenAiTelemetry.Providers.AzureAiInference,
                   "Foundry Web Research",
                   "web-research",
                   "ctx-1"))
        {
        }

        var activity = Assert.Single(_activities);
        // The convention requires "invoke_agent {gen_ai.agent.name}".
        Assert.Equal("invoke_agent Foundry Web Research", activity.DisplayName);
        // A call leaving the process to a remote agent must be CLIENT.
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("invoke_agent", Tag(activity, "gen_ai.operation.name"));
        Assert.Equal("azure.ai.inference", Tag(activity, "gen_ai.provider.name"));
        Assert.Equal("Foundry Web Research", Tag(activity, "gen_ai.agent.name"));
        Assert.Equal("web-research", Tag(activity, "gen_ai.agent.id"));
        Assert.Equal("ctx-1", Tag(activity, "gen_ai.conversation.id"));
    }

    [Fact]
    public void ExecuteToolSpanFollowsGenAiConventions()
    {
        using (GenAiTelemetry.StartExecuteTool("Reverser Classic", "agent", "ctx-2"))
        {
        }

        var activity = Assert.Single(_activities);
        Assert.Equal("execute_tool Reverser Classic", activity.DisplayName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal("execute_tool", Tag(activity, "gen_ai.operation.name"));
        Assert.Equal("Reverser Classic", Tag(activity, "gen_ai.tool.name"));
        Assert.Equal("agent", Tag(activity, "gen_ai.tool.type"));
        Assert.Equal("ctx-2", Tag(activity, "gen_ai.conversation.id"));
    }

    [Fact]
    public void ChainWorkflowSpanNestsTheAgentAndToolSpans()
    {
        using (GenAiTelemetry.StartChainWorkflow("web-research", "reverser-classic"))
        using (GenAiTelemetry.StartInvokeAgent(
                   GenAiTelemetry.Providers.AzureAiInference, "Foundry Web Research"))
        using (GenAiTelemetry.StartExecuteTool("Reverser Classic", "agent"))
        {
        }

        var tool = _activities.Single(activity => activity.DisplayName.StartsWith("execute_tool"));
        var agent = _activities.Single(activity => activity.DisplayName.StartsWith("invoke_agent"));
        var workflow = _activities.Single(activity =>
            activity.DisplayName.StartsWith("invoke_workflow"));

        Assert.Equal("invoke_workflow web-research->reverser-classic", workflow.DisplayName);
        Assert.Equal("invoke_workflow", Tag(workflow, "gen_ai.operation.name"));
        // The chain must read as one trace: tool nested in agent, agent nested in workflow.
        Assert.Equal(agent.SpanId, tool.ParentSpanId);
        Assert.Equal(workflow.SpanId, agent.ParentSpanId);
        Assert.Equal(workflow.TraceId, tool.TraceId);
    }

    [Fact]
    public void FailureRecordsErrorTypeAndErrorStatus()
    {
        using (var failing = GenAiTelemetry.StartInvokeAgent(
                   GenAiTelemetry.Providers.CopilotStudio, "Reverser Classic"))
        {
            GenAiTelemetry.RecordFailure(failing, new InvalidOperationException("boom"));
        }

        var activity = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            "System.InvalidOperationException",
            Tag(activity, "error.type"));
    }

    [Fact]
    public void OptionalAttributesAreOmittedRatherThanEmpty()
    {
        using (GenAiTelemetry.StartInvokeAgent(
                   GenAiTelemetry.Providers.CopilotStudio, "Reverser Classic"))
        {
        }

        var activity = Assert.Single(_activities);
        // Emitting empty strings would show up as blank attributes in every backend.
        Assert.Null(activity.GetTagItem("gen_ai.conversation.id"));
        Assert.Null(activity.GetTagItem("gen_ai.agent.id"));
        Assert.Null(activity.GetTagItem("gen_ai.request.model"));
    }
}
