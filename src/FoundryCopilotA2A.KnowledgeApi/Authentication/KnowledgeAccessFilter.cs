using System.Net;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FoundryCopilotA2A.KnowledgeApi.Authentication;

public sealed class KnowledgeAccessFilter(
    IConfiguration configuration) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var allowsAnonymous = context.ActionDescriptor.EndpointMetadata
            .OfType<IAllowAnonymous>()
            .Any();

        if (!allowsAnonymous && configuration["Knowledge:Backend"] == "Mock")
        {
            var remoteAddress = context.HttpContext.Connection.RemoteIpAddress;
            if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress))
            {
                throw new ApiException(
                    StatusCodes.Status403Forbidden,
                    "local_mock_only",
                    "Mock endpoints accept loopback callers only.");
            }
        }

        var missingBody = context.ActionDescriptor.Parameters.Any(parameter =>
            parameter.BindingInfo?.BindingSource == BindingSource.Body &&
            (!context.ActionArguments.TryGetValue(parameter.Name, out var value) || value is null));
        if (!context.ModelState.IsValid || missingBody)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Send a valid, non-null JSON request body matching this endpoint.");
        }

        await next();
    }
}
