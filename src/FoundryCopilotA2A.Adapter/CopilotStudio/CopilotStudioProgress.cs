using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

/// <summary>
/// Turns Copilot Studio activity that carries no answer text (typing indicators and dynamic-plan
/// events) into short informative progress, so the caller sees the agent working before the first
/// answer token. Repeats are suppressed, and nothing is emitted once the answer has started.
/// </summary>
internal sealed class CopilotStudioProgress
{
    public const string WorkingText = "Working...";
    private const string PlanReceivedValueType = "DynamicPlanReceived";
    private const string StepTriggeredValueType = "DynamicPlanStepTriggered";
    private const string StepFinishedValueType = "DynamicPlanStepFinished";
    private const int MaximumStepIdCharacters = 512;
    private const int MaximumListedSteps = 4;

    private readonly Dictionary<string, string> _toolNames = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastText;
    private bool _progressSent;
    private bool _answerStarted;

    public CopilotInvocationUpdate? Next(IActivity activity, string conversationId)
    {
        foreach (var tool in CopilotStudioPlanTool.Read(activity))
        {
            _toolNames[tool.SchemaName] = tool.DisplayName;
        }

        if (_answerStarted)
        {
            return null;
        }

        var text = Describe(activity);
        if (text is null || string.Equals(text, _lastText, StringComparison.Ordinal))
        {
            return null;
        }

        var update = new CopilotInvocationUpdate(text, conversationId, activity.Id, IsInformative: true);
        Observe(update);
        return update;
    }

    /// <summary>Records an update produced elsewhere so progress does not repeat or trail the answer.</summary>
    public void Observe(CopilotInvocationUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Text))
        {
            return;
        }

        if (!update.IsInformative)
        {
            _answerStarted = true;
            return;
        }

        _progressSent = true;
        _lastText = update.Text;
    }

    private string? Describe(IActivity activity)
    {
        if (string.Equals(activity.Type, "typing", StringComparison.OrdinalIgnoreCase))
        {
            return !_progressSent && string.IsNullOrWhiteSpace(activity.Text) ? WorkingText : null;
        }

        if (!CopilotStudioDynamicPlan.IsDynamicPlanEvent(activity) ||
            !CopilotStudioDynamicPlan.TryReadValue(activity.Value, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (CopilotStudioDynamicPlan.IsEventType(activity, PlanReceivedValueType))
        {
            var steps = value.TryGetProperty("steps", out var stepArray) &&
                        stepArray.ValueKind == JsonValueKind.Array
                ? stepArray.EnumerateArray()
                    .Where(step => step.ValueKind == JsonValueKind.String)
                    .Select(step => NameOf(step.GetString()))
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [];
            if (steps.Length == 0)
            {
                return "Planning...";
            }

            var listed = string.Join(", ", steps.Take(MaximumListedSteps));
            return steps.Length > MaximumListedSteps
                ? $"Planning: {listed}, and {steps.Length - MaximumListedSteps} more"
                : $"Planning: {listed}";
        }

        var stepName = NameOf(CopilotStudioDynamicPlan.ReadString(
            value, "taskDialogId", MaximumStepIdCharacters));
        if (CopilotStudioDynamicPlan.IsEventType(activity, StepTriggeredValueType))
        {
            return stepName is null ? "Running a step..." : $"Running {stepName}...";
        }

        if (CopilotStudioDynamicPlan.IsEventType(activity, StepFinishedValueType) &&
            !(value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object))
        {
            return stepName is null ? null : $"Finished {stepName}";
        }

        return null;
    }

    private string? NameOf(string? schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return null;
        }

        if (_toolNames.TryGetValue(schemaName, out var displayName))
        {
            return displayName;
        }

        var name = schemaName[(schemaName.LastIndexOf('.') + 1)..].Trim();
        return name.Length == 0 ? null : name.Replace('\r', ' ').Replace('\n', ' ');
    }
}

internal sealed record CopilotStudioPlanThought(string Text)
{
    private const int MaximumThoughtCharacters = 1_000;

    public CopilotInvocationUpdate ToUpdate(string conversationId, string? responseId) =>
        new($"Thought: {Text}", conversationId, responseId, IsInformative: true);

    public static CopilotStudioPlanThought? Read(IActivity activity)
    {
        if (!CopilotStudioDynamicPlan.IsDynamicPlanEvent(activity) ||
            !CopilotStudioDynamicPlan.TryReadValue(activity.Value, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var thought = CopilotStudioDynamicPlan.ReadString(
            value,
            "thought",
            MaximumThoughtCharacters);
        return thought is null ? null : new CopilotStudioPlanThought(thought);
    }
}

internal sealed record CopilotStudioPlanFailure(
    string? Code,
    string? Message,
    string? TaskDialogId,
    string? ConnectionSettingsUrl)
{
    private const string FinishedValueType = "DynamicPlanStepFinished";
    private const int MaximumCodeCharacters = 128;
    private const int MaximumMessageCharacters = 1_000;
    private const int MaximumTaskDialogIdCharacters = 512;

    public string DiagnosticMessage =>
        Message ?? $"Copilot Studio reported plan-step failure '{Code}'.";

    public bool Matches(string response) =>
        (!string.IsNullOrWhiteSpace(Code) &&
         response.Contains(Code, StringComparison.OrdinalIgnoreCase)) ||
        (Message is not null &&
         response.Contains(Message, StringComparison.Ordinal)) ||
        response.Contains("An error has occurred", StringComparison.OrdinalIgnoreCase);

    public static CopilotStudioPlanFailure? Read(IActivity activity)
    {
        if (!CopilotStudioDynamicPlan.IsEventType(activity, FinishedValueType) ||
            !CopilotStudioDynamicPlan.TryReadValue(activity.Value, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = CopilotStudioDynamicPlan.ReadString(
            error,
            "userErrorCode",
            MaximumCodeCharacters);
        var message = CopilotStudioDynamicPlan.ReadString(
            error,
            "message",
            MaximumMessageCharacters);
        var taskDialogId = CopilotStudioDynamicPlan.ReadString(
            value,
            "taskDialogId",
            MaximumTaskDialogIdCharacters);
        return code is null && message is null
            ? null
            : new CopilotStudioPlanFailure(
                code,
                message,
                taskDialogId,
                CopilotStudioConnectionSettings.TryCreateUrl(activity));
    }
}
