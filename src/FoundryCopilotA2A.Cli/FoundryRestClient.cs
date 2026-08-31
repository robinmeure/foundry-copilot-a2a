using System.Text.Json;

namespace FoundryCopilotA2A.Cli;

internal static class FoundryRestClient
{
    public static async Task<JsonDocument> InvokeAsync(
        CliContext context,
        HttpMethod method,
        string url,
        string? body,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        TemporaryTextFile? bodyFile = null;
        try
        {
            var commandArguments = new List<string>
            {
                "rest",
                "--method", method.Method,
                "--url", url,
                "--resource", "https://ai.azure.com"
            };

            if (body is not null || headers is not null)
            {
                commandArguments.Add("--headers");
                if (body is not null)
                {
                    commandArguments.Add("Content-Type=application/json");
                }

                if (headers is not null)
                {
                    commandArguments.AddRange(
                        headers.Select(header => $"{header.Key}={header.Value}"));
                }
            }

            if (body is not null)
            {
                bodyFile = await TemporaryTextFile.CreateAsync(body, cancellationToken);
                commandArguments.AddRange(["--body", bodyFile.AzureCliReference]);
            }

            commandArguments.AddRange(["--output", "json"]);
            var response = await context.Processes.CaptureAsync(
                "az",
                commandArguments,
                cancellationToken);

            try
            {
                return JsonDocument.Parse(response.StandardOutput);
            }
            catch (JsonException exception)
            {
                throw new CliException(
                    $"Foundry returned invalid JSON: {exception.Message}");
            }
        }
        finally
        {
            if (bodyFile is not null)
            {
                bodyFile.Dispose();
            }
        }
    }
}
