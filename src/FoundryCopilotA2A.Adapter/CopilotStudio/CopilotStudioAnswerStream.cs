using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

internal readonly record struct CopilotStudioAnswerChunk(
    string Text, bool IsInformative, bool ReplacesAnswer = false);

/// <summary>
/// Assembles one Copilot Studio turn into ordered chunks.
/// <para>
/// A streamed answer arrives as informative typing ("Generating plan..."), then typing deltas
/// that build the answer token by token, then a final message for the same stream id. When the
/// final text equals the concatenated deltas it is dropped, because forwarding it would return
/// the answer twice. When it differs, for example because the deltas carried raw
/// <c>\uE200cite…\uE201</c> markers that the final message resolved to <c>[n]</c> links, the
/// final text is canonical: it is forwarded as a replacement of the whole answer, together with
/// every citation and card seen so far. Agents that never stream still send only a message,
/// which is forwarded unchanged.
/// </para>
/// </summary>
internal sealed class CopilotStudioAnswerStream
{
    private readonly HashSet<string> _streamedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamSpan> _streams = new(StringComparer.Ordinal);
    private readonly StringBuilder _answer = new();
    private CitationBundle? _citations;
    private AdaptiveCardBundle? _cards;
    private bool _needsSeparator;

    public int DeltaCount { get; private set; }

    public int SuppressedFinalCount { get; private set; }

    public int ReplacedFinalCount { get; private set; }

    public CopilotInvocationUpdate? NextUpdate(
        IActivity activity, string? messageText, string conversationId, AgentResponder responder)
    {
        var citations = CopilotStudioCitations.Read(activity, responder);
        var cards = CopilotStudioAdaptiveCards.Read(activity, responder);
        var chunk = Next(activity, messageText);
        if (chunk is not { IsInformative: true })
        {
            _citations = AgentCitations.Merge(_citations, citations);
            _cards = A2AResponseText.MergeCards(_cards, cards);
        }
        if (chunk is { ReplacesAnswer: true })
        {
            return new CopilotInvocationUpdate(chunk.Value.Text, conversationId, activity.Id)
            {
                Citations = _citations,
                Cards = _cards,
                ReplacesAnswer = true
            };
        }

        // The final activity may repeat streamed text but carry the only citation or card data.
        return chunk is null && citations is null && cards is null
            ? null
            : new CopilotInvocationUpdate(
                chunk?.Text ?? string.Empty, conversationId, activity.Id,
                chunk?.IsInformative ?? false)
            {
                Citations = citations,
                Cards = cards
            };
    }

    /// <param name="messageText">
    /// The resolved text of a message activity, which may come from an adaptive card rather than
    /// <see cref="IActivity.Text"/>. Null for every activity that is not a message.
    /// </param>
    /// <returns>The chunk to forward, or null when the activity contributes nothing.</returns>
    public CopilotStudioAnswerChunk? Next(IActivity activity, string? messageText)
    {
        var isMessage = messageText is not null ||
                        string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase);
        var stream = CopilotStudioStreamingText.ReadStreamInfo(activity);

        // Only a delta that carried text can stand in for the final message; a stream of empty
        // deltas must still let that message through.
        var isDelta = stream.Role == CopilotStudioStreamRole.Delta &&
                      !string.IsNullOrEmpty(activity.Text);

        if (stream.Role == CopilotStudioStreamRole.Final &&
            stream.StreamId is not null &&
            _streams.TryGetValue(stream.StreamId, out var streamed))
        {
            var final = messageText ?? activity.Text;
            var sent = streamed.Text.ToString();
            if (string.IsNullOrWhiteSpace(final) ||
                string.Equals(final.Trim(), sent.Trim(), StringComparison.Ordinal))
            {
                SuppressedFinalCount++;
                return null;
            }

            // Splice the canonical text over the streamed span, keeping any text around it.
            _answer.Remove(streamed.Start, sent.Length).Insert(streamed.Start, final);
            foreach (var later in _streams.Values.Where(span => span.Start > streamed.Start))
            {
                later.Start += final.Length - sent.Length;
            }
            streamed.Text.Clear().Append(final);
            ReplacedFinalCount++;
            return new CopilotStudioAnswerChunk(
                _answer.ToString(), IsInformative: false, ReplacesAnswer: true);
        }

        var text = isMessage
            ? messageText
            : stream.Role is CopilotStudioStreamRole.Informative or CopilotStudioStreamRole.Delta
                ? activity.Text
                : null;

        // A delta may legitimately be a lone space or newline, so answer fragments are dropped
        // only when truly empty; everything else keeps the whitespace guard.
        if (isDelta ? string.IsNullOrEmpty(text) : string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!isMessage && !isDelta)
        {
            return new CopilotStudioAnswerChunk(text!, IsInformative: true);
        }

        // Deltas are fragments of a single answer, so only the first is separated from whatever
        // preceded it and the rest concatenate verbatim.
        var continuesStream = isDelta &&
                              stream.StreamId is not null &&
                              _streamedIds.Contains(stream.StreamId);
        var separated = _needsSeparator && !continuesStream
            ? Environment.NewLine + text
            : text!;
        _needsSeparator = true;

        if (isDelta && stream.StreamId is not null)
        {
            if (!_streams.TryGetValue(stream.StreamId, out var span))
            {
                span = new StreamSpan(_answer.Length + separated.Length - text!.Length);
                _streams[stream.StreamId] = span;
            }
            span.Text.Append(text);
        }
        _answer.Append(separated);

        if (isDelta)
        {
            DeltaCount++;
            if (stream.StreamId is not null)
            {
                _streamedIds.Add(stream.StreamId);
            }
        }

        return new CopilotStudioAnswerChunk(separated, IsInformative: false);
    }

    private sealed class StreamSpan(int start)
    {
        public int Start { get; set; } = start;
        public StringBuilder Text { get; } = new();
    }
}

/// <summary>What a Copilot Studio activity contributes to the streamed answer.</summary>
internal enum CopilotStudioStreamRole
{
    /// <summary>Not part of a stream; treated as a standalone response.</summary>
    None,

    /// <summary>Transient progress such as "Generating plan..."; never part of the answer.</summary>
    Informative,

    /// <summary>An incremental chunk of the answer.</summary>
    Delta,

    /// <summary>The completed answer. Repeats every delta already sent for the same stream.</summary>
    Final
}

internal readonly record struct CopilotStudioStreamInfo(
    CopilotStudioStreamRole Role,
    string? StreamId)
{
    public static readonly CopilotStudioStreamInfo None = new(CopilotStudioStreamRole.None, null);
}

internal static class CopilotStudioStreamingText
{
    /// <summary>
    /// Classifies an activity against the Copilot Studio streaming protocol. A streamed answer
    /// arrives as informative typing, then delta typing, then a final message that repeats the
    /// concatenation of those deltas, all sharing one stream id.
    /// </summary>
    public static CopilotStudioStreamInfo ReadStreamInfo(IActivity activity)
    {
        if (activity.ChannelData is null)
        {
            return CopilotStudioStreamInfo.None;
        }

        var channelData = activity.ChannelData switch
        {
            JsonElement element => element,
            JsonDocument document => document.RootElement,
            _ => JsonSerializer.SerializeToElement(
                activity.ChannelData,
                activity.ChannelData.GetType())
        };

        if (channelData.ValueKind != JsonValueKind.Object ||
            !channelData.TryGetProperty("streamType", out var streamType) ||
            streamType.ValueKind != JsonValueKind.String)
        {
            return CopilotStudioStreamInfo.None;
        }

        // Progress and deltas ride on typing activities; only the committed answer is a message.
        // Anything else carrying a streamType is outside the protocol and is ignored.
        var isTyping = string.Equals(activity.Type, "typing", StringComparison.OrdinalIgnoreCase);
        var isMessage = string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase);
        var role = streamType.GetString()?.ToLowerInvariant() switch
        {
            "informative" when isTyping => CopilotStudioStreamRole.Informative,
            "streaming" when isTyping => CopilotStudioStreamRole.Delta,
            "final" when isMessage => CopilotStudioStreamRole.Final,
            _ => CopilotStudioStreamRole.None
        };

        if (role == CopilotStudioStreamRole.None)
        {
            return CopilotStudioStreamInfo.None;
        }

        // The first activity of a stream omits streamId and establishes it through its own id.
        var streamId = channelData.TryGetProperty("streamId", out var id) &&
                       id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : activity.Id;

        return new CopilotStudioStreamInfo(role, streamId);
    }
}
