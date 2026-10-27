namespace FoundryCopilotA2A.KnowledgeApi.Middleware;

/// <summary>
/// Records the sign-in challenge and retry hint for the current request. Headers are written
/// only while the response has not started; streamed chats read the recorded values instead.
/// </summary>
internal static class ResponseSignals
{
    public const string Challenge = "WWW-Authenticate";
    public const string RetryAfter = "Retry-After";

    public static string? Get(HttpContext context, string name)
    {
        if (context.Items.TryGetValue(Key(name), out var recorded))
        {
            return recorded as string;
        }

        var header = context.Response.Headers[name].ToString();
        return header.Length > 0 ? header : null;
    }

    public static void Set(HttpContext context, string name, string? value)
    {
        context.Items[Key(name)] = value;
        if (context.Response.HasStarted)
        {
            return;
        }

        if (value is null)
        {
            context.Response.Headers.Remove(name);
        }
        else
        {
            context.Response.Headers[name] = value;
        }
    }

    public static Snapshot Capture(HttpContext context) =>
        new(Get(context, Challenge), Get(context, RetryAfter));

    public static void Restore(HttpContext context, Snapshot snapshot)
    {
        Set(context, Challenge, snapshot.Challenge);
        Set(context, RetryAfter, snapshot.RetryAfter);
    }

    private static string Key(string name) => $"knowledge-signal:{name}";

    internal readonly record struct Snapshot(string? Challenge, string? RetryAfter);
}
