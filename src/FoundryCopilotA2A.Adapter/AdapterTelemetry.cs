using System.Diagnostics;

namespace FoundryCopilotA2A.Adapter;

internal static class AdapterTelemetry
{
    public const string ActivitySourceName = "FoundryCopilotA2A.Adapter";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal) =>
        Source.StartActivity(name, kind);

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
}
