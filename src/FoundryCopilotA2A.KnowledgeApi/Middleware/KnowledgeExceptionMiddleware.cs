using System.Text.Json;

namespace FoundryCopilotA2A.KnowledgeApi.Middleware;

public sealed class KnowledgeExceptionMiddleware(
    RequestDelegate next,
    ILogger<KnowledgeExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (IsHandledException(exception))
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            logger.LogWarning(exception, "Knowledge request failed: {Path}", context.Request.Path);

            if (context.Response.HasStarted)
            {
                return;
            }

            var failure = MapToApiException(exception);
            var problem = Results.Problem(
                statusCode: failure.Status,
                title: failure.Code,
                detail: failure.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["traceId"] = context.TraceIdentifier
                });

            await problem.ExecuteAsync(context);
        }
    }

    internal static bool IsHandledException(Exception exception) =>
        exception is ApiException
            or HttpRequestException
            or JsonException
            or InvalidDataException
            or OperationCanceledException
            or BadHttpRequestException;

    internal static ApiException MapToApiException(Exception exception) =>
        exception switch
        {
            ApiException apiException => apiException,
            BadHttpRequestException badRequest => new ApiException(
                badRequest.StatusCode,
                "invalid_request",
                "Malformed or oversized request."),
            InvalidDataException => new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_upload",
                "Malformed multipart upload."),
            OperationCanceledException => new ApiException(
                StatusCodes.Status504GatewayTimeout,
                "knowledge_request_failed",
                "The backend could not complete this request. Check service health and retry explicitly."),
            _ => new ApiException(
                StatusCodes.Status502BadGateway,
                "knowledge_request_failed",
                "The backend could not complete this request. Check service health and retry explicitly.")
        };
}
