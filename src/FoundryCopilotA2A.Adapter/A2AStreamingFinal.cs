using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoundryCopilotA2A.Adapter;

/// <summary>
/// A2A 0.3 requires the last <c>status-update</c> of a stream to carry <c>final: true</c>, which is
/// the documented end-of-stream marker. The hosting and compatibility packages emit terminal states
/// with <c>final: false</c>, so a strict client that waits for the marker never sees it and has to
/// rely on the connection closing instead. A2A 1.0 has no <c>final</c> field, so its payloads, which
/// carry a <c>statusUpdate</c> wrapper rather than <c>kind</c>, are deliberately left untouched.
/// </summary>
internal static class A2AFinalStatus
{
    private const string StatusUpdateKind = "status-update";

    /// <summary>States after which the server sends nothing further for the task.</summary>
    private static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
        "canceled",
        "cancelled",
        "rejected"
    };

    /// <summary>
    /// Keeps the re-serialized event byte-faithful to the original. The default encoder escapes
    /// characters such as '+' in timestamps, which needlessly rewrites untouched fields.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool TryMarkFinal(string json, out string rewritten)
    {
        rewritten = json;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject root ||
            root["result"] is not JsonObject result ||
            !IsString(result["kind"], StatusUpdateKind) ||
            result["status"] is not JsonObject status ||
            !IsTerminal(status["state"]) ||
            IsTrue(result["final"]))
        {
            return false;
        }

        result["final"] = true;
        rewritten = root.ToJsonString(SerializerOptions);
        return true;
    }

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue value &&
        value.TryGetValue<string>(out var text) &&
        string.Equals(text, expected, StringComparison.Ordinal);

    private static bool IsTerminal(JsonNode? node) =>
        node is JsonValue value &&
        value.TryGetValue<string>(out var state) &&
        TerminalStates.Contains(state);

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>
    /// Rewrites one server-sent-event line, preserving its framing. Anything that is not a
    /// terminal 0.3 status update is returned unchanged.
    /// </summary>
    public static string TransformLine(string line)
    {
        var payloadEnd = line.Length;
        while (payloadEnd > 0 && line[payloadEnd - 1] is '\n' or '\r')
        {
            payloadEnd--;
        }

        var terminator = line[payloadEnd..];
        var content = line[..payloadEnd];
        if (!content.StartsWith("data:", StringComparison.Ordinal))
        {
            return line;
        }

        var json = content["data:".Length..];
        var leading = json.Length - json.TrimStart(' ').Length;
        json = json[leading..];
        if (json.Length == 0 || !TryMarkFinal(json, out var rewritten))
        {
            return line;
        }

        return string.Concat("data:", new string(' ', leading), rewritten, terminator);
    }
}

/// <summary>
/// Rewrites server-sent events as they are written, without buffering the whole response, so
/// streaming stays incremental.
/// </summary>
internal sealed class A2AFinalStatusStream(Stream inner, HttpResponse response) : Stream
{
    private readonly List<byte> _pending = [];
    private bool? _isEventStream;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private bool IsEventStream => _isEventStream ??=
        response.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!IsEventStream)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            return;
        }

        _pending.AddRange(buffer.Span);
        foreach (var line in DrainCompleteLines())
        {
            await inner.WriteAsync(line, cancellationToken);
        }
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!IsEventStream)
        {
            inner.Write(buffer, offset, count);
            return;
        }

        _pending.AddRange(buffer.AsSpan(offset, count));
        foreach (var line in DrainCompleteLines())
        {
            inner.Write(line.Span);
        }
    }

    /// <summary>Emits any trailing bytes that were never terminated by a newline.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var remainder = Transform(_pending.ToArray());
        _pending.Clear();
        await inner.WriteAsync(remainder, cancellationToken);
    }

    private List<ReadOnlyMemory<byte>> DrainCompleteLines()
    {
        var lines = new List<ReadOnlyMemory<byte>>();
        while (true)
        {
            var index = _pending.IndexOf((byte)'\n');
            if (index < 0)
            {
                return lines;
            }

            var line = new byte[index + 1];
            _pending.CopyTo(0, line, 0, line.Length);
            _pending.RemoveRange(0, line.Length);
            lines.Add(Transform(line));
        }
    }

    private static ReadOnlyMemory<byte> Transform(byte[] line)
    {
        var text = Encoding.UTF8.GetString(line);
        var transformed = A2AFinalStatus.TransformLine(text);
        return ReferenceEquals(transformed, text)
            ? line
            : Encoding.UTF8.GetBytes(transformed);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Applies <see cref="A2AFinalStatusStream"/> to the A2A runtime routes only.
/// </summary>
public sealed class A2AStreamingFinalMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !A2ARequestContextMiddleware.TryResolveRuntimeAgent(context.Request.Path, out _))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        var rewriter = new A2AFinalStatusStream(originalBody, context.Response);
        context.Response.Body = rewriter;
        try
        {
            await next(context);
            await rewriter.CompleteAsync(context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }
}
