using System.Text;

namespace FoundryCopilotA2A.Cli;

internal sealed class TemporaryTextFile : IDisposable
{
    private TemporaryTextFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string AzureCliReference => $"@{Path}";

    public static async Task<TemporaryTextFile> CreateAsync(
        string content,
        CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"foundry-copilot-a2a-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        return new TemporaryTextFile(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (FileNotFoundException)
        {
            // Already removed.
        }
    }
}
