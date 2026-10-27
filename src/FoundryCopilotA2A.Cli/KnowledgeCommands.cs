using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace FoundryCopilotA2A.Cli;

internal static class KnowledgeCommands
{
    private static string Project => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(RepoPaths.ResolveAdapterProject(null))!, "..", "FoundryCopilotA2A.KnowledgeApi",
        "FoundryCopilotA2A.KnowledgeApi.csproj"));

    public static async Task<int> RunAsync(CliContext context, CommandArguments args, CancellationToken ct)
    {
        args.EnsureOnly("backend", "urls", "allowed-origin", "help");
        var backend = args.Optional("backend", "Mock");
        if (backend is not ("Mock" or "Azure")) throw new CliException("--backend must be Mock or Azure.");
        var urls = args.Optional("urls", "http://localhost:5100")!;
        if (!Uri.TryCreate(urls, UriKind.Absolute, out var binding) ||
            binding.Scheme is not ("http" or "https") || !binding.IsLoopback || binding.AbsolutePath != "/" ||
            binding.Query.Length > 0 || binding.UserInfo.Length > 0 || binding.Fragment.Length > 0)
            throw new CliException("--urls must be a single loopback HTTP(S) origin.");
        var origin = args.Optional("allowed-origin", "http://localhost:5174")!;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
            originUri.Scheme is not ("http" or "https") || originUri.AbsolutePath != "/" ||
            originUri.Query.Length > 0 || originUri.UserInfo.Length > 0 || originUri.Fragment.Length > 0)
            throw new CliException("--allowed-origin must be an HTTP(S) origin.");
        context.Out.WriteLine($"Starting Knowledge WebAPI ({backend}) at {urls}.");
        await context.Processes.RunInteractiveAsync("dotnet",
            ["run", "--project", Project, "--no-launch-profile", "--urls", urls], ct,
            new Dictionary<string, string?>
            {
                ["Knowledge__Backend"] = backend, ["Knowledge__AllowedOrigins__0"] = origin,
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            }, Path.GetDirectoryName(Project));
        return 0;
    }

    public static async Task<int> CreateIndexAsync(CliContext context, CommandArguments args, CancellationToken ct)
    {
        args.EnsureOnly("endpoint", "index", "dimensions", "apply", "help");
        var endpoint = args.AbsoluteHttpUri("endpoint");
        if (endpoint.Scheme != "https" || !endpoint.Host.EndsWith(".search.windows.net", StringComparison.OrdinalIgnoreCase) ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.AbsolutePath != "/" || endpoint.Fragment.Length > 0)
            throw new CliException("--endpoint must be an Azure AI Search HTTPS resource root.");
        var name = args.Optional("index", "asrlocalrag")!;
        if (name.Length is < 2 or > 128 || !char.IsAsciiLetterLower(name[0]) ||
            name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
            throw new CliException("--index must be a lower-case Search index name.");
        var dimensions = args.Integer("dimensions", 3072);
        if (dimensions is < 1 or > 3072) throw new CliException("--dimensions must be between 1 and 3072.");
        var schema = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(Project)!, "search-index.json"), ct))!;
        schema["name"] = name;
        schema["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "contentVector")!["dimensions"] = dimensions;
        if (!args.Flag("apply"))
        {
            context.Out.WriteLine(schema.ToJsonString());
            context.Out.WriteLine("Preview only. Add --apply to create; an existing index will never be overwritten.");
            return 0;
        }
        var token = await context.Processes.CaptureAsync("az",
            ["account", "get-access-token", "--resource", "https://search.azure.com", "--query", "accessToken", "--output", "tsv"], ct);
        var url = $"{endpoint.AbsoluteUri.TrimEnd('/')}/indexes/{name}?api-version=2024-07-01";
        using var check = new HttpRequestMessage(HttpMethod.Get, url);
        check.Headers.Authorization = new("Bearer", token.StandardOutput.Trim());
        using var existing = await context.HttpClient.SendAsync(check, ct);
        if (existing.IsSuccessStatusCode) throw new CliException("The index already exists. Refusing to overwrite it.");
        if (existing.StatusCode != HttpStatusCode.NotFound) existing.EnsureSuccessStatusCode();
        using var create = new HttpRequestMessage(HttpMethod.Put, url);
        create.Headers.Authorization = check.Headers.Authorization;
        create.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        create.Content = new StringContent(schema.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await context.HttpClient.SendAsync(create, ct);
        response.EnsureSuccessStatusCode();
        using var verify = new HttpRequestMessage(HttpMethod.Get, url);
        verify.Headers.Authorization = check.Headers.Authorization;
        using var verified = await context.HttpClient.SendAsync(verify, ct);
        verified.EnsureSuccessStatusCode();
        context.Out.WriteLine($"Created and verified index '{name}'.");
        return 0;
    }

    public static async Task<int> TestSearchAsync(CliContext context, CommandArguments args, CancellationToken ct)
    {
        args.EnsureOnly("endpoint", "index", "request-file", "help");
        var endpoint = args.AbsoluteHttpUri("endpoint");
        if (endpoint.Scheme != "https" || !endpoint.IsDefaultPort ||
            !endpoint.Host.EndsWith(".search.windows.net", StringComparison.OrdinalIgnoreCase) ||
            endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 ||
            endpoint.AbsolutePath != "/" || endpoint.Fragment.Length > 0)
            throw new CliException("--endpoint must be an Azure AI Search HTTPS resource root.");
        var index = args.Optional("index", "kamervragen-openai")!;
        if (index.Length is < 2 or > 128 ||
            index.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
            throw new CliException("--index must be a lower-case Search index name.");
        var path = args.Require("request-file");
        if (!File.Exists(path) || new FileInfo(path).Length > 16384)
            throw new CliException("--request-file must be an existing JSON file of at most 16 KiB.");
        var body = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject
            ?? throw new CliException("--request-file must contain a Search request object.");
        var token = await context.Processes.CaptureAsync("az",
            ["account", "get-access-token", "--resource", "https://search.azure.com", "--query", "accessToken", "--output", "tsv"], ct);
        var accessToken = token.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new CliException("Azure CLI returned no Search access token. Sign in again.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{endpoint.AbsoluteUri.TrimEnd('/')}/indexes/{Uri.EscapeDataString(index)}/docs/search?api-version=2024-07-01");
        request.Headers.Authorization = new("Bearer", accessToken);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await context.HttpClient.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        context.Out.WriteLine($"Search HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
        {
            var error = responseText.Replace(accessToken, "<REDACTED>", StringComparison.Ordinal);
            context.Error.WriteLine(error[..Math.Min(error.Length, 4096)]);
            return 1;
        }
        var results = JsonNode.Parse(responseText)?["value"] as JsonArray
            ?? throw new CliException("Search returned an invalid results envelope.");
        context.Out.WriteLine($"Verified {results.Count} search results. Document contents and tokens are not displayed.");
        return 0;
    }
}
