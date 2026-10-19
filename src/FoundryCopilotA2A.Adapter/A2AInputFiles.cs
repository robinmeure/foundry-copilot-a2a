using System.Text.Json;
using Microsoft.Agents.Core.Models;

namespace FoundryCopilotA2A.Adapter;

public sealed record A2AInputFile(string Name, string MediaType, byte[] Bytes)
{
    public override string ToString() => $"A2AInputFile {{ MediaType = {MediaType}, Size = {Bytes.Length} }}";
}

public static class A2AInputFiles
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    public const string UnsupportedProviderMessage =
        "Experimental file input is available only for Copilot Studio agents, not Foundry or APIM agents.";
    public static readonly string[] MediaTypes =
        ["text/plain", "text/csv", "application/pdf",
         "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "image/png", "image/jpeg"];

    public static IReadOnlyList<A2AInputFile> Read(JsonElement message)
    {
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var files = new List<A2AInputFile>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("url", out _))
            {
                throw new AdapterRequestException("File URLs are not accepted. Attach inline file bytes instead.");
            }
            string name, mediaType, encoded;
            if (part.TryGetProperty("raw", out var raw))
            {
                if (part.TryGetProperty("text", out _) || part.TryGetProperty("data", out _))
                    throw new AdapterRequestException("A file part cannot also contain text or data.");
                encoded = ReadString(raw);
                name = ReadString(part, "filename");
                mediaType = ReadString(part, "mediaType");
            }
            else if (part.TryGetProperty("file", out var file))
            {
                if (file.ValueKind != JsonValueKind.Object || file.TryGetProperty("uri", out _))
                    throw new AdapterRequestException("Only inline file bytes are accepted.");
                encoded = ReadString(file, "bytes");
                name = ReadString(file, "name");
                mediaType = ReadString(file, "mimeType");
            }
            else continue;

            if (files.Count != 0)
                throw new AdapterRequestException("Attach only one file per message.");
            if (name.Length is < 1 or > 180 || name.Any(character =>
                char.IsControl(character) || character is '/' or '\\' or ':'))
                throw new AdapterRequestException("Use a filename of 1-180 characters without paths or control characters.");
            var extension = Path.GetExtension(name).ToLowerInvariant();
            var expected = extension switch
            {
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => null
            };
            if (expected is null || mediaType != expected)
                throw new AdapterRequestException("Attach a TXT, CSV, PDF, DOCX, PNG or JPEG file with its matching media type.");
            if (encoded.Length > ((MaximumBytes + 2) / 3) * 4)
                throw new AdapterRequestException("Files must be nonempty and no larger than 5 MB.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException)
            {
                throw new AdapterRequestException("The attachment must contain valid base64 file bytes.");
            }
            if (bytes.Length is < 1 or > MaximumBytes)
                throw new AdapterRequestException("Files must be nonempty and no larger than 5 MB.");
            files.Add(new A2AInputFile(name, mediaType, bytes));
        }
        return files;
    }

    public static Activity CreateMessage(string prompt, IReadOnlyList<A2AInputFile> files) => new()
    {
        Type = ActivityTypes.Message,
        Text = prompt,
        Attachments = files.Select(file => new Attachment
        {
            Name = file.Name,
            ContentType = file.MediaType,
            ContentUrl = $"data:{file.MediaType};base64,{Convert.ToBase64String(file.Bytes)}"
        }).ToList()
    };

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? ReadString(value) :
            throw new AdapterRequestException("File parts require a filename, media type and base64 bytes.");

    private static string ReadString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! :
            throw new AdapterRequestException("File metadata and base64 bytes must be strings.");
}
