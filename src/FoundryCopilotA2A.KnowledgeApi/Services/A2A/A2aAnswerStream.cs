using System.Text;
using System.Text.Json;
using A2A;

namespace FoundryCopilotA2A.KnowledgeApi.Services.A2A;

/// <summary>A source cited by a specialist, as reported by the adapter's citation extension.</summary>
public sealed record A2aCitedSource(
    string Id,
    string Title,
    string? Url,
    string? AgentId,
    string? AgentName,
    string? Locator,
    IReadOnlyList<string> Quotes);

/// <summary>
/// Folds an A2A v1 <c>SendStreamingMessage</c> response into the specialist's final answer,
/// its cited sources and the progress lines it reported along the way.
/// </summary>
/// <remarks>
/// Answer text is accumulated per artifact: <c>append: false</c> replaces what the artifact had
/// (the adapter does this when Copilot Studio's final message differs from its streamed deltas).
/// Parts marked <c>isInformative</c> are progress, not answer text.
/// </remarks>
public sealed class A2aAnswerStream
{
    public const string CitationExtensionUri = "urn:foundry-copilot-a2a:citations:v1";
    public const int MaxProgressLines = 50;
    public const int MaxProgressCharacters = 300;
    public const int MaxSources = 20;

    private readonly List<string> _artifactOrder = [];
    private readonly Dictionary<string, StringBuilder> _artifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceBuilder> _sources = new(StringComparer.Ordinal);
    private readonly StringBuilder _messageText = new();
    private string? _lastProgress;
    private int _progressCount;

    public TaskState? State { get; private set; }

    /// <summary>The answer: artifact text when any arrived, otherwise direct message text.</summary>
    public string Answer
    {
        get
        {
            var artifacts = string.Join(Environment.NewLine,
                _artifactOrder.Select(id => _artifacts[id].ToString()).Where(text => !string.IsNullOrWhiteSpace(text)));
            return artifacts.Length > 0 ? artifacts : _messageText.ToString();
        }
    }

    public IReadOnlyList<A2aCitedSource> Sources => _sources.Values
        .Take(MaxSources)
        .Select(source => source.Build())
        .ToArray();

    /// <summary>Applies one stream event and returns the progress lines it carried, if any.</summary>
    public IReadOnlyList<string> Apply(StreamResponse response)
    {
        var progress = new List<string>();
        if (response.StatusUpdate is { } statusUpdate)
        {
            State = statusUpdate.Status?.State ?? State;
            if (statusUpdate.Status is { State: TaskState.Working or TaskState.Submitted, Message: { } message })
            {
                foreach (var part in message.Parts ?? [])
                {
                    ReadCitations(part);
                    if (part.Text is { } text)
                    {
                        AddProgress(progress, text);
                    }
                }
            }
        }

        if (response.ArtifactUpdate is { Artifact: { } artifact } artifactUpdate)
        {
            ApplyArtifact(artifact, artifactUpdate.Append, progress);
        }

        if (response.Task is { } task)
        {
            State = task.Status?.State ?? State;
            // A terminal task snapshot is authoritative for every artifact it carries.
            foreach (var taskArtifact in task.Artifacts ?? [])
            {
                ApplyArtifact(taskArtifact, append: false, progress);
            }
        }

        if (response.Message is { } direct)
        {
            State ??= TaskState.Completed;
            _messageText.Clear();
            foreach (var part in direct.Parts ?? [])
            {
                ReadCitations(part);
                if (part.Text is { } text && !IsInformative(part))
                {
                    _messageText.Append(text);
                }
            }
        }

        return progress;
    }

    private void ApplyArtifact(Artifact artifact, bool append, List<string> progress)
    {
        var id = artifact.ArtifactId ?? string.Empty;
        if (!_artifacts.TryGetValue(id, out var text))
        {
            _artifacts[id] = text = new StringBuilder();
            _artifactOrder.Add(id);
        }

        var answerParts = (artifact.Parts ?? []).Where(part => part.Text is not null && !IsInformative(part)).ToArray();
        if (!append && answerParts.Length > 0)
        {
            text.Clear();
        }

        foreach (var part in artifact.Parts ?? [])
        {
            ReadCitations(part);
            if (part.Text is not { } value)
            {
                continue;
            }

            if (IsInformative(part))
            {
                AddProgress(progress, value);
            }
            else
            {
                text.Append(value);
            }
        }
    }

    private void AddProgress(List<string> progress, string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0 || _progressCount >= MaxProgressLines ||
            string.Equals(line, _lastProgress, StringComparison.Ordinal))
        {
            return;
        }

        if (line.Length > MaxProgressCharacters)
        {
            line = line[..MaxProgressCharacters] + "…";
        }

        _lastProgress = line;
        _progressCount++;
        progress.Add(line);
    }

    private static bool IsInformative(Part part) =>
        part.Metadata?.TryGetValue("isInformative", out var informative) == true &&
        informative.ValueKind == JsonValueKind.True;

    // Citation data is provider-reported and optional, so malformed entries are skipped, not fatal.
    private void ReadCitations(Part part)
    {
        if (part.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty(CitationExtensionUri, out var bundle) ||
            bundle.ValueKind != JsonValueKind.Object ||
            !string.Equals(ReadString(bundle, "schemaVersion"), "1", StringComparison.Ordinal))
        {
            return;
        }

        if (bundle.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                if (ReadString(source, "id") is not { } id || ReadString(source, "title") is not { } title)
                {
                    continue;
                }

                if (!_sources.TryGetValue(id, out var builder))
                {
                    if (_sources.Count >= MaxSources)
                    {
                        continue;
                    }

                    _sources[id] = builder = new SourceBuilder(id);
                }

                builder.Title = title;
                builder.Url = SafeUrl(ReadString(source, "url")) ?? builder.Url;
                if (source.TryGetProperty("originatingAgent", out var agent) && agent.ValueKind == JsonValueKind.Object)
                {
                    builder.AgentId = ReadString(agent, "id") ?? builder.AgentId;
                    builder.AgentName = ReadString(agent, "name") ?? builder.AgentName;
                }
            }
        }

        if (bundle.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
        {
            foreach (var citation in citations.EnumerateArray())
            {
                if (ReadString(citation, "sourceId") is not { } sourceId || !_sources.TryGetValue(sourceId, out var builder))
                {
                    continue;
                }

                builder.Locator ??= ReadString(citation, "locator");
                if (ReadString(citation, "quote") is { } quote && builder.Quotes.Count < 3 && !builder.Quotes.Contains(quote))
                {
                    builder.Quotes.Add(quote);
                }
            }
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;

    private sealed class SourceBuilder(string id)
    {
        public string Id { get; } = id;
        public string Title { get; set; } = id;
        public string? Url { get; set; }
        public string? AgentId { get; set; }
        public string? AgentName { get; set; }
        public string? Locator { get; set; }
        public List<string> Quotes { get; } = [];

        public A2aCitedSource Build() => new(Id, Title, Url, AgentId, AgentName, Locator, Quotes.ToArray());
    }
}
