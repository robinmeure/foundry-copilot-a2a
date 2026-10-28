using System.Runtime.CompilerServices;
using System.Text;

namespace FoundryCopilotA2A.Adapter;

internal static class ServerSentEvents
{
    /// <summary>Yields the joined <c>data:</c> payload of each event as soon as it completes.</summary>
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (TakePayload(data) is { } payload)
                {
                    yield return payload;
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsSpan("data:".Length);
                if (value.Length > 0 && value[0] == ' ')
                {
                    value = value[1..];
                }
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(value);
            }
        }

        if (TakePayload(data) is { } trailing)
        {
            yield return trailing;
        }
    }

    private static string? TakePayload(StringBuilder data)
    {
        var payload = data.ToString().Trim();
        data.Clear();
        return payload.Length == 0 || payload is "[DONE]" or "end" ? null : payload;
    }
}
