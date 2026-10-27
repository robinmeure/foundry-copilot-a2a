using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Stores;

/// <summary>Owns per-scope turn serialization and atomic persistence of bounded conversation history.</summary>
public sealed class ConversationHistoryStore(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<ConversationHistoryStore> logger) : IConversationHistoryStore, IDisposable
{
    private const string HistoryFileName = "conversation.agent.json";
    private const int MaxSessionBytes = 256 * 1024;
    private readonly string root = Path.GetFullPath(
        configuration["Knowledge:DataDirectory"] ?? throw new InvalidOperationException("Knowledge:DataDirectory is required."),
        environment.ContentRootPath);
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    public async Task<GroundedAnswer> RunAsync(
        string scope,
        Func<JsonElement?, CancellationToken, Task<GroundedAnswer>> run,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(scope);
        var gate = GetGate(scope);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var history = await ReadAsync(path, cancellationToken);
            var state = history is not null && history.ExpiresAt > DateTimeOffset.UtcNow
                ? history.Session
                : (JsonElement?)null;
            var result = await run(state, cancellationToken);
            if (result.SessionState is not { } session)
            {
                throw new InvalidOperationException("The orchestrator did not produce conversation history.");
            }
            await WriteAsync(path, new HistoryRecord(DateTimeOffset.UtcNow.AddDays(configuration.GetValue<int>("Knowledge:RetentionDays")), session),
                cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SweepExpiredAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var scope = Path.GetFileName(directory);
            var path = ResolvePath(scope);
            var gate = GetGate(scope);
            // An active turn owns its expiry/commit; cleanup must not queue behind a model call.
            if (!await gate.WaitAsync(0, cancellationToken)) continue;
            try
            {
                var history = await ReadAsync(path, cancellationToken);
                if (history is not null && history.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ApiException)
            {
                logger.LogError(exception, "Conversation history cleanup failed; retrying on next sweep");
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<HistoryRecord?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            if (stream.Length > MaxSessionBytes) throw new InvalidDataException("Conversation history is oversized.");
            var history = await JsonSerializer.DeserializeAsync<HistoryRecord>(stream, cancellationToken: cancellationToken);
            if (history is null || history.Session.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Conversation history is invalid.");
            }
            return history;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(exception, "Could not read conversation history");
            throw new ApiException(503, "history_unavailable", "Conversation history could not be loaded. Please retry later.");
        }
    }

    private async Task WriteAsync(string path, HistoryRecord history, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(history);
            if (bytes.Length > MaxSessionBytes) throw new InvalidDataException("Conversation history is oversized.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(exception, "Could not persist conversation history");
            throw new ApiException(503, "history_unavailable", "Conversation history could not be saved. Please retry later.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Could not remove a temporary conversation history file");
            }
        }
    }

    private string ResolvePath(string scope)
    {
        if (scope.Length != 64 || scope.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Invalid conversation scope.", nameof(scope));
        }
        return Path.Combine(root, scope, HistoryFileName);
    }

    private SemaphoreSlim GetGate(string scope) =>
        gates[int.Parse(scope.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) % gates.Length];

    public void Dispose()
    {
        foreach (var gate in gates) gate.Dispose();
    }

    private sealed record HistoryRecord(
        [property: JsonRequired] DateTimeOffset ExpiresAt,
        [property: JsonRequired] JsonElement Session);
}
