using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Search.Query;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;
using ApiException = FoundryCopilotA2A.KnowledgeApi.Middleware.ApiException;

[assembly: InternalsVisibleTo("FoundryCopilotA2A.KnowledgeApi.Tests")]

namespace FoundryCopilotA2A.KnowledgeApi.Services.Graph;

internal static class GraphSdkRequests
{
    internal const string BaseUrl = "https://graph.microsoft.com/v1.0/";

    internal sealed class Transport(HttpClient upstream, HttpContext context) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // HttpClient cannot send the same message twice; retain the named client's
            // configured transport without replacing its logging/timeout policy.
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (var option in request.Options)
            {
                forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            }

            var response = await upstream.SendAsync(forwarded, ct);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                // Surface challenges to the API caller before Kiota's automatic CAE replay.
                // Replaying the same OBO token cannot satisfy a renewed interactive sign-in.
                if (response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Any())
                {
                    ResponseSignals.Set(context, ResponseSignals.Challenge,
                        string.Join(", ", response.Headers.WwwAuthenticate));
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    ResponseSignals.Set(context, ResponseSignals.RetryAfter,
                        response.Headers.RetryAfter?.ToString() ?? "5");
                }

                var status = (int)response.StatusCode;
                throw new ApiException(status, "graph_error",
                    $"Microsoft Graph returned HTTP {status}. Check access, delegated consent and tenant policy.");
            }
        }
    }

    internal sealed class DelegatedAuthentication(string accessToken) : IAuthenticationProvider
    {
        public Task AuthenticateRequestAsync(
            RequestInformation request,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateGraphUri(request.URI);
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            return Task.CompletedTask;
        }
    }

    internal static void ValidateGraphUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
        {
            throw new ApiException(502, "invalid_continuation", "Graph returned an unexpected continuation.");
        }
    }

    internal static async Task<JsonDocument> SerializeAsync(IParsable? result, CancellationToken ct)
    {
        if (result is null)
        {
            throw new ApiException(502, "invalid_graph_result", "Graph returned an empty response.");
        }

        // Kiota's serializer preserves Graph's wire names, enum values and additional data.
        // System.Text.Json would serialize SDK implementation details instead.
        using var writer = new JsonSerializationWriter();
        writer.WriteObjectValue(null, result);
        using var stream = writer.GetSerializedContent();
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    internal static string? SiteLookupPath(string? query)
    {
        if (query is null || !(query.Contains("://", StringComparison.Ordinal) ||
            query.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("//", StringComparison.Ordinal)))
        {
            return null;
        }

        if (query.Length > 2048 || query.Any(char.IsControl) || query.Contains('\\') ||
            !Uri.TryCreate(query, UriKind.Absolute, out var site) ||
            site.Scheme != Uri.UriSchemeHttps || !site.IsDefaultPort ||
            site.UserInfo.Length != 0 || site.Query.Length != 0 || site.Fragment.Length != 0 ||
            !site.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidSiteUrl();
        }

        var segments = site.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (segments.Any(segment => segment is "." or ".." ||
            segment.Any(character => char.IsControl(character) || "/\\:?#".Contains(character)) ||
            segment.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidSiteUrl();
        }

        return site.IdnHost + ":/" + string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static ApiException InvalidSiteUrl() => new(
        400, "invalid_site_url",
        "Enter an HTTPS SharePoint site URL, not a page or sharing link. Remove query strings, fragments and credentials.");

    internal static async Task<IParsable?> BrowseAsync(
        GraphServiceClient client, string kind, string? parent, string? drive,
        string? query, string? continuation, CancellationToken ct, string? siteLookupPath = null)
    {
        switch (kind)
        {
            case "sites":
                if (siteLookupPath is not null)
                {
                    // Only the validated host/path become Graph resource identifiers, never a request destination.
                    var site = await client.Sites[siteLookupPath]
                        .WithUrl(BaseUrl + "sites/" + siteLookupPath + "?$select=id,displayName,webUrl")
                        .GetAsync(cancellationToken: ct)
                        ?? throw new ApiException(502, "invalid_graph_result", "Graph returned an empty site response.");
                    if (string.IsNullOrWhiteSpace(site.Id) || string.IsNullOrWhiteSpace(site.DisplayName))
                    {
                        throw new ApiException(502, "invalid_graph_result", "Graph returned an invalid site response.");
                    }
                    return new SiteCollectionResponse { Value = [site] };
                }
                var sites = client.Sites;
                return continuation is not null
                    ? await sites.WithUrl(continuation).GetAsync(cancellationToken: ct)
                    : await sites.GetAsync(r =>
                    {
                        r.QueryParameters.Search = query?.Trim() ?? "*";
                        r.QueryParameters.Select = ["id", "displayName"];
                        r.QueryParameters.Top = 30;
                    }, ct);
            case "drives":
                var drives = client.Sites[parent!].Drives;
                return continuation is not null
                    ? await drives.WithUrl(continuation).GetAsync(cancellationToken: ct)
                    : await drives.GetAsync(r =>
                    {
                        r.QueryParameters.Select = ["id", "name"];
                        r.QueryParameters.Top = 30;
                    }, ct);
            default:
                var children = client.Drives[drive!].Items[string.IsNullOrEmpty(parent) ? "root" : parent].Children;
                return continuation is not null
                    ? await children.WithUrl(continuation).GetAsync(cancellationToken: ct)
                    : await children.GetAsync(r =>
                    {
                        r.QueryParameters.Select = ["id", "name", "folder", "file"];
                        r.QueryParameters.Top = 30;
                    }, ct);
        }
    }

    internal static async Task<IParsable?> ReadToolAsync(
        GraphServiceClient client, string name, JsonElement arguments, CancellationToken ct,
        string? preferredTimeZone = null)
    {
        switch (name)
        {
            case "graph_list_recent_mail":
                var unreadOnly = arguments.GetProperty("unreadOnly").GetBoolean();
                return await client.Me.MailFolders["inbox"].Messages.GetAsync(r =>
                {
                    r.QueryParameters.Select = GraphSkill.RecentMailFields;
                    r.QueryParameters.Orderby = ["receivedDateTime desc"];
                    r.QueryParameters.Top = GraphSkill.RecentMailCount;
                    if (unreadOnly)
                    {
                        r.QueryParameters.Filter = GraphSkill.UnreadMailFilter;
                    }
                }, ct);
            case "graph_search_mail":
                var query = arguments.GetProperty("query").GetString()!;
                return await client.Me.Messages.GetAsync(r =>
                {
                    r.QueryParameters.Search = "\"" + query.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                    r.QueryParameters.Top = 10;
                    r.QueryParameters.Select = GraphSkill.SearchMailFields;
                }, ct);
            case "graph_list_calendar":
                var start = DateTimeOffset.Parse(arguments.GetProperty("start").GetString()!, CultureInfo.InvariantCulture);
                var end = DateTimeOffset.Parse(arguments.GetProperty("end").GetString()!, CultureInfo.InvariantCulture);
                return await client.Me.CalendarView.GetAsync(r =>
                {
                    r.QueryParameters.Select = GraphSkill.CalendarFields;
                    r.QueryParameters.Orderby = [GraphSkill.CalendarOrder];
                    r.QueryParameters.Top = GraphSkill.CalendarPageSize;
                    r.QueryParameters.StartDateTime = GraphSkill.FormatGraphUtc(start);
                    r.QueryParameters.EndDateTime = GraphSkill.FormatGraphUtc(end);
                    if (IsHeaderSafeTimeZone(preferredTimeZone))
                    {
                        // Graph returns start/end in the user's zone instead of UTC.
                        r.Headers.Add("Prefer", $"outlook.timezone=\"{preferredTimeZone}\"");
                    }
                }, ct);
            case "graph_list_todo_lists":
                // The To Do lists endpoint rejects $top/$select with HTTP 400.
                return await client.Me.Todo.Lists.GetAsync(cancellationToken: ct);
            case "graph_list_todo_tasks":
                return await client.Me.Todo.Lists[arguments.GetProperty("listId").GetString()!].Tasks.GetAsync(cancellationToken: ct);
            case "graph_list_my_planner_tasks":
                return await client.Me.Planner.Tasks.GetAsync(cancellationToken: ct);
            case "graph_list_plan_tasks":
                return await client.Planner.Plans[arguments.GetProperty("planId").GetString()!].Tasks.GetAsync(cancellationToken: ct);
            default:
                throw new ApiException(400, "unknown_graph_tool", "This Graph tool is not available.");
        }
    }

    internal static bool IsHeaderSafeTimeZone(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '.' or '-' or '+' or '_' or '/' or '(' or ')');

    internal static async Task<IParsable?> SearchAsync(
        GraphServiceClient client, string source, string question, CancellationToken ct) =>
        await client.Search.Query.PostAsQueryPostResponseAsync(new QueryPostRequestBody
        {
            Requests =
            [
                new SearchRequest
                {
                    EntityTypes = [source == "teams" ? EntityType.ChatMessage : EntityType.Person],
                    Query = new SearchQuery { QueryString = question },
                    From = 0,
                    Size = 5
                }
            ]
        }, cancellationToken: ct);
}
