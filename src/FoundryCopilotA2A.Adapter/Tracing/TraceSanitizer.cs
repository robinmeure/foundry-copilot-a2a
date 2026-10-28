using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoundryCopilotA2A.Adapter;

internal static class TraceSanitizer
{
    private const int MaximumBodyCharacters = 12_000;
    private static readonly HashSet<string> AllowedAttributes =
    [
        "a2a.context.present",
        "a2a.chain.enabled",
        "a2a.chain.target_agent",
        "a2a.idempotency.cache_hit",
        "a2a.message_id.present",
        "a2a.outcome",
        "adapter.failure.reason",
        "auth.flow",
        "auth.scope",
        "copilot_studio.agent.id",
        "copilot_studio.activity.count",
        "copilot_studio.activity.types",
        "copilot_studio.attachment.count",
        "copilot_studio.backend",
        "copilot_studio.client.compatible",
        "copilot_studio.conversation.id.source",
        "copilot_studio.conversation.restarted",
        "copilot_studio.conversation.reused",
        "copilot_studio.message.adaptive_card_text.count",
        "copilot_studio.message.count",
        "copilot_studio.message.text.count",
        "copilot_studio.oauth_card.present",
        "copilot_studio.plan.failure.code",
        "copilot_studio.plan.failure.count",
        "copilot_studio.plan.thought.count",
        "copilot_studio.response.present",
        "copilot_studio.token_exchange.required",
        "error.type",
        "foundry.agent.id",
        "http.request.method",
        "http.response.status_code",
        "network.protocol.version",
        "rpc.method",
        "rpc.system",
        "url.path"
    ];

    public static IReadOnlyDictionary<string, string> SanitizeAttributes(
        Activity activity,
        string agentId)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Value is null)
            {
                continue;
            }

            if (AllowedAttributes.Contains(tag.Key))
            {
                result[tag.Key] = tag.Value.ToString() ?? string.Empty;
            }
            else if (tag.Key == "url.full")
            {
                result[tag.Key] = Uri.TryCreate(tag.Value.ToString(), UriKind.Absolute, out var uri)
                    ? SanitizeUrl(uri, agentId)
                    : "[redacted URL]";
            }
            else if (tag.Key == "server.address")
            {
                result[tag.Key] = ResolveDestination(activity) ?? "External service";
            }
        }

        return result;
    }

    public static string? ResolveDestination(Activity activity)
    {
        var address = activity.GetTagItem("server.address")?.ToString();
        if (string.IsNullOrWhiteSpace(address))
        {
            return activity.Kind == ActivityKind.Server ? "A2A adapter" : null;
        }

        if (address.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Entra ID";
        }

        if (address.Contains("environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Copilot Studio API";
        }

        if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            address.Equals("127.0.0.1", StringComparison.Ordinal))
        {
            return "A2A adapter";
        }

        return "External service";
    }

    public static string SanitizeName(string name)
    {
        if (name.Contains("environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP connection to Copilot Studio API";
        }

        if (name.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP connection to Microsoft Entra ID";
        }

        return name;
    }

    public static string SanitizeUrl(Uri? uri, string agentId)
    {
        if (uri is null)
        {
            return "[unknown URL]";
        }

        if (uri.Host.Contains("environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase))
        {
            var conversationSuffix = uri.AbsolutePath.Contains(
                "/conversations/",
                StringComparison.OrdinalIgnoreCase)
                ? "/{conversation}"
                : string.Empty;
            return $"https://[power-platform-environment]/copilotstudio/agents/{agentId}/conversations{conversationSuffix}";
        }

        if (uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = uri.AbsolutePath.Contains("/token", StringComparison.OrdinalIgnoreCase)
                ? "/oauth2/v2.0/token"
                : "/identity-metadata";
            return $"https://login.microsoftonline.com/[tenant]{suffix}";
        }

        if (uri.IsLoopback)
        {
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }

        return $"{uri.Scheme}://[external-service]{uri.AbsolutePath}";
    }

    public static IReadOnlyDictionary<string, string> SanitizeHeaders(HttpRequestMessage request)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers.Authorization is not null)
        {
            headers["Authorization"] = $"{request.Headers.Authorization.Scheme} [redacted]";
        }

        if (request.Headers.Accept.Count > 0)
        {
            headers["Accept"] = string.Join(", ", request.Headers.Accept);
        }

        if (request.Content?.Headers.ContentType is not null)
        {
            headers["Content-Type"] = request.Content.Headers.ContentType.ToString();
        }

        return headers;
    }

    public static async Task<string?> ReadSanitizedRequestBodyAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return null;
        }

        var raw = await request.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(raw);
            SanitizeNode(node);
            return Limit(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
        }
        catch (JsonException)
        {
            return "[non-JSON request body omitted]";
        }
    }

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Key.Equals("attachments", StringComparison.OrdinalIgnoreCase))
                {
                    jsonObject[property.Key] = "[attachment content omitted]";
                }
                else if (IsSensitiveProperty(property.Key))
                {
                    jsonObject[property.Key] = "[redacted]";
                }
                else
                {
                    SanitizeNode(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                SanitizeNode(item);
            }
        }
    }

    private static bool IsSensitiveProperty(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("connectionString", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("raw", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Url", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Uri", StringComparison.OrdinalIgnoreCase);

    private static string Limit(string value) =>
        value.Length <= MaximumBodyCharacters
            ? value
            : $"{value[..MaximumBodyCharacters]}\n[truncated]";
}
