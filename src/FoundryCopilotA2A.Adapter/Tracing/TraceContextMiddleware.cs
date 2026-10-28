using System.Diagnostics;


namespace FoundryCopilotA2A.Adapter;

internal sealed class TraceContextMiddleware(RequestDelegate next, ILogger<TraceContextMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        SanitizedTraceStore traceStore,
        A2ARequestMetadataAccessor metadataAccessor)
    {
        if (HttpMethods.IsPost(context.Request.Method) &&
                A2ARequestContextMiddleware.TryResolveRuntimeAgent(
                    context.Request.Path,
                    out _) &&
                Activity.Current is { } activity)
        {
            var metadata = metadataAccessor.Current;
            if (!traceStore.Register(activity, metadata.UserId, metadata.AgentId))
            {
                logger.LogWarning("Refused a trace context with a different owner or exhausted request capacity.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id = (string?)null,
                    error = new
                    {
                        code = -32600,
                        message = "The supplied trace context cannot be reused. Start a new trace for this request."
                    }
                });
                return;
            }
            context.Response.Headers[AdapterConstants.TraceHeaderName] = activity.TraceId.ToHexString();
        }

        await next(context);
    }
}
