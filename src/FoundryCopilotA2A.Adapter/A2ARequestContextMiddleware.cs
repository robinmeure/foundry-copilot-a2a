using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter;

public sealed class A2ARequestContextMiddleware(
    RequestDelegate next,
    ILogger<A2ARequestContextMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IdempotencyStore idempotencyStore,
        A2ARequestMetadataAccessor metadataAccessor,
        AgentIsolationKeyContext isolationKeyContext,
        AgentCatalog agentCatalog)
    {
        string? routeAgentId = null;
        var isRuntimeRequest =
            HttpMethods.IsPost(context.Request.Method) &&
            TryResolveRuntimeAgent(context.Request.Path, out routeAgentId);
        if (isRuntimeRequest)
        {
            if (routeAgentId is not null)
            {
                context.Items[AdapterConstants.RouteAgentItem] =
                    agentCatalog.ResolveAgentId(routeAgentId);
            }

            Activity.Current?.SetTag("a2a.runtime", true);
            context.Request.EnableBuffering();

            string? messageId = null;
            string? contextId = null;
            string? payloadHash = null;
            JsonElement requestId = default;

            try
            {
                using var document = await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted);

                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("method", out var methodElement) &&
                    methodElement.ValueKind == JsonValueKind.String)
                {
                    Activity.Current?.SetTag("rpc.system", "jsonrpc");
                    Activity.Current?.SetTag("rpc.method", methodElement.GetString());
                }

                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("id", out var idElement))
                {
                    requestId = idElement.Clone();
                }

                if (TryGetObject(document.RootElement, "params", out var parameters) &&
                    TryGetObject(parameters, "message", out var message))
                {
                    if (TryGetString(message, "contextId", out contextId))
                    {
                        context.Items[AdapterConstants.ContextIdItem] = contextId;
                    }

                    if (TryGetString(message, "messageId", out messageId))
                    {
                        context.Items[AdapterConstants.MessageIdItem] = messageId;
                    }

                    payloadHash = PayloadHash.Compute(message);
                    context.Items[AdapterConstants.PayloadHashItem] = payloadHash;

                    var history = ReadHistory(message);
                    if (history.Count > 0)
                    {
                        context.Items[AdapterConstants.HistoryItem] = history;
                    }

                    Activity.Current?.SetTag("a2a.history.turns", history.Count);
                }

                Activity.Current?.SetTag("a2a.context.present", contextId is not null);
                Activity.Current?.SetTag("a2a.message_id.present", messageId is not null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Never let malformed or unexpectedly shaped input escape this middleware:
                // it would bypass the A2A layer's JSON-RPC error translation and surface as a 500.
                logger.LogDebug(exception, "A2A metadata extraction skipped for unusable request body.");
            }
            finally
            {
                TryRewind(context.Request.Body);
            }

            try
            {
                var requestedAgentId = ResolveRequestedAgentId(context, agentCatalog);
                var requestedChainTarget = context.Request.Headers[
                    AdapterConstants.ChainTargetHeaderName].ToString();
                if (!string.IsNullOrWhiteSpace(requestedChainTarget))
                {
                    var target = agentCatalog.ResolveChainTarget(
                        requestedAgentId,
                        requestedChainTarget);
                    context.Items[AdapterConstants.ChainTargetItem] = target.Id;
                    Activity.Current?.SetTag("a2a.chain.target_agent", target.Id);
                }
            }
            catch (AdapterRequestException exception)
            {
                Activity.Current?.SetTag("a2a.outcome", "invalid_agent");
                logger.LogWarning(
                    "Refused an A2A request selecting an unconfigured agent.");
                await WriteJsonRpcErrorAsync(
                    context,
                    requestId,
                    InvalidParamsErrorCode,
                    exception.Message);
                return;
            }

            if (messageId is not null && payloadHash is not null &&
                IsConflictingReplay(idempotencyStore, metadataAccessor, contextId, messageId, payloadHash))
            {
                Activity.Current?.SetTag("a2a.outcome", "replay_conflict");
                logger.LogWarning(
                    "Refused a replayed messageId carrying different content in context {ContextId}.",
                    contextId);
                await WriteJsonRpcErrorAsync(
                    context,
                    requestId,
                    ReplayConflictErrorCode,
                    "This messageId has already been used with different content. " +
                    "Reusing a messageId for a different request is not permitted.");
                return;
            }

            try
            {
                // The A2A server finishes streaming work on a background execution context.
                // Capture the tenant-scoped identity directly so session persistence remains
                // isolated after ASP.NET Core clears its ambient HttpContext.
                isolationKeyContext.Current = metadataAccessor.ResolveUserId(context);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(
                    exception,
                    "No caller isolation key is available; the A2A session store will reject the request.");
            }
        }

        try
        {
            await next(context);
        }
        finally
        {
            if (isRuntimeRequest)
            {
                isolationKeyContext.Current = null;
            }
        }
    }

    /// <summary>
    /// Rejects a conflicting replay at the transport edge. The agent pipeline reports handler
    /// exceptions as a generic "no response events" error, which hides the real cause, and a
    /// request that must be refused should never reach the delegated backend at all.
    /// <see cref="IdempotencyStore.GetOrAddAsync"/> stays the atomic authority for races.
    /// </summary>
    private bool IsConflictingReplay(
        IdempotencyStore idempotencyStore,
        A2ARequestMetadataAccessor metadataAccessor,
        string? contextId,
        string messageId,
        string payloadHash)
    {
        try
        {
            var metadata = metadataAccessor.Current;
            return idempotencyStore.IsConflictingReplay(
                IdempotencyStore.BuildKey(
                    metadata.UserId,
                    metadata.AgentId,
                    contextId,
                    messageId),
                payloadHash);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or AdapterRequestException)
        {
            // Identity is unresolvable, so no cache key can be built. Defer to the chat client.
            return false;
        }
    }

    private static async Task WriteJsonRpcErrorAsync(
        HttpContext context,
        JsonElement requestId,
        int errorCode,
        string errorMessage)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        var id = requestId.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? requestId
            : default;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                id.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteStartObject("error");
            writer.WriteNumber("code", errorCode);
            writer.WriteString("message", errorMessage);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted);
    }

    /// <summary>JSON-RPC "Invalid Request".</summary>
    private const int ReplayConflictErrorCode = -32600;

    /// <summary>JSON-RPC "Invalid params".</summary>
    private const int InvalidParamsErrorCode = -32602;

    /// <summary>
    /// Endpoint routing matches both "/a2a/copilot-studio" and "/a2a/copilot-studio/".
    /// An exact comparison here would let a trailing slash silently bypass replay protection.
    /// </summary>
    public static bool TryResolveRuntimeAgent(PathString path, out string? routeAgentId)
    {
        routeAgentId = null;
        if (path.StartsWithSegments(
            AdapterConstants.RuntimePath,
            StringComparison.OrdinalIgnoreCase,
            out var remaining)
            && (!remaining.HasValue || remaining.Value is "/" or ""))
        {
            return true;
        }

        var value = path.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(value) ||
            !value.StartsWith(
                AdapterConstants.ChainAgentsPath + "/",
                StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith("/a2a", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetStart = AdapterConstants.ChainAgentsPath.Length + 1;
        var targetLength = value.Length - targetStart - "/a2a".Length;
        if (targetLength <= 0)
        {
            return false;
        }

        var encodedTarget = value.Substring(targetStart, targetLength);
        if (encodedTarget.Contains('/'))
        {
            return false;
        }

        routeAgentId = Uri.UnescapeDataString(encodedTarget);
        return true;
    }

    private static string ResolveRequestedAgentId(
        HttpContext context,
        AgentCatalog agentCatalog) =>
        context.Items[AdapterConstants.RouteAgentItem] as string ??
        agentCatalog.ResolveAgentId(
            context.Request.Headers[AdapterConstants.AgentHeaderName].ToString());

    private static bool TryGetObject(JsonElement source, string name, out JsonElement value)
    {
        value = default;
        return source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Reads the prior turns the caller attached under <c>message.metadata.history</c>. The list is
    /// bounded in both turn count and per-turn length so an oversized client payload cannot be
    /// relayed verbatim to a delegated backend.
    /// </summary>
    private static IReadOnlyList<A2AConversationTurn> ReadHistory(JsonElement message)
    {
        if (!TryGetObject(message, "metadata", out var metadata) ||
            !metadata.TryGetProperty(AdapterConstants.HistoryMetadataKey, out var history) ||
            history.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var turns = new List<A2AConversationTurn>();
        foreach (var entry in history.EnumerateArray())
        {
            if (!TryGetString(entry, "role", out var role) ||
                !TryGetString(entry, "text", out var text) ||
                string.IsNullOrWhiteSpace(text) ||
                NormalizeRole(role) is not { } normalizedRole)
            {
                continue;
            }

            turns.Add(new A2AConversationTurn(
                normalizedRole,
                text!.Length > AdapterConstants.MaxHistoryTurnLength
                    ? text[..AdapterConstants.MaxHistoryTurnLength]
                    : text));
        }

        return turns.Count > AdapterConstants.MaxHistoryTurns
            ? turns[^AdapterConstants.MaxHistoryTurns..]
            : turns;
    }

    private static string? NormalizeRole(string? role) => role?.ToLowerInvariant() switch
    {
        "user" or "role_user" => A2AConversationTurn.UserRole,
        "assistant" or "agent" or "role_agent" => A2AConversationTurn.AssistantRole,
        _ => null
    };

    private static bool TryGetString(JsonElement source, string name, out string? value)
    {
        value = null;
        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    private static void TryRewind(Stream body)
    {
        try
        {
            if (body.CanSeek)
            {
                body.Position = 0;
            }
        }
        catch (ObjectDisposedException)
        {
            // The request was aborted; there is nothing left to rewind.
        }
    }
}

public sealed record A2ARequestMetadata
{
    public required string? ContextId { get; init; }

    public required string? MessageId { get; init; }

    public required string AgentId { get; init; }

    /// <summary>Composite tenant-scoped caller identity. Never a shared fallback in a secured deployment.</summary>
    public required string UserId { get; init; }

    public required string? PayloadHash { get; init; }

    public required string? BearerToken { get; init; }

    public string? ChainTargetAgentId { get; init; }

    /// <summary>Prior turns supplied by the caller, oldest first. Empty when the caller sent none.</summary>
    public IReadOnlyList<A2AConversationTurn> History { get; init; } = [];

    /// <summary>
    /// Overridden so the synthesized record ToString() cannot leak the delegated bearer token
    /// into logs or telemetry.
    /// </summary>
    public override string ToString() =>
        $"A2ARequestMetadata {{ ContextId = {ContextId}, MessageId = {MessageId}, " +
        $"AgentId = {AgentId}, UserId = {UserId}, PayloadHash = {PayloadHash}, " +
        $"ChainTargetAgentId = {ChainTargetAgentId}, HistoryTurns = {History.Count}, " +
        $"BearerToken = [redacted] }}";
}

/// <summary>A single prior turn of the caller's conversation.</summary>
public sealed record A2AConversationTurn(string Role, string Text)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";
}

/// <summary>
/// Renders caller-supplied history into a prompt prefix for backends that do not keep the
/// conversation server-side.
/// </summary>
public static class ConversationTranscript
{
    public static string Prepend(string prompt, IReadOnlyList<A2AConversationTurn> history)
    {
        if (history.Count == 0)
        {
            return prompt;
        }

        var builder = new System.Text.StringBuilder("Conversation so far:");
        foreach (var turn in history)
        {
            builder.Append('\n')
                .Append(turn.Role == A2AConversationTurn.UserRole ? "User: " : "Assistant: ")
                .Append(turn.Text);
        }

        return builder.Append("\n\n---\n").Append(prompt).ToString();
    }
}

public sealed class A2ARequestMetadataAccessor(
    IHttpContextAccessor httpContextAccessor,
    IOptions<AdapterOptions> adapterOptions,
    IOptions<AuthenticationOptions> authenticationOptions,
    AgentCatalog agentCatalog)
{
    public A2ARequestMetadata Current
    {
        get
        {
            var context = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("No active HTTP request is available.");

            return new A2ARequestMetadata
            {
                ContextId = context.Items[AdapterConstants.ContextIdItem] as string,
                MessageId = context.Items[AdapterConstants.MessageIdItem] as string,
                AgentId = context.Items[AdapterConstants.RouteAgentItem] as string ??
                          agentCatalog.ResolveAgentId(
                              context.Request.Headers[AdapterConstants.AgentHeaderName].ToString()),
                UserId = ResolveUserId(context),
                PayloadHash = context.Items[AdapterConstants.PayloadHashItem] as string,
                BearerToken = ReadBearerToken(context),
                ChainTargetAgentId =
                    context.Items[AdapterConstants.ChainTargetItem] as string,
                History =
                    context.Items[AdapterConstants.HistoryItem]
                        as IReadOnlyList<A2AConversationTurn> ?? []
            };
        }
    }

    /// <summary>
    /// Resolves a tenant-scoped caller identity. Fails closed: an authenticated deployment that
    /// cannot establish who the caller is must not fall back to a shared partition, because both
    /// the idempotency cache and the conversation store are keyed on this value.
    /// </summary>
    public string ResolveUserId(HttpContext context)
    {
        var objectId = context.User.FindFirstValue("oid")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");

        if (objectId is not null)
        {
            // "oid" is unique only within a tenant, so scope it with "tid".
            var tenantId = context.User.FindFirstValue("tid") ?? "unknown-tenant";
            return $"{tenantId}|{objectId}";
        }

        if (!authenticationOptions.Value.Enabled &&
            adapterOptions.Value.AllowAnonymousDevelopmentMode)
        {
            return AdapterConstants.AnonymousDevelopmentUser;
        }

        throw new UnauthorizedAccessException(
            "The delegated caller identity could not be established from the request token.");
    }

    private static string? ReadBearerToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}
