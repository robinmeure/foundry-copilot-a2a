using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Graph;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Serialization;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Graph;

/// <summary>Applies delegated authorization and download restrictions to Graph-backed knowledge operations.</summary>
public sealed class GraphService : IGraphService
{
    private const string GraphDefaultScope = "https://graph.microsoft.com/.default";
    private const int MaxIdentifierLength = 512;

    private static readonly string[] SupportedLiveSources = ["teams", "people"];

    private readonly IHttpClientFactory factory;
    private readonly IConfiguration configuration;
    private readonly DelegatedTokenProvider? tokens;
    private readonly Func<HttpContext, CancellationToken, Task<string>>? delegatedToken;

    internal GraphService(
        IHttpClientFactory factory,
        IConfiguration configuration,
        Func<HttpContext, CancellationToken, Task<string>> delegatedToken)
    {
        this.factory = factory;
        this.configuration = configuration;
        this.delegatedToken = delegatedToken;
    }

    public GraphService(
        IHttpClientFactory factory,
        IConfiguration configuration)
        : this(factory, configuration, new DelegatedTokenProvider(configuration))
    {
    }

    public GraphService(
        IHttpClientFactory factory,
        IConfiguration configuration,
        DelegatedTokenProvider tokens)
    {
        this.factory = factory;
        this.configuration = configuration;
        this.tokens = tokens;
    }

    public Task<JsonDocument> ReadForSkill(
        HttpContext context,
        string name,
        JsonElement arguments,
        CancellationToken ct,
        string? preferredTimeZone = null) =>
        SendGraphRequestAsync(context,
            client => GraphSdkRequests.ReadToolAsync(client, name, arguments, ct, preferredTimeZone), ct);

    public async Task<(JsonElement[] Items, bool IsFallback)> SearchForSkillAsync(
        HttpContext context,
        string source,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var passages = await SearchLiveGraphSourceAsync(context, query, source, cancellationToken);
            var items = passages
                .Take(5)
                .Select(passage => JsonSerializer.SerializeToElement(new
                {
                    id = passage.Id,
                    title = passage.Title,
                    bodyPreview = passage.Content,
                    webLink = passage.Url
                }))
                .ToArray();
            return (items, false);
        }
        catch (ApiException exception) when (source == "teams" && exception.Status == StatusCodes.Status403Forbidden)
        {
            // POST /search/query for chatMessage requires ChannelMessage.Read.All (admin consent).
            // Fall back to GET /me/chats with lastMessagePreview, which only requires Chat.Read.
            return (await FallbackListChatsForSkillAsync(context, query, cancellationToken), true);
        }
    }

    public async Task<GraphPage> Browse(
        HttpContext context,
        string kind,
        string? parent,
        string? drive,
        string? query,
        string? nextPage,
        CancellationToken ct)
    {
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var siteLookupPath = kind == "sites" ? GraphSdkRequests.SiteLookupPath(query) : null;
        if (siteLookupPath is not null && nextPage is not null)
        {
            throw new ApiException(400, "invalid_continuation", "A direct site lookup cannot have a continuation.");
        }
        if (configuration["Knowledge:Backend"] == "Mock")
        {
            var page = GetMockBrowsePage(kind);
            return siteLookupPath is null ? page : page with { ResolvedSiteId = page.Items[0].Id };
        }

        var browseScope = BuildBrowsePath(kind, parent, drive, query);
        string? continuation = null;

        // Continuations are protected and scoped to this caller and exact browse operation.
        if (nextPage is not null)
        {
            var protector = context.RequestServices.GetRequiredService<ContinuationTokens>();
            var requestPath = protector.Read(nextPage, Caller.From(context.User, mock: false), browseScope);
            continuation = GraphSdkRequests.BaseUrl + requestPath;
            GraphSdkRequests.ValidateGraphUri(new Uri(continuation));
        }

        using var json = await SendGraphRequestAsync(
            context, client => GraphSdkRequests.BrowseAsync(client, kind, parent, drive, query, continuation, ct, siteLookupPath), ct);

        var items = json.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .Select(element => new GraphItem(
                Id: element.GetProperty("id").GetString()!,
                Name: element.TryGetProperty("displayName", out var displayName)
                    ? displayName.GetString()!
                    : element.GetProperty("name").GetString()!,
                Folder: element.TryGetProperty("folder", out _)))
            .ToArray();

        string? nextContinuation = null;
        if (json.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement))
        {
            if (!Uri.TryCreate(nextLinkElement.GetString(), UriKind.Absolute, out var nextUri))
            {
                throw new ApiException(
                    StatusCodes.Status502BadGateway,
                    "invalid_continuation",
                    "Graph returned an unexpected continuation.");
            }

            GraphSdkRequests.ValidateGraphUri(nextUri);
            var relativeGraphPath = nextUri.PathAndQuery["/v1.0/".Length..];
            nextContinuation = context.RequestServices
                .GetRequiredService<ContinuationTokens>()
                .Write(Caller.From(context.User, mock: false), browseScope, relativeGraphPath);
        }

        return new GraphPage(items, nextContinuation, siteLookupPath is not null ? items.Single().Id : null);
    }

    public async Task<(byte[] Bytes, string Name, string ETag, string? Url)> Download(
        HttpContext context,
        ImportRequest input,
        CancellationToken ct)
    {
        if (configuration["Knowledge:Backend"] == "Mock")
        {
            var mockContent = Encoding.UTF8.GetBytes(
                "Release gates\nThe launch approval owner is Morgan. The launch budget is 42000 EUR.");
            return (mockContent, "Launch brief.txt", "mock-v1", null);
        }

        _ = EscapeGraphIdentifier(input.DriveId);
        _ = EscapeGraphIdentifier(input.ItemId);
        using var itemDocument = await SendGraphRequestAsync(context, async client =>
            await client.Drives[input.DriveId].Items[input.ItemId].GetAsync(r =>
                r.QueryParameters.Select = ["name", "size", "eTag", "webUrl", "file", "@microsoft.graph.downloadUrl"],
                ct), ct);
        var root = itemDocument.RootElement;

        if (!root.TryGetProperty("file", out _))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "not_a_file",
                "Choose a document, not a folder.");
        }

        if (root.GetProperty("size").GetInt64() > configuration.GetValue<int>("Knowledge:MaxFileBytes"))
        {
            throw new ApiException(
                StatusCodes.Status413PayloadTooLarge,
                "file_too_large",
                "The file exceeds 20 MiB.");
        }

        // Graph does not always return the pre-authenticated URL in the metadata response;
        // GET .../content then redirects to the same URL.
        var downloadUrl = root.TryGetProperty("@microsoft.graph.downloadUrl", out var annotation) &&
            annotation.ValueKind == JsonValueKind.String
                ? annotation.GetString()
                : null;
        var downloadUri = Uri.TryCreate(downloadUrl, UriKind.Absolute, out var annotatedUri)
            ? annotatedUri
            : await ResolveContentRedirectAsync(context, input, ct);
        var isSupportedSharePointHost =
            downloadUri.Scheme == Uri.UriSchemeHttps &&
            (downloadUri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) ||
             downloadUri.Host.EndsWith(".sharepoint-df.com", StringComparison.OrdinalIgnoreCase));

        if (!isSupportedSharePointHost)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                "unsupported_download_host",
                "Only tenant SharePoint/OneDrive for Business downloads are supported.");
        }

        // This pre-authorized URL already grants temporary access; do not attach the Graph bearer token.
        using var telemetrySuppression = OpenTelemetry.SuppressInstrumentationScope.Begin();
        using var response = await factory
            .CreateClient("download")
            .GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "download_failed",
                "The authorized SharePoint download failed.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = await Files.ReadLimited(stream, configuration.GetValue<int>("Knowledge:MaxFileBytes"), ct);

        return (
            Bytes: bytes,
            Name: root.GetProperty("name").GetString()!,
            ETag: root.GetProperty("eTag").GetString()!,
            Url: root.GetProperty("webUrl").GetString());
    }

    private async Task<Uri> ResolveContentRedirectAsync(HttpContext context, ImportRequest input, CancellationToken ct)
    {
        var accessToken = await AcquireTokenAsync(context, ct);
        var contentUri = new Uri(
            $"{GraphSdkRequests.BaseUrl}drives/{EscapeGraphIdentifier(input.DriveId)}/items/{EscapeGraphIdentifier(input.ItemId)}/content");
        GraphSdkRequests.ValidateGraphUri(contentUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, contentUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var telemetrySuppression = OpenTelemetry.SuppressInstrumentationScope.Begin();
        // The "download" client never follows redirects, so the bearer token cannot reach another host.
        using var response = await factory
            .CreateClient("download")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
            or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect &&
            response.Headers.Location is { IsAbsoluteUri: true } location)
        {
            return location;
        }

        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Any())
        {
            ResponseSignals.Set(context, ResponseSignals.Challenge, string.Join(", ", response.Headers.WwwAuthenticate));
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            ResponseSignals.Set(context, ResponseSignals.RetryAfter, response.Headers.RetryAfter?.ToString() ?? "5");
        }

        throw status is >= 400 and < 500
            ? new ApiException(status, "download_unavailable",
                "Microsoft Graph did not allow downloading this file. Check access, delegated consent and tenant policy.")
            : new ApiException(StatusCodes.Status502BadGateway, "download_unavailable",
                "Microsoft Graph did not provide a download location for this file.");
    }

    public async Task Reauthorize(
        HttpContext context,
        DocumentRecord document,
        CancellationToken ct)
    {
        if (configuration["Knowledge:Backend"] == "Mock" || document.DriveId is null)
        {
            return;
        }

        _ = EscapeGraphIdentifier(document.DriveId);
        _ = EscapeGraphIdentifier(document.ItemId!);
        using var itemDocument = await SendGraphRequestAsync(context, async client =>
            await client.Drives[document.DriveId].Items[document.ItemId!].GetAsync(
                r => r.QueryParameters.Select = ["eTag"], ct), ct);
        var currentETag = itemDocument.RootElement.GetProperty("eTag").GetString();

        if (currentETag != document.ETag)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "source_changed",
                $"'{document.Name}' changed in SharePoint. Remove and reimport it before asking again.");
        }
    }

    public async Task<GraphEvidence> Evidence(
        HttpContext context,
        string question,
        string[] sources,
        CancellationToken ct)
    {
        var passages = new List<Passage>();
        var warnings = new List<string>();

        foreach (var source in sources.Distinct())
        {
            if (!SupportedLiveSources.Contains(source))
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "invalid_source",
                    "Unknown Microsoft 365 source.");
            }

            if (configuration["Knowledge:Backend"] == "Mock")
            {
                passages.Add(new Passage(
                    Id: $"mock-{source}",
                    DocumentId: source,
                    Title: $"Mock {source}",
                    Content: $"Synthetic {source} fixture: the project owner is Morgan.",
                    Locator: "Live fixture"));
                continue;
            }

            try
            {
                var sourcePassages = await SearchLiveGraphSourceAsync(context, question, source, ct);
                passages.AddRange(sourcePassages);
            }
            catch (ApiException exception) when (exception.Status is StatusCodes.Status403Forbidden
                or StatusCodes.Status404NotFound
                or StatusCodes.Status429TooManyRequests)
            {
                warnings.Add($"{source}: {exception.Message}");
            }
        }

        return new GraphEvidence(passages.ToArray(), warnings.ToArray());
    }

    private static GraphPage GetMockBrowsePage(string kind) =>
        kind switch
        {
            "sites" => new GraphPage([new GraphItem("mock-site", "Product team (mock)")]),
            "drives" => new GraphPage([new GraphItem("mock-drive", "Documents (mock)")]),
            _ => new GraphPage([new GraphItem("mock-file", "Launch brief.txt")])
        };

    private static string BuildBrowsePath(
        string kind,
        string? parent,
        string? drive,
        string? query) =>
        kind switch
        {
            "sites" => $"sites?search={Uri.EscapeDataString(query?.Trim() ?? "*")}&$select=id,displayName&$top=30",
            "drives" => $"sites/{EscapeGraphIdentifier(parent!)}/drives?$select=id,name&$top=30",
            "items" => $"drives/{EscapeGraphIdentifier(drive!)}/{(string.IsNullOrEmpty(parent) ? "root" : $"items/{EscapeGraphIdentifier(parent)}")}/children?$select=id,name,folder,file&$top=30",
            _ => throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_browse",
                "Unknown picker operation.")
        };

    private static string EscapeGraphIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxIdentifierLength ||
            value.Any(char.IsControl))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_graph_id",
                "Invalid Graph item identifier.");
        }

        return Uri.EscapeDataString(value);
    }

    private Task<JsonDocument> SendGraphRequestAsync(
        HttpContext context,
        Func<GraphServiceClient, Task<IParsable?>> send,
        CancellationToken cancellationToken) =>
        WithClientAsync(
            context,
            async client => await GraphSdkRequests.SerializeAsync(await send(client), cancellationToken),
            cancellationToken);

    private Task<string> AcquireTokenAsync(HttpContext context, CancellationToken cancellationToken) =>
        delegatedToken is null
            ? tokens!.GetTokenAsync(context, [GraphDefaultScope], cancellationToken)
            : delegatedToken(context, cancellationToken);

    public async Task<T> WithClientAsync<T>(
        HttpContext context,
        Func<GraphServiceClient, Task<T>> send,
        CancellationToken cancellationToken)
    {
        var accessToken = await AcquireTokenAsync(context, cancellationToken);
        using var upstream = factory.CreateClient("knowledge");
        using var http = new HttpClient(new GraphSdkRequests.Transport(upstream, context))
        {
            Timeout = upstream.Timeout
        };
        using var client = new GraphServiceClient(
            http, new GraphSdkRequests.DelegatedAuthentication(accessToken));
        return await send(client);
    }

    private async Task<List<Passage>> SearchLiveGraphSourceAsync(
        HttpContext context,
        string question,
        string source,
        CancellationToken cancellationToken)
    {
        using var result = await SendGraphRequestAsync(context,
            client => GraphSdkRequests.SearchAsync(client, source, question, cancellationToken), cancellationToken);
        var passages = new List<Passage>();

        var hitContainers = result.RootElement
            .GetProperty("value")
            .EnumerateArray()
            .SelectMany(entry => entry.GetProperty("hitsContainers").EnumerateArray());

        foreach (var container in hitContainers)
        {
            if (!container.TryGetProperty("hits", out var hits))
            {
                continue;
            }

            foreach (var hit in hits.EnumerateArray())
            {
                var resource = hit.GetProperty("resource");
                var rawSummary = hit.TryGetProperty("summary", out var summaryElement)
                    ? summaryElement.GetString() ?? string.Empty
                    : string.Empty;
                var personDetails = source == "people" ? ReadPersonDetails(resource) : string.Empty;
                if (!string.IsNullOrWhiteSpace(personDetails))
                {
                    rawSummary = string.IsNullOrWhiteSpace(rawSummary)
                        ? personDetails
                        : $"{rawSummary}\n{personDetails}";
                }
                else if (source != "people" && string.IsNullOrWhiteSpace(rawSummary))
                {
                    rawSummary = resource.ToString();
                }

                var sanitizedSummary = WebUtility.HtmlDecode(
                    Regex.Replace(
                        rawSummary,
                        "<[^>]+>",
                        " ",
                        RegexOptions.None,
                        TimeSpan.FromMilliseconds(100)));

                var webLink = resource.TryGetProperty("webLink", out var linkElement)
                    ? linkElement.GetString()
                    : null;

                passages.Add(new Passage(
                    Id: hit.GetProperty("hitId").GetString()!,
                    DocumentId: source,
                    Title: source == "people" && resource.TryGetProperty("displayName", out var name) &&
                        name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
                        ? name.GetString()!
                        : $"Graph {source}",
                    Content: sanitizedSummary,
                    Locator: source == "people"
                        ? "Live Graph people search (bounded)"
                        : "Live Graph search (bounded; Teams search may only cover your own messages)",
                    Url: webLink));
            }
        }

        return passages;
    }

    private static string ReadPersonDetails(JsonElement resource)
    {
        string[] fields = ["displayName", "jobTitle", "department", "officeLocation", "companyName", "userPrincipalName"];
        var details = fields
            .Where(field => resource.TryGetProperty(field, out var value) &&
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(field => $"{field}: {resource.GetProperty(field).GetString()}")
            .ToList();
        if (resource.TryGetProperty("emailAddresses", out var emails) && emails.ValueKind == JsonValueKind.Array)
        {
            details.AddRange(emails.EnumerateArray().Take(3)
                .Where(email => email.ValueKind == JsonValueKind.Object &&
                    email.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String)
                .Select(email => $"email: {email.GetProperty("address").GetString()}"));
        }
        return string.Join("\n", details);
    }

    private async Task<JsonElement[]> FallbackListChatsForSkillAsync(
        HttpContext context,
        string query,
        CancellationToken cancellationToken)
    {
        using var document = await SendGraphRequestAsync(
            context,
            async client => await client.Me.Chats.GetAsync(r =>
            {
                r.QueryParameters.Expand = ["lastMessagePreview"];
                r.QueryParameters.Top = 10;
                r.QueryParameters.Orderby = ["lastMessagePreview/createdDateTime desc"];
                r.QueryParameters.Select = ["id", "topic", "chatType", "webUrl", "lastMessagePreview"];
            }, cancellationToken),
            cancellationToken);

        if (!document.RootElement.TryGetProperty("value", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var queryTerms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var items = new List<JsonElement>();
        foreach (var chat in values.EnumerateArray().Take(10))
        {
            var chatId = chat.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "chat" : "chat";
            var topic = chat.TryGetProperty("topic", out var topicEl) && !string.IsNullOrWhiteSpace(topicEl.GetString())
                ? topicEl.GetString()!
                : "Teams chat";
            var webUrl = chat.TryGetProperty("webUrl", out var urlEl) ? urlEl.GetString() : null;

            var previewText = string.Empty;
            if (chat.TryGetProperty("lastMessagePreview", out var preview) &&
                preview.ValueKind == JsonValueKind.Object &&
                preview.TryGetProperty("body", out var body) &&
                body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("content", out var contentEl))
            {
                var raw = contentEl.GetString() ?? string.Empty;
                previewText = WebUtility.HtmlDecode(
                    Regex.Replace(raw, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100))).Trim();
            }

            if (string.IsNullOrWhiteSpace(previewText) && topic == "Teams chat")
            {
                continue;
            }

            items.Add(JsonSerializer.SerializeToElement(new
            {
                id = chatId,
                subject = topic,
                bodyPreview = previewText,
                webLink = webUrl
            }));
        }

        return items
            .OrderByDescending(item =>
            {
                var text = item.GetRawText();
                return queryTerms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            })
            .Take(10)
            .ToArray();
    }
}
