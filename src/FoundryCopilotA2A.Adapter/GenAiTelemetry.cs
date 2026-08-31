using System.Diagnostics;

namespace FoundryCopilotA2A.Adapter;

/// <summary>
/// Emits spans that follow the OpenTelemetry GenAI semantic conventions so the Aspire dashboard
/// and any OTLP backend can render the agent chain as a recognisable GenAI trace rather than as
/// opaque HTTP calls.
/// <para>
/// These conventions are still at "Development" stability, so the attribute names can change in a
/// future release. They are centralised here to keep that churn out of the invokers.
/// </para>
/// </summary>
internal static class GenAiTelemetry
{
    public const string ActivitySourceName = "FoundryCopilotA2A.Adapter.GenAI";
    private static readonly ActivitySource Source = new(ActivitySourceName);

    public static class Attributes
    {
        public const string OperationName = "gen_ai.operation.name";
        public const string ProviderName = "gen_ai.provider.name";
        public const string ConversationId = "gen_ai.conversation.id";
        public const string AgentName = "gen_ai.agent.name";
        public const string AgentId = "gen_ai.agent.id";
        public const string AgentDescription = "gen_ai.agent.description";
        public const string ToolName = "gen_ai.tool.name";
        public const string ToolType = "gen_ai.tool.type";
        public const string ToolDescription = "gen_ai.tool.description";
        public const string RequestModel = "gen_ai.request.model";
        public const string ResponseModel = "gen_ai.response.model";
        public const string ErrorType = "error.type";
    }

    public static class Operations
    {
        public const string InvokeAgent = "invoke_agent";
        public const string ExecuteTool = "execute_tool";
        public const string InvokeWorkflow = "invoke_workflow";
    }

    public static class Providers
    {
        /// <summary>Azure AI Foundry surfaces itself as the Azure AI Inference provider.</summary>
        public const string AzureAiInference = "azure.ai.inference";

        /// <summary>
        /// Copilot Studio has no registered provider value, so use a stable custom identifier
        /// rather than mislabelling it as one of the well-known providers.
        /// </summary>
        public const string CopilotStudio = "microsoft.copilot_studio";
    }

    /// <summary>
    /// Entry point for a chained request. The convention models multi-agent orchestration as a
    /// workflow span so the Agent A to Agent B hop nests underneath a single parent.
    /// </summary>
    public static Activity? StartChainWorkflow(string entryAgentId, string targetAgentId)
    {
        var activity = Source.StartActivity(
            $"{Operations.InvokeWorkflow} {entryAgentId}->{targetAgentId}",
            ActivityKind.Internal);
        activity?.SetTag(Attributes.OperationName, Operations.InvokeWorkflow);
        activity?.SetTag("gen_ai.workflow.name", $"{entryAgentId}->{targetAgentId}");
        return activity;
    }

    /// <summary>
    /// A call that leaves this process to reach a remote agent, so the convention requires
    /// <see cref="ActivityKind.Client"/>.
    /// </summary>
    public static Activity? StartInvokeAgent(
        string providerName,
        string agentName,
        string? agentId = null,
        string? conversationId = null,
        string? requestModel = null,
        string? agentDescription = null)
    {
        var activity = Source.StartActivity(
            $"{Operations.InvokeAgent} {agentName}",
            ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(Attributes.OperationName, Operations.InvokeAgent);
        activity.SetTag(Attributes.ProviderName, providerName);
        activity.SetTag(Attributes.AgentName, agentName);
        SetIfPresent(activity, Attributes.AgentId, agentId);
        SetIfPresent(activity, Attributes.ConversationId, conversationId);
        SetIfPresent(activity, Attributes.RequestModel, requestModel);
        SetIfPresent(activity, Attributes.AgentDescription, agentDescription);
        return activity;
    }

    /// <summary>
    /// The remote agent treats the chained specialist as a tool, so the delegation is recorded as
    /// an execute_tool span nested under the invoking agent.
    /// </summary>
    public static Activity? StartExecuteTool(
        string toolName,
        string toolType,
        string? conversationId = null,
        string? toolDescription = null)
    {
        var activity = Source.StartActivity(
            $"{Operations.ExecuteTool} {toolName}",
            ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(Attributes.OperationName, Operations.ExecuteTool);
        activity.SetTag(Attributes.ToolName, toolName);
        activity.SetTag(Attributes.ToolType, toolType);
        SetIfPresent(activity, Attributes.ConversationId, conversationId);
        SetIfPresent(activity, Attributes.ToolDescription, toolDescription);
        return activity;
    }

    public static void RecordResponseModel(Activity? activity, string? responseModel) =>
        SetIfPresent(activity, Attributes.ResponseModel, responseModel);

    /// <summary>
    /// The convention requires error.type to carry the exception type or an error code, and the
    /// span status to be Error.
    /// </summary>
    public static void RecordFailure(Activity? activity, Exception exception)
    {
        activity?.SetTag(Attributes.ErrorType, exception.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    private static void SetIfPresent(Activity? activity, string name, string? value)
    {
        if (activity is not null && !string.IsNullOrWhiteSpace(value))
        {
            activity.SetTag(name, value);
        }
    }
}
