using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static FoundryCopilotA2A.Cli.AdaptiveCardProbe;

namespace FoundryCopilotA2A.Cli;

internal static class AdaptiveCardWireProbe
{
    internal static async Task RunAsync(
        HttpClient http, Uri endpoint, string? token, string? agentId, string prompt, string marker,
        JsonElement? expectedContent, TextWriter output, CancellationToken cancellationToken)
    {
        foreach (var v03 in new[] { false, true })
        {
            foreach (var streaming in new[] { false, true })
            {
                var requestId = Guid.NewGuid().ToString("N");
                using var request = CreateRequest(endpoint, token, agentId, prompt, requestId, v03, streaming);
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CliException($"A2A card probe returned HTTP {(int)response.StatusCode}; no body is logged.");
                }
                var expectedType = streaming ? "text/event-stream" : "application/json";
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType,
                    expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliException("A2A card probe returned an unexpected response content type.");
                }
                var body = await ReadBoundedAsync(response.Content, cancellationToken);
                var cards = ValidateResponse(body, requestId, streaming, marker, expectedContent);
                output.WriteLine($"PASS: A2A {(v03 ? "0.3" : "1.0")} " +
                    $"{(streaming ? "streaming" : "nonstreaming")}: {cards} card(s), visible marker, " +
                    $"text answer and bounded fallback{(expectedContent.HasValue ? ", SDK content preserved" : "")}.");
            }
        }
    }

    private static HttpRequestMessage CreateRequest(
        Uri endpoint, string? token, string? agentId, string prompt, string id, bool v03, bool streaming)
    {
        var part = new JsonObject { ["text"] = prompt };
        var message = new JsonObject
        {
            ["role"] = v03 ? "user" : "ROLE_USER",
            ["parts"] = new JsonArray(part),
            ["messageId"] = Guid.NewGuid().ToString("N"),
            ["contextId"] = Guid.NewGuid().ToString("N")
        };
        if (v03)
        {
            message["kind"] = "message";
            part["kind"] = "text";
        }
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id,
                method = v03
                    ? streaming ? "message/stream" : "message/send"
                    : streaming ? "SendStreamingMessage" : "SendMessage",
                @params = new
                {
                    message,
                    configuration = new { blocking = true, acceptedOutputModes = new[] { "text/plain", "application/json" } }
                }
            })
        };
        if (!v03)
        {
            request.Headers.Add("A2A-Version", "1.0");
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            streaming ? "text/event-stream" : "application/json"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (agentId is not null)
        {
            request.Headers.Add("X-Copilot-Agent", agentId);
        }
        return request;
    }

    internal static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
        => Encoding.UTF8.GetString(await ProbeResponse.ReadBoundedBytesAsync(content, cancellationToken));

    internal static int ValidateResponse(
        string body, string requestId, bool streaming, string marker, JsonElement? expectedContent = null)
    {
        var evidence = new Evidence(requestId);
        if (streaming)
        {
            using var reader = new StringReader(body);
            var data = new StringBuilder();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    ConsumeEvent(data.ToString(), evidence);
                    data.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.AppendLine(line[5..].TrimStart(' '));
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal) &&
                    line[6..].Trim() == "error")
                {
                    throw new CliException("A2A stream returned an error event; no payload is logged.");
                }
            }
            ConsumeEvent(data.ToString(), evidence);
        }
        else
        {
            evidence.Read(body);
        }
        return evidence.Verify(marker, expectedContent);
    }

    private static void ConsumeEvent(string data, Evidence evidence)
    {
        data = data.Trim();
        if (data.Length != 0 && data is not "[DONE]" and not "end")
        {
            evidence.Read(data);
        }
    }

    private sealed record Card(int Revision, JsonElement Content, string Fallback);

    private sealed class Parts
    {
        internal string Text { get; private set; } = "";
        internal Dictionary<string, Card> Cards { get; } = new(StringComparer.Ordinal);

        internal void Read(JsonElement container)
        {
            if (!container.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                throw new CliException("A2A answer has no valid parts array.");
            }
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    throw new CliException("A2A answer contains an invalid part.");
                }
                Text += String(part, "text") ?? "";
                if (!part.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty(ExtensionUri, out var envelope))
                {
                    continue;
                }
                if (String(envelope, "schemaVersion") != "1" ||
                    !envelope.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array ||
                    cards.GetArrayLength() is < 1 or > 5)
                {
                    throw new CliException("NOT VERIFIED: malformed or unsupported card envelope.");
                }
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var card in cards.EnumerateArray())
                {
                    var id = String(card, "id");
                    var fallback = String(card, "fallbackText");
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !ids.Add(id) ||
                        String(card, "contentType") != ContentType ||
                        string.IsNullOrWhiteSpace(fallback) || fallback.Length > 4000 ||
                        !card.TryGetProperty("revision", out var revision) ||
                        revision.ValueKind != JsonValueKind.Number ||
                        !revision.TryGetInt32(out var number) || number < 1 ||
                        !card.TryGetProperty("content", out var content))
                    {
                        throw new CliException("NOT VERIFIED: invalid card identity, revision, content type or fallback.");
                    }
                    ValidateContent(content);
                    var incoming = new Card(number, content.Clone(), fallback);
                    if (Cards.TryGetValue(id, out var previous))
                    {
                        if (previous.Revision == number &&
                            (!JsonElement.DeepEquals(previous.Content, content) || previous.Fallback != fallback))
                        {
                            throw new CliException("NOT VERIFIED: conflicting payloads for one card revision.");
                        }
                        if (previous.Revision >= number)
                        {
                            continue;
                        }
                    }
                    Cards[id] = incoming;
                }
            }
        }
    }

    private sealed class Evidence(string requestId)
    {
        private readonly Dictionary<string, Parts> _artifacts = new(StringComparer.Ordinal);
        private Parts _message = new();
        private bool _sawTask;
        private bool _completed;

        internal void Read(string json)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null))
            {
                throw new CliException("A2A returned an invalid response or JSON-RPC error; no payload is logged.");
            }
            if (String(root, "jsonrpc") != "2.0" || String(root, "id") != requestId ||
                !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                throw new CliException("A2A returned a malformed or uncorrelated JSON-RPC result.");
            }

            if (result.TryGetProperty("message", out var message))
            {
                ReadMessage(message);
            }
            else if (result.TryGetProperty("task", out var task))
            {
                ReadTask(task);
            }
            else if (result.TryGetProperty("artifactUpdate", out var update))
            {
                ReadUpdate(update);
            }
            else if (result.TryGetProperty("statusUpdate", out var statusUpdate))
            {
                ReadStatus(statusUpdate);
            }
            else
            {
                switch (String(result, "kind"))
                {
                    case "message": ReadMessage(result); break;
                    case "task": ReadTask(result); break;
                    case "artifact-update": ReadUpdate(result); break;
                    case "status-update": ReadStatus(result); break;
                    default: throw new CliException("A2A returned an unsupported result shape.");
                }
            }
            RequireLimits();
        }

        private void ReadMessage(JsonElement message)
        {
            if (String(message, "role") is not ("ROLE_AGENT" or "agent"))
            {
                throw new CliException("A2A answer must be an agent message, not a user echo.");
            }
            _message = new Parts();
            _message.Read(message);
        }

        private void ReadTask(JsonElement task)
        {
            ReadStatus(task);
            if (task.TryGetProperty("artifacts", out var artifacts))
            {
                if (artifacts.ValueKind != JsonValueKind.Array)
                {
                    throw new CliException("A2A task contains an invalid artifacts collection.");
                }
                _artifacts.Clear();
                foreach (var artifact in artifacts.EnumerateArray())
                {
                    ReadArtifact(artifact, append: false);
                }
            }
        }

        private void ReadUpdate(JsonElement update)
        {
            _sawTask = true;
            if (update.ValueKind != JsonValueKind.Object || !update.TryGetProperty("artifact", out var artifact))
            {
                throw new CliException("A2A returned an invalid artifact update.");
            }
            ReadArtifact(artifact, update.TryGetProperty("append", out var append) &&
                append.ValueKind == JsonValueKind.True);
        }

        private void ReadArtifact(JsonElement artifact, bool append)
        {
            var id = String(artifact, "artifactId");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new CliException("A2A artifact is missing its identity.");
            }
            if (!append || !_artifacts.TryGetValue(id, out var parts))
            {
                parts = new Parts();
                _artifacts[id] = parts;
            }
            parts.Read(artifact);
        }

        private void ReadStatus(JsonElement value)
        {
            _sawTask = true;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("status", out var status))
            {
                throw new CliException("A2A returned a task without status.");
            }
            switch (String(status, "state"))
            {
                case "TASK_STATE_COMPLETED": case "completed":
                    _completed = true; break;
                case "TASK_STATE_SUBMITTED": case "submitted":
                case "TASK_STATE_WORKING": case "working":
                    _completed = false; break;
                default:
                    throw new CliException("A2A task failed, requires input/authentication, or has an unknown state.");
            }
        }

        private IEnumerable<Parts> AllParts => _artifacts.Values.Prepend(_message);

        private void RequireLimits()
        {
            var cards = AllParts.SelectMany(parts => parts.Cards.Values).ToArray();
            if (cards.Length > 5 || cards.Sum(card =>
                Encoding.UTF8.GetByteCount(card.Content.GetRawText())) > MaxTurnBytes)
            {
                throw new CliException("A2A cards exceeded the five-card or 256 KiB accumulated-turn bound.");
            }
        }

        internal int Verify(string marker, JsonElement? expected)
        {
            if ((_sawTask && !_completed) || !AllParts.Any(parts => !string.IsNullOrWhiteSpace(parts.Text)))
            {
                throw new CliException("NOT VERIFIED: A2A response was incomplete or lacked an ordinary text answer.");
            }
            var cards = AllParts.SelectMany(parts => parts.Cards.Values).ToArray();
            var matches = cards.Where(card => HasVisibleMarker(card.Content, marker)).ToArray();
            if (matches.Length == 0 || (expected is { } original &&
                !matches.Any(card => JsonElement.DeepEquals(card.Content, original))))
            {
                throw new CliException(
                    "NOT VERIFIED: A2A response did not preserve the expected card content and visible marker. " +
                    "Check the published fixture and Adapter:AdaptiveCardsEnabled.");
            }
            return cards.Length;
        }
    }
}
