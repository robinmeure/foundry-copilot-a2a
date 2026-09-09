using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoundryCopilotA2A.Cli;

internal static class CitadelSmokeTest
{
    public static async Task<int> RunAsync(
        CliContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        arguments.EnsureOnly(
            "base-url", "bearer-token-env", "agent-id", "prompt",
            "expected-output-pattern", "allowed-origin", "require-obo",
            "specialist-route", "chain-target", "require-native-callback", "help");
        var endpoint = arguments.AbsoluteHttpUri("base-url");
        if (endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new CliException("--base-url must be an HTTPS gateway API base URL.");
        }

        var baseUrl = endpoint.AbsoluteUri.TrimEnd('/');
        var origin = CitadelCommands.ReadOrigin(arguments);
        var agentId = arguments.Require("agent-id");
        var prompt = arguments.Require("prompt");
        var requireObo = arguments.Flag("require-obo");
        var specialistRoute = arguments.Flag("specialist-route");
        var chainTarget = arguments.Optional("chain-target");
        var requireNativeCallback = arguments.Flag("require-native-callback");
        if (specialistRoute && chainTarget is not null)
        {
            throw new CliException("Choose either --specialist-route or --chain-target.");
        }
        if (requireNativeCallback && string.IsNullOrWhiteSpace(chainTarget))
        {
            throw new CliException("--require-native-callback requires --chain-target.");
        }
        var tokenVariable = arguments.Require("bearer-token-env");
        var token = Environment.GetEnvironmentVariable(tokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CliException($"Environment variable '{tokenVariable}' does not contain a bearer token.");
        }

        Regex expected;
        try
        {
            expected = new Regex(
                arguments.Require("expected-output-pattern"),
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException exception)
        {
            throw new CliException($"Invalid expected-output-pattern: {exception.Message}");
        }

        // ResponseHeadersRead does not apply HttpClient.Timeout to the SSE body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = timeout.Token;
        var cardBaseUrl = specialistRoute
            ? $"{baseUrl}/a2a-agents/{Uri.EscapeDataString(agentId)}"
            : baseUrl;
        var runtimeUrl = specialistRoute ? $"{cardBaseUrl}/a2a" : $"{baseUrl}/a2a/copilot-studio";
        using (var preflight = new HttpRequestMessage(HttpMethod.Options, runtimeUrl))
        {
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers",
                "authorization,content-type,a2a-version,x-copilot-agent,x-a2a-chain-target");
            using var response = await context.HttpClient.SendAsync(preflight, ct);
            await RequireSuccessAsync(response, ct);
            RequireCors(response, origin);
            RequireHeaderValues(response, "Access-Control-Allow-Methods", ["POST"]);
            RequireHeaderValues(response, "Access-Control-Allow-Headers",
                ["authorization", "content-type", "a2a-version", "x-copilot-agent", "x-a2a-chain-target"]);
        }

        using (var cardRequest = CreateRequest(HttpMethod.Get, $"{cardBaseUrl}/.well-known/agent-card.json", origin))
        {
            cardRequest.Headers.Add("A2A-Version", "1.0");
            using var response = await context.HttpClient.SendAsync(cardRequest, ct);
            await RequireSuccessAsync(response, ct);
            RequireCors(response, origin, publicCard: true);
            using var card = await ReadJsonAsync(response, ct);
            if (!AgentCardContract.AdvertisesRuntime(card.RootElement, url => url == runtimeUrl))
            {
                throw new CliException(
                    "The agent card does not advertise the Citadel runtime URL. " +
                    "Set Adapter:PublicBaseUrl (or Aspire GatewayBaseUrl) to the gateway API base URL.");
            }
        }

        using (var catalogRequest = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/agents", origin))
        {
            using var response = await context.HttpClient.SendAsync(catalogRequest, ct);
            await RequireSuccessAsync(response, ct);
            RequireCors(response, origin);
            using var catalog = await ReadJsonAsync(response, ct);
            var agent = catalog.RootElement.GetProperty("agents").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("id").GetString() == agentId);
            if (agent.ValueKind != JsonValueKind.Object || !agent.GetProperty("supported").GetBoolean())
            {
                throw new CliException($"The gateway catalog does not contain supported agent '{agentId}'.");
            }
            if (chainTarget is not null &&
                (!agent.TryGetProperty("canOrchestrate", out var capability) ||
                 capability.ValueKind != JsonValueKind.True ||
                 !agent.GetProperty("chainTargets").EnumerateArray().Any(target => target.GetString() == chainTarget)))
            {
                throw new CliException(
                    $"Agent '{agentId}' does not advertise native orchestration to '{chainTarget}'.");
            }
        }

        var body = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "SendStreamingMessage",
            @params = new
            {
                message = new
                {
                    role = "ROLE_USER",
                    parts = new[] { new { text = prompt } },
                    messageId = Guid.NewGuid().ToString("N"),
                    contextId = Guid.NewGuid().ToString("N")
                }
            }
        };
        using (var unauthenticated = CreateRequest(HttpMethod.Post, runtimeUrl, origin))
        {
            unauthenticated.Content = JsonContent.Create(body);
            using var response = await context.HttpClient.SendAsync(unauthenticated, ct);
            RequireUnauthorized(response, origin);
        }
        using (var traceRequest = CreateRequest(
                   HttpMethod.Get, $"{baseUrl}/api/traces/{new string('0', 32)}", origin))
        {
            using var response = await context.HttpClient.SendAsync(traceRequest, ct);
            RequireUnauthorized(response, origin);
        }

        using var request = CreateRequest(HttpMethod.Post, runtimeUrl, origin, token);
        request.Headers.Add("A2A-Version", "1.0");
        request.Headers.Add("X-Copilot-Agent", agentId);
        if (chainTarget is not null)
        {
            request.Headers.Add("X-A2A-Chain-Target", chainTarget);
        }
        request.Content = JsonContent.Create(body);
        var stopwatch = Stopwatch.StartNew();
        using var streamed = await context.HttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        await RequireSuccessAsync(streamed, ct);
        RequireCors(streamed, origin);
        RequireHeaderValues(streamed, "Access-Control-Expose-Headers", ["X-Trace-Id", "X-Correlation-ID"]);
        if (streamed.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            throw new CliException("The gateway runtime did not return an SSE response.");
        }
        var traceId = streamed.Headers.TryGetValues("X-Trace-Id", out var traceIds)
            ? traceIds.SingleOrDefault()
            : null;
        if (traceId is null || traceId.Length != 32 || !traceId.All(Uri.IsHexDigit))
        {
            throw new CliException("The gateway response is missing a valid X-Trace-Id.");
        }
        context.Out.WriteLine($"Trace: {traceId}");

        var answer = new StringBuilder();
        long? firstAnswerMilliseconds = null;
        await using (var stream = await streamed.Content.ReadAsStreamAsync(ct))
        using (var reader = new StreamReader(stream))
        {
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    ConsumeEvent(data.ToString(), answer);
                    data.Clear();
                    if (answer.Length > 0)
                    {
                        firstAnswerMilliseconds ??= stopwatch.ElapsedMilliseconds;
                    }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.AppendLine(line["data:".Length..].TrimStart());
                }
            }
            ConsumeEvent(data.ToString(), answer);
        }
        if (answer.Length == 0 || !expected.IsMatch(answer.ToString()))
        {
            throw new CliException("The streamed answer did not match the expected output.");
        }

        using var trace = await LoadTraceAsync(context.HttpClient, baseUrl, origin, token, traceId, ct);
        if ((requireObo || requireNativeCallback) &&
            trace.RootElement.TryGetProperty("truncated", out var truncated) &&
            truncated.ValueKind == JsonValueKind.True)
        {
            throw new CliException(
                "The adapter trace was truncated. Retry with a new trace context before asserting " +
                "complete OBO or native callback evidence.");
        }
        if (requireObo &&
            !trace.RootElement.GetProperty("spans").EnumerateArray().Any(span =>
                span.GetProperty("name").GetString() == "entra.obo.acquire_token" &&
                string.Equals(span.GetProperty("status").GetString(), "Ok", StringComparison.OrdinalIgnoreCase) &&
                span.GetProperty("attributes").TryGetProperty("auth.flow", out var flow) &&
                flow.GetString() == "on_behalf_of"))
        {
            throw new CliException("The completed adapter trace does not contain a successful delegated OBO exchange.");
        }
        if (requireNativeCallback && !HasNativeCallback(trace.RootElement, chainTarget!))
        {
            throw new CliException(
                "No completed specialist callback was observed in the entry trace. An orchestrator " +
                "answer or requested-target span is not proof of delegation. Confirm native A2A " +
                "configuration and inspect the provider activity map and Citadel diagnostics; " +
                "providers that do not propagate trace context produce a separate callback trace.");
        }

        context.Out.WriteLine("Citadel browser contract and A2A round trip passed.");
        if (chainTarget is not null && !requireNativeCallback)
        {
            context.Out.WriteLine(
                "This proves the entry-agent round trip, not the specialist callback. " +
                "Use --require-native-callback when the provider propagates trace context.");
        }
        context.Out.WriteLine($"First answer event: {firstAnswerMilliseconds ?? stopwatch.ElapsedMilliseconds} ms");
        context.Out.WriteLine($"Answer: {answer}");
        return 0;
    }

    internal static bool HasNativeCallback(JsonElement trace, string targetAgentId) =>
        trace.GetProperty("spans").EnumerateArray().Any(span =>
            span.GetProperty("name").GetString()?.StartsWith("execute_tool ", StringComparison.Ordinal) == true &&
            string.Equals(span.GetProperty("status").GetString(), "Ok", StringComparison.OrdinalIgnoreCase) &&
            span.GetProperty("attributes").TryGetProperty("copilot_studio.agent.id", out var agentId) &&
            string.Equals(agentId.GetString(), targetAgentId, StringComparison.OrdinalIgnoreCase));

    internal static void ConsumeEvent(string data, StringBuilder answer)
    {
        data = data.Trim();
        if (data.Length == 0 || data is "end" or "[DONE]")
        {
            return;
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new CliException($"A2A returned an error: {error}");
        }
        if (!root.TryGetProperty("result", out var result))
        {
            return;
        }
        if (result.TryGetProperty("statusUpdate", out var statusUpdate) &&
            statusUpdate.TryGetProperty("status", out var status) &&
            status.TryGetProperty("state", out var state) &&
            state.GetString() is "TASK_STATE_FAILED" or "TASK_STATE_REJECTED" or "TASK_STATE_CANCELED")
        {
            throw new CliException($"A2A task ended with state {state.GetString()}.");
        }

        var isArtifact = result.TryGetProperty("artifactUpdate", out var update);
        var content = isArtifact
            ? update.GetProperty("artifact")
            : result.TryGetProperty("message", out var message) ? message : result;
        if (!content.TryGetProperty("parts", out var parts))
        {
            return;
        }

        var text = string.Concat(parts.EnumerateArray()
            .Where(part => !(part.TryGetProperty("metadata", out var metadata) &&
                             metadata.ValueKind == JsonValueKind.Object &&
                             metadata.TryGetProperty("isInformative", out var informative) &&
                             informative.ValueKind == JsonValueKind.True))
            .Select(part => part.TryGetProperty("text", out var value) ? value.GetString() : null));
        if (text.Length == 0)
        {
            return;
        }
        if (!isArtifact || !update.TryGetProperty("append", out var append) ||
            append.ValueKind != JsonValueKind.True)
        {
            answer.Clear();
        }
        answer.Append(text);
    }

    private static async Task<JsonDocument> LoadTraceAsync(
        HttpClient client,
        string baseUrl,
        string origin,
        string token,
        string traceId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"{baseUrl}/api/traces/{traceId}", origin, token);
            using var response = await client.SendAsync(request, cancellationToken);
            await RequireSuccessAsync(response, cancellationToken);
            RequireCors(response, origin);
            var trace = await ReadJsonAsync(response, cancellationToken);
            if (trace.RootElement.GetProperty("complete").GetBoolean())
            {
                return trace;
            }
            trace.Dispose();
            await Task.Delay(100, cancellationToken);
        }
        throw new CliException("The adapter trace did not complete.");
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method, string url, string origin, string? token = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Origin", origin);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void RequireUnauthorized(HttpResponseMessage response, string origin)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            throw new CliException(
                $"An unauthenticated gateway request returned {(int)response.StatusCode}, not 401.");
        }
        RequireCors(response, origin);
    }

    private static async Task RequireSuccessAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var path = response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path);
        throw new CliException(
            $"Gateway request to '{path}' returned HTTP {(int)response.StatusCode}: " +
            (body.Length > 4000 ? body[..4000] + "..." : body));
    }

    internal static void RequireCors(HttpResponseMessage response, string origin, bool publicCard = false)
    {
        var allowedOrigin = response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values)
            ? string.Join(",", values)
            : null;
        var publicRead = publicCard && allowedOrigin == "*" &&
            (!response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var credentials) ||
             !credentials.Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase)));
        if (allowedOrigin != origin && !publicRead)
        {
            throw new CliException(publicCard
                ? $"The public agent card did not allow browser discovery from '{origin}'."
                : $"The gateway did not allow the exact browser origin '{origin}'.");
        }
    }

    private static void RequireHeaderValues(
        HttpResponseMessage response, string name, string[] required)
    {
        var values = response.Headers.TryGetValues(name, out var headers)
            ? string.Join(",", headers).Split(',', StringSplitOptions.TrimEntries)
            : [];
        if (required.Any(value => !values.Contains(value, StringComparer.OrdinalIgnoreCase)))
        {
            throw new CliException($"Gateway header '{name}' is missing required browser values.");
        }
    }
}
