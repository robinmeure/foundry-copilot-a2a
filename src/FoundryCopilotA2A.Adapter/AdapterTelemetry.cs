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
}
