using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.Cli;

internal static class AdaptiveCardProbe
{
    internal const string ExtensionUri = "urn:foundry-copilot-a2a:adaptive-cards:v1";
    internal const string ContentType = "application/vnd.microsoft.card.adaptive";
    internal const string DefaultPrompt = "show adaptive card test";
    internal const string DefaultMarker = "A2A-CARD-PROBE-001";
    internal const int MaxCardBytes = 64 * 1024;
    internal const int MaxTurnBytes = 256 * 1024;

    internal static async Task<int> RunAsync(
        CliContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "direct-connect-url-env", "bearer-token-env", "obo", "tenant-id-env",
            "client-id-env", "client-secret-env", "a2a-url-env", "a2a-bearer-token-env",
            "agent-id", "prompt", "expected-marker", "timeout-seconds", "help");
        var seconds = arguments.Integer("timeout-seconds", 300, minimum: 10);
        if (seconds > 600)
        {
            throw new CliException("Option '--timeout-seconds' cannot exceed 600.");
        }
        var sdk = arguments.Has("direct-connect-url-env");
        var wire = arguments.Has("a2a-url-env");
        if (!sdk && !wire)
        {
            throw new CliException("Supply --direct-connect-url-env, --a2a-url-env, or both.");
        }
        if (!sdk && new[] { "bearer-token-env", "obo", "tenant-id-env", "client-id-env", "client-secret-env" }
            .Any(arguments.Has))
        {
            throw new CliException("SDK authentication options require --direct-connect-url-env.");
        }
        if (!wire && new[] { "a2a-bearer-token-env", "agent-id" }.Any(arguments.Has))
        {
            throw new CliException("A2A authentication/routing options require --a2a-url-env.");
        }
        var prompt = BoundedOption(arguments, "prompt", DefaultPrompt, 2000);
        var marker = BoundedOption(arguments, "expected-marker", DefaultMarker, 256);
        var endpoint = wire
            ? ReadEndpoint(CopilotProbeSession.RequireEnvironment(arguments.Require("a2a-url-env")))
            : null;
        var agentId = arguments.Optional("agent-id");
        if (agentId is not null && (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 128 ||
            agentId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
        {
            throw new CliException("--agent-id must be 1-128 ASCII letters, digits, hyphens, underscores or dots.");
        }
        var a2aToken = arguments.Has("a2a-bearer-token-env")
            ? CopilotProbeSession.RequireEnvironment(arguments.Require("a2a-bearer-token-env")) : null;
        if (a2aToken is not null)
        {
            CopilotProbeSession.RequireDelegatedToken(a2aToken);
        }

        // Bound authentication, SDK calls and SSE body consumption, not just receipt of HTTP headers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var ct = timeout.Token;
        try
        {
            JsonElement? expectedContent = null;
            if (sdk)
            {
                using var session = await CopilotProbeSession.ConnectAsync(context, arguments, seconds, ct);
                var conversation = await session.StartAsync(ct);
                expectedContent = await ReadSdkCardAsync(
                    session.Client.AskQuestionAsync(prompt, conversation, cancellationToken: ct), marker, ct);
                context.Out.WriteLine("PASS: SDK returned a normal Adaptive Card attachment with the visible marker.");
            }
            if (endpoint is not null)
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                {
                    Timeout = TimeSpan.FromSeconds(seconds)
                };
                await AdaptiveCardWireProbe.RunAsync(
                    http, endpoint, a2aToken, agentId, prompt, marker, expectedContent, context.Out, ct);
            }
            context.Out.WriteLine(
                $"VERIFIED: requested card checks passed (SDK: {sdk}; A2A wire: {wire}). " +
                "This verifies only the selected routes and fixture, not browser rendering, " +
                "Action.Submit, or native orchestrator pass-through.");
            return 0;
        }
        catch (MsalException)
        {
            throw new CliException("Delegated authentication failed; card delivery is not verified.");
        }
        catch (HttpRequestException exception)
        {
            var status = exception.StatusCode is { } code ? ((int)code).ToString() : "unavailable";
            throw new CliException($"Card probe request failed (HTTP {status}); no response body or URL is logged.");
        }
        catch (JsonException)
        {
            throw new CliException("Malformed card probe response; no payload is logged. Card delivery is not verified.");
        }
        catch (IOException)
        {
            throw new CliException("Card probe response could not be read completely; card delivery is not verified.");
        }
    }

    internal static Uri ReadEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new CliException(
                "The A2A URL variable must contain a full HTTPS runtime URL (or HTTP loopback), " +
                "without credentials, query or fragment.");
        }
        return uri;
    }

    internal static async Task<JsonElement> ReadSdkCardAsync(
        IAsyncEnumerable<IActivity> activities, string marker, CancellationToken cancellationToken)
    {
        JsonElement? matched = null;
        var count = 0;
        await foreach (var activity in activities.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 256)
            {
                throw new CliException("SDK response exceeded the probe's 256-activity bound.");
            }
            CopilotProbeSession.CheckActivity(activity);
            if (activity.Type != ActivityTypes.Message ||
                string.Equals(activity.From?.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(activity.Name, "connectors/connectionManagerCard", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException("Agent returned a connection-manager card; complete sign-in separately.");
            }
            foreach (var attachment in activity.Attachments ?? [])
            {
                if (!string.Equals(attachment.ContentType, ContentType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                using var document = JsonDocument.Parse(
                    JsonSerializer.Serialize(attachment.Content),
                    new JsonDocumentOptions { MaxDepth = 32 });
                var content = document.RootElement;
                ValidateContent(content);
                if (HasVisibleMarker(content, marker))
                {
                    if (matched is { } previous && !JsonElement.DeepEquals(previous, content))
                    {
                        throw new CliException("SDK returned conflicting marker cards; use one deterministic fixture.");
                    }
                    matched = content.Clone();
                }
            }
        }
        return matched ?? throw new CliException(
            "NOT VERIFIED: SDK did not return a normal Adaptive Card attachment with the visible marker. " +
            "Text echoes and card JSON printed in text do not pass.");
    }

    internal static void ValidateContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object || String(content, "type") != "AdaptiveCard" ||
            !Version.TryParse(String(content, "version"), out _) ||
            !content.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array)
        {
            throw new CliException("NOT VERIFIED: card content must have AdaptiveCard type, version and a body array.");
        }
        if (Encoding.UTF8.GetByteCount(content.GetRawText()) > MaxCardBytes || !WithinDepth(content, 1))
        {
            throw new CliException("NOT VERIFIED: card content exceeded the 64 KiB or depth-32 bound.");
        }
    }

    private static bool WithinDepth(JsonElement value, int depth) =>
        depth <= 32 && (value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().All(p => WithinDepth(p.Value, depth + 1)),
            JsonValueKind.Array => value.EnumerateArray().All(item => WithinDepth(item, depth + 1)),
            _ => true
        });

    internal static bool HasVisibleMarker(JsonElement node, string marker)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            (node.TryGetProperty("isVisible", out var visible) && visible.ValueKind == JsonValueKind.False))
        {
            return false;
        }
        if (String(node, "type") == "TextBlock" &&
            String(node, "text")?.Contains(marker, StringComparison.Ordinal) == true)
        {
            return true;
        }
        var childProperty = String(node, "type") switch
        {
            "AdaptiveCard" => "body",
            "Container" or "Column" => "items",
            "ColumnSet" => "columns",
            _ => null
        };
        return childProperty is not null &&
            node.TryGetProperty(childProperty, out var children) && children.ValueKind == JsonValueKind.Array &&
            children.EnumerateArray().Any(child =>
                (childProperty != "columns" || String(child, "type") == "Column") &&
                HasVisibleMarker(child, marker));
    }

    internal static string? String(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string BoundedOption(CommandArguments arguments, string name, string fallback, int max)
    {
        var value = arguments.Optional(name, fallback)!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
        {
            throw new CliException($"Option '--{name}' must contain 1-{max} characters.");
        }
        return value;
    }
}
