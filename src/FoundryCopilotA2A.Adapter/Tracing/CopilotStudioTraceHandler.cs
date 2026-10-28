using System.Diagnostics;

namespace FoundryCopilotA2A.Adapter;

public sealed class CopilotStudioTraceHandler(
    SanitizedTraceStore traceStore,
    A2ARequestMetadataAccessor metadataAccessor,
    CopilotStudioResponseMetadataAccessor responseMetadataAccessor)
    : DelegatingHandler
{
    private const string ConversationIdHeaderName = "x-ms-conversationid";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var activity = AdapterTelemetry.StartActivity(
            "copilot_studio.http.exchange",
            ActivityKind.Client);
        var agentId = metadataAccessor.Current.AgentId;
        activity?.SetTag("copilot_studio.agent.id", agentId);
        activity?.SetTag("http.request.method", request.Method.Method);

        var capturedRequest = new TraceHttpRequest(
            request.Method.Method,
            TraceSanitizer.SanitizeUrl(request.RequestUri, agentId),
            TraceSanitizer.SanitizeHeaders(request),
            await TraceSanitizer.ReadSanitizedRequestBodyAsync(request, cancellationToken));

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues(ConversationIdHeaderName, out var conversationIds))
            {
                var conversationId = conversationIds.FirstOrDefault(
                    value => !string.IsNullOrWhiteSpace(value));
                if (conversationId is not null)
                {
                    responseMetadataAccessor.CaptureConversationId(conversationId);
                }
            }

            var capturedResponse = new TraceHttpResponse(
                (int)response.StatusCode,
                "Response content is consumed as Copilot Studio activities and is not buffered by tracing.");
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            activity?.SetStatus(
                response.IsSuccessStatusCode ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            if (activity is not null)
            {
                traceStore.RecordHttpExchange(activity, capturedRequest, capturedResponse, null);
            }

            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AdapterTelemetry.RecordFailure(activity, exception);
            if (activity is not null)
            {
                traceStore.RecordHttpExchange(
                    activity,
                    capturedRequest,
                    null,
                    exception.GetType().Name);
            }

            throw;
        }
    }
}
