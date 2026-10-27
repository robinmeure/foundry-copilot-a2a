using System.Text;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Documents;

public static class Chunker
{
    private const int MaxChunkUtf8Bytes = 2400;
    private const int MaxDocumentChunks = 2000;

    /// <summary>
    /// Splits extracted pages into page-local, newline-aware chunks bounded by UTF-8 byte count
    /// so tokenizer input remains conservative across Latin and non-Latin text.
    /// </summary>
    public static Passage[] Split(
        string documentId,
        string name,
        IEnumerable<ExtractedPage> pages,
        string? url = null,
        string? userId = null)
    {
        var chunks = new List<Passage>();

        foreach (var page in pages)
        {
            var text = page.Text.Trim();
            var start = 0;

            while (start < text.Length)
            {
                var end = FindChunkEnd(text, start);
                var chunkIndex = chunks.Count;

                chunks.Add(new Passage(
                    Id: $"{documentId}-{chunkIndex}",
                    DocumentId: documentId,
                    Title: name,
                    Content: text[start..end],
                    Locator: $"Page {page.Number}, passage {chunkIndex + 1}",
                    Url: url,
                    UserId: userId));

                start = end;
            }
        }

        if (chunks.Count == 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                "no_text",
                "No usable text was extracted. Check scans, encryption or file protection.");
        }

        if (chunks.Count > MaxDocumentChunks)
        {
            throw new ApiException(
                StatusCodes.Status413PayloadTooLarge,
                "too_many_chunks",
                "The document exceeds the 2,000-chunk ingestion budget.");
        }

        return chunks.ToArray();
    }

    private static int FindChunkEnd(string text, int start)
    {
        var end = start;
        var byteCount = 0;

        while (end < text.Length)
        {
            var charLength = char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1;
            var nextCharBytes = Encoding.UTF8.GetByteCount(text.AsSpan(end, charLength));

            if (byteCount + nextCharBytes > MaxChunkUtf8Bytes)
            {
                break;
            }

            byteCount += nextCharBytes;
            end += charLength;
        }

        if (end < text.Length)
        {
            var newlineIndex = text.LastIndexOf('\n', end - 1, end - start);
            var midpoint = start + ((end - start) / 2);

            if (newlineIndex > midpoint)
            {
                end = newlineIndex + 1;
            }
        }

        return end;
    }
}
