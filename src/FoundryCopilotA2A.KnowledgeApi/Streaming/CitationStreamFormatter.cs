using System.Text;
using System.Text.RegularExpressions;

namespace FoundryCopilotA2A.KnowledgeApi.Streaming;

/// <summary>
/// Converts streamed model text into append-only display text: completed [[cite:n]] markers
/// become [n], and a trailing partial marker is held back until it completes.
/// </summary>
internal sealed partial class CitationStreamFormatter
{
    private const string MarkerPrefix = "[[cite:";
    private const int MaxHeldCharacters = 48;
    private readonly StringBuilder pending = new();

    public string Append(string text)
    {
        pending.Append(text);
        return Drain(final: false);
    }

    public string Flush() => Drain(final: true);

    private string Drain(bool final)
    {
        var buffered = pending.ToString();
        var ready = final ? buffered.Length : FindHoldStart(buffered);
        pending.Clear().Append(buffered, ready, buffered.Length - ready);
        return CompletedMarker().Replace(buffered[..ready], marker =>
            NumericValue().IsMatch(marker.Groups[1].Value) ? $"[{marker.Groups[1].Value}]" : "");
    }

    private static int FindHoldStart(string text)
    {
        for (var index = Math.Max(0, text.Length - MaxHeldCharacters); index < text.Length; index++)
        {
            if (text[index] == '[' && IsPartialMarker(text.AsSpan(index)))
            {
                return index;
            }
        }
        return text.Length;
    }

    private static bool IsPartialMarker(ReadOnlySpan<char> tail)
    {
        if (tail.Length <= MarkerPrefix.Length)
        {
            return MarkerPrefix.AsSpan().StartsWith(tail, StringComparison.Ordinal);
        }
        if (!tail.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Still open: the value has no line break and at most the first closing bracket at the end.
        var value = tail[MarkerPrefix.Length..];
        var close = value.IndexOfAny(']', '\r', '\n');
        return close < 0 || (close == value.Length - 1 && value[close] == ']');
    }

    [GeneratedRegex(@"\[\[cite:([^\]\r\n]*)\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedMarker();

    [GeneratedRegex(@"^[1-9][0-9]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericValue();
}
