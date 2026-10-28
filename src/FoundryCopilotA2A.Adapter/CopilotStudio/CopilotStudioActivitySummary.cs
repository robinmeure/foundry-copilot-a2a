using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

internal sealed class CopilotStudioActivitySummary
{
    private readonly HashSet<string> _activityTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _planToolDisplayNames =
        new(StringComparer.OrdinalIgnoreCase);
    private CopilotStudioPlanFailure? _latestPlanFailure;

    public int ActivityCount { get; private set; }

    public int MessageCount { get; private set; }

    public int TextMessageCount { get; private set; }

    public int AdaptiveCardTextMessageCount { get; private set; }

    public int AttachmentCount { get; private set; }

    public bool OAuthCardPresent { get; private set; }

    public int PlanFailureCount { get; private set; }

    public int PlanThoughtCount { get; private set; }

    public bool HasTextResponse => TextMessageCount > 0;

    public string ActivityTypes =>
        _activityTypes.Count == 0
            ? "none"
            : string.Join(",", _activityTypes.Order(StringComparer.Ordinal));

    public void Observe(
        IActivity activity,
        bool oauthCardPresent,
        string? effectiveText = null,
        bool extractedFromAdaptiveCard = false)
    {
        ActivityCount++;
        _activityTypes.Add(NormalizeActivityType(activity.Type));
        AttachmentCount += activity.Attachments?.Count ?? 0;
        OAuthCardPresent |= oauthCardPresent;
        foreach (var tool in CopilotStudioPlanTool.Read(activity))
        {
            _planToolDisplayNames[tool.SchemaName] = tool.DisplayName;
        }
        if (CopilotStudioPlanFailure.Read(activity) is { } planFailure)
        {
            PlanFailureCount++;
            _latestPlanFailure = planFailure;
        }
        if (CopilotStudioPlanThought.Read(activity) is not null)
        {
            PlanThoughtCount++;
        }

        if (!string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MessageCount++;
        if (!string.IsNullOrWhiteSpace(effectiveText ?? activity.Text))
        {
            TextMessageCount++;
            if (extractedFromAdaptiveCard)
            {
                AdaptiveCardTextMessageCount++;
            }
        }
    }

    public void Merge(CopilotStudioActivitySummary other)
    {
        ActivityCount += other.ActivityCount;
        MessageCount += other.MessageCount;
        TextMessageCount += other.TextMessageCount;
        AdaptiveCardTextMessageCount += other.AdaptiveCardTextMessageCount;
        AttachmentCount += other.AttachmentCount;
        OAuthCardPresent |= other.OAuthCardPresent;
        PlanFailureCount += other.PlanFailureCount;
        PlanThoughtCount += other.PlanThoughtCount;
        _latestPlanFailure = other._latestPlanFailure ?? _latestPlanFailure;
        foreach (var (schemaName, displayName) in other._planToolDisplayNames)
        {
            _planToolDisplayNames[schemaName] = displayName;
        }
        _activityTypes.UnionWith(other._activityTypes);
    }

    public string EnrichFailureResponse(string response)
    {
        if (_latestPlanFailure is not { } failure || !failure.Matches(response))
        {
            return response;
        }

        var enriched = response.TrimEnd();
        if (failure.Message is not null &&
            !enriched.Contains(failure.Message, StringComparison.Ordinal))
        {
            enriched += Environment.NewLine + Environment.NewLine +
                        $"Failure details: {failure.DiagnosticMessage}";
        }

        var repair = CreateConnectionRepairMessage(failure);
        if (repair is not null &&
            !enriched.Contains(repair.Url, StringComparison.OrdinalIgnoreCase))
        {
            enriched += Environment.NewLine + Environment.NewLine +
                        "CONNECTION REPAIR REQUIRED" +
                        Environment.NewLine +
                        $"Connection: {repair.DisplayName}" +
                        Environment.NewLine +
                        $"Open connection settings: {repair.Url}";
        }

        return enriched;
    }

    private ConnectionRepair? CreateConnectionRepairMessage(CopilotStudioPlanFailure failure)
    {
        if (failure.ConnectionSettingsUrl is null)
        {
            return null;
        }

        var displayName =
            failure.TaskDialogId is not null &&
            _planToolDisplayNames.TryGetValue(failure.TaskDialogId, out var mappedName)
                ? mappedName
                : ExtractConnectorName(failure.Message) ??
                  failure.TaskDialogId ??
                  "the failed connector";
        return new ConnectionRepair(displayName, failure.ConnectionSettingsUrl);
    }

    private static string? ExtractConnectorName(string? message)
    {
        const string prefix = "The connector '";
        if (message is null ||
            !message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = message.IndexOf('\'', prefix.Length);
        return end <= prefix.Length
            ? null
            : message[prefix.Length..end].Replace('\r', ' ').Replace('\n', ' ');
    }

    private sealed record ConnectionRepair(string DisplayName, string Url);

    public string CreateEmptyResponseMessage()
    {
        const string guidance =
            "The adapter requires at least one text message; make sure every published agent " +
            "route sends a final text response.";

        if (_latestPlanFailure is not null)
        {
            return $"Copilot Studio reported a failed plan step: {_latestPlanFailure.DiagnosticMessage}";
        }

        if (ActivityCount == 0)
        {
            return "Copilot Studio completed the delegated request without returning any activities. " +
                   guidance;
        }

        if (MessageCount == 0)
        {
            return $"Copilot Studio returned {ActivityCount} activities (types: {ActivityTypes}), " +
                   $"but none was a message. {guidance}";
        }

        var attachmentDescription = AttachmentCount == 0
            ? string.Empty
            : $" with {AttachmentCount} attachment{(AttachmentCount == 1 ? string.Empty : "s")}";
        return $"Copilot Studio returned {MessageCount} message " +
               $"activit{(MessageCount == 1 ? "y" : "ies")}{attachmentDescription}, but no text. " +
               guidance;
    }

    public void RecordTelemetry(System.Diagnostics.Activity? activity, bool allowOAuthChallenge)
    {
        activity?.SetTag("copilot_studio.activity.count", ActivityCount);
        activity?.SetTag("copilot_studio.activity.types", ActivityTypes);
        activity?.SetTag("copilot_studio.message.count", MessageCount);
        activity?.SetTag("copilot_studio.message.text.count", TextMessageCount);
        activity?.SetTag(
            "copilot_studio.message.adaptive_card_text.count",
            AdaptiveCardTextMessageCount);
        activity?.SetTag("copilot_studio.attachment.count", AttachmentCount);
        activity?.SetTag("copilot_studio.oauth_card.present", OAuthCardPresent);
        activity?.SetTag("copilot_studio.plan.failure.count", PlanFailureCount);
        activity?.SetTag("copilot_studio.plan.thought.count", PlanThoughtCount);
        activity?.SetTag("copilot_studio.response.present", HasTextResponse);

        if (_latestPlanFailure is not null)
        {
            activity?.SetTag("copilot_studio.plan.failure.code", _latestPlanFailure.Code);
            var planFailureException = new CopilotStudioPlanFailureException(
                _latestPlanFailure.DiagnosticMessage);
            AdapterTelemetry.RecordFailure(activity, planFailureException);
            AdapterTelemetry.RecordFailureReason(activity, planFailureException.Message);
            return;
        }

        if (HasTextResponse || (allowOAuthChallenge && OAuthCardPresent))
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return;
        }

        var exception = new CopilotStudioResponseException(CreateEmptyResponseMessage());
        AdapterTelemetry.RecordFailure(activity, exception);
        AdapterTelemetry.RecordFailureReason(activity, exception.Message);
    }

    private static string NormalizeActivityType(string? activityType) =>
        activityType?.ToLowerInvariant() switch
        {
            "command" => "command",
            "commandresult" => "commandResult",
            "contactrelationupdate" => "contactRelationUpdate",
            "conversationupdate" => "conversationUpdate",
            "endofconversation" => "endOfConversation",
            "event" => "event",
            "handoff" => "handoff",
            "installationupdate" => "installationUpdate",
            "invoke" => "invoke",
            "message" => "message",
            "messagereaction" => "messageReaction",
            "suggestion" => "suggestion",
            "trace" => "trace",
            "typing" => "typing",
            null or "" => "unknown",
            _ => "other"
        };
}
