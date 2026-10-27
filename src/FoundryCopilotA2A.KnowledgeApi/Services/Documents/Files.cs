using System.Text;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Middleware;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Documents;

public static class Files
{
    private const int MaxFileNameLength = 180;
    private const int ReadBufferSize = 65536;

    public static readonly string[] Extensions = [".txt", ".md", ".csv", ".pdf", ".docx"];

    public static void Validate(string name, byte[] bytes)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var hasInvalidFileName =
            name.Length is < 1 or > MaxFileNameLength ||
            name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            name.Any(char.IsControl) ||
            !Extensions.Contains(extension);

        if (hasInvalidFileName)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_file",
                "Choose PDF, DOCX, TXT, Markdown or CSV with a filename without paths.");
        }

        if (bytes.Length == 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "empty_file",
                "Choose a nonempty file.");
        }

        var hasInvalidPdfSignature =
            extension == ".pdf" &&
            !bytes.AsSpan().StartsWith("%PDF-"u8);
        var hasInvalidDocxSignature =
            extension == ".docx" &&
            !bytes.AsSpan().StartsWith("PK\u0003\u0004"u8);

        if (hasInvalidPdfSignature || hasInvalidDocxSignature)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_signature",
                "The file signature does not match its extension.");
        }
    }

    public static async Task<byte[]> ReadLimited(
        Stream stream,
        int limit,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferSize];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(chunk, ct);
            if (bytesRead == 0)
            {
                break;
            }

            if (buffer.Length + bytesRead > limit)
            {
                throw new ApiException(
                    StatusCodes.Status413PayloadTooLarge,
                    "file_too_large",
                    "The file exceeds the configured upload limit.");
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return buffer.ToArray();
    }

    public static ExtractedPage[] Text(byte[] bytes)
    {
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var content = strictUtf8.GetString(bytes).TrimStart('\uFEFF');
            return [new ExtractedPage(1, content)];
        }
        catch (DecoderFallbackException)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_encoding",
                "Text documents must use UTF-8 encoding.");
        }
    }
}
