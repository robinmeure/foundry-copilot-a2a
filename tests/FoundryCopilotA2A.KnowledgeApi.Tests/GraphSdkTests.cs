using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Documents;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Requests;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using ApiException = FoundryCopilotA2A.KnowledgeApi.Middleware.ApiException;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class GraphSdkTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MsalFactory(HttpMessageHandler handler) : IMsalHttpClientFactory
    {
        private readonly HttpClient client = new(handler, disposeHandler: false);
        public HttpClient GetHttpClient() => client;
    }

    private static readonly IConfiguration Live = TestConfiguration.Create("Azure");

    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static GraphService Service(HttpMessageHandler handler) =>
        new(new Factory(handler), Live, (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("obo-" + context.User.FindFirstValue("oid"));
        });

    private static DefaultHttpContext Context(string user = "caller")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tid", "tenant"), new Claim("oid", user)], "test"))
        };
        context.Request.Headers.Authorization = "Bearer incoming-api-token";
        return context;
    }

    [Theory]
    [InlineData("graph_search_mail", """{"query":"from:alex \"budget\" & launch"}""", "/v1.0/me/messages", "mail", """{"id":"m1","subject":"Budget","from":{"emailAddress":{"address":"alex@example.test"}},"receivedDateTime":"2026-10-26T10:00:00Z","bodyPreview":"Launch","webLink":"https://outlook.office.com/mail/m1"}""")]
    [InlineData("graph_list_recent_mail", """{"unreadOnly":true}""", "/v1.0/me/mailFolders/inbox/messages", "mail", """{"id":"m2","subject":"New","from":{"emailAddress":{"address":"morgan@example.test"}},"receivedDateTime":"2026-10-27T08:00:00Z","isRead":false,"bodyPreview":"Hi","webLink":"https://outlook.office.com/mail/m2"}""")]
    [InlineData("graph_list_calendar", """{"start":"2026-10-27T00:00:00+01:00","end":"2026-10-28T00:00:00+01:00"}""", "/v1.0/me/calendarView", "calendar", """{"id":"e1","subject":"Sync on Today","isAllDay":false,"isCancelled":false,"webLink":"https://outlook.office365.com/owa/?itemid=AAMk%3D&exvsurl=1&path=/calendar/item","onlineMeetingUrl":null,"start":{"dateTime":"2026-10-27T10:30:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-27T11:00:00.0000000","timeZone":"UTC"},"location":{"displayName":"","locationType":"default","uniqueIdType":"unknown","address":{},"coordinates":{}},"onlineMeeting":null}""")]
    [InlineData("graph_list_todo_lists", "{}", "/v1.0/me/todo/lists", "todo", """{"@odata.etag":"W/\"etag\"","displayName":"Tasks","isOwner":true,"isShared":false,"wellknownListName":"defaultList","id":"AQMkAGNhNWE4OWUxLWQwOTAtNGRjOC1iOWJhLTdlMmUwNTAxMmYyYwAuAAADPBagwcK_nk2QDYrgW8dquAEASCmAadLEbEubFEWjkvTQSQAAAgESAAAA"}""")]
    [InlineData("graph_list_todo_tasks", """{"listId":"AQMkAGNhNWE4OWUxLWQwOTAtNGRjOC1iOWJhLTdlMmUwNTAxMmYyYwAuAAADPBagwcK_nk2QDYrgW8dquAEASCmAadLEbEubFEWjkvTQSQAAAgESAAAA"}""", "/v1.0/me/todo/lists/AQMkAGNhNWE4OWUxLWQwOTAtNGRjOC1iOWJhLTdlMmUwNTAxMmYyYwAuAAADPBagwcK_nk2QDYrgW8dquAEASCmAadLEbEubFEWjkvTQSQAAAgESAAAA/tasks", "todo", """{"importance":"normal","isReminderOn":false,"status":"notStarted","title":"Adhere to styling guidelines","createdDateTime":"2026-09-28T10:25:11.2486166Z","lastModifiedDateTime":"2026-09-28T10:25:58.8888345Z","hasAttachments":false,"categories":["Blue category"],"id":"AAMk=","body":{"content":"","contentType":"text"},"dueDateTime":{"dateTime":"2026-10-04T22:00:00.0000000","timeZone":"UTC"},"recurrence":{"pattern":{"type":"absoluteMonthly","interval":1,"month":0,"dayOfMonth":5,"daysOfWeek":[],"firstDayOfWeek":"sunday","index":"first"},"range":{"type":"noEnd","startDate":"2026-10-05","endDate":"0001-01-01","recurrenceTimeZone":"UTC","numberOfOccurrences":0}}}""")]
    [InlineData("graph_list_my_planner_tasks", "{}", "/v1.0/me/planner/tasks", "planner", """{"id":"task","title":"Review","planId":"plan","percentComplete":50}""")]
    [InlineData("graph_list_plan_tasks", """{"planId":"plan"}""", "/v1.0/planner/plans/plan/tasks", "planner", """{"id":"task","title":"Review","planId":"plan","percentComplete":50}""")]
    public async Task TypedToolsPreserveRoutesSelectedFieldsAndWireShapes(
        string tool, string args, string path, string source, string item)
    {
        using var handler = new Handler((request, ct) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(path, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            Assert.Equal("obo-caller", request.Headers.Authorization!.Parameter);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (source is "planner" or "todo")
            {
                Assert.Empty(query);
            }
            else
            {
                var expectedTop = tool switch
                {
                    "graph_list_recent_mail" => "6",
                    "graph_list_calendar" => "50",
                    _ => "10"
                };
                Assert.Equal(expectedTop, query["$top"]);
                Assert.Contains("id", query["$select"].ToString());
            }
            if (tool == "graph_list_recent_mail")
            {
                Assert.Equal("receivedDateTime desc", query["$orderby"]);
                Assert.Contains("isRead", query["$select"].ToString());
                Assert.Equal("receivedDateTime ge 1900-01-01T00:00:00Z and isRead eq false", query["$filter"]);
                Assert.False(query.ContainsKey("$search"));
            }
            if (tool == "graph_search_mail")
                Assert.Equal("\"from:alex \\\"budget\\\" & launch\"", query["$search"]);
            if (source == "calendar")
            {
                Assert.Equal(string.Join(',', GraphSkill.CalendarFields), query["$select"]);
                Assert.Equal("start/dateTime", query["$orderby"]);
                Assert.Equal("2026-10-26T23:00:00.000Z", query["startDateTime"]);
                Assert.Equal("2026-10-27T23:00:00.000Z", query["endDateTime"]);
            }
            return Task.FromResult(Json($$"""{"value":[{{item}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me?$skiptoken=opaque"}"""));
        });
        var skill = new GraphSkill(Service(handler), Live);
        using var arguments = JsonDocument.Parse(args);
        var result = await skill.Execute(Context(), tool, arguments.RootElement, [source], default);
        var output = Assert.Single(result.Items);
        using var original = JsonDocument.Parse(item);
        foreach (var property in original.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("@odata.", StringComparison.Ordinal) || property.Value.ValueKind == JsonValueKind.Null)
            {
                Assert.False(output.TryGetProperty(property.Name, out _), property.Name);
                continue;
            }

            Assert.True(output.TryGetProperty(property.Name, out var actual), property.Name);
            if (property.Value.ValueKind == JsonValueKind.String && !property.Name.EndsWith("DateTime", StringComparison.Ordinal))
                Assert.Equal(property.Value.GetString(), actual.GetString());
        }
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("teams", "chatMessage")]
    [InlineData("people", "person")]
    public async Task TypedSearchPreservesRequestAndEvidence(string source, string entity)
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/search/query", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var search = json.RootElement.GetProperty("requests")[0];
            Assert.Equal(entity, search.GetProperty("entityTypes")[0].GetString());
            Assert.Equal("Morgan", search.GetProperty("query").GetProperty("queryString").GetString());
            Assert.Equal(0, search.GetProperty("from").GetInt32());
            Assert.Equal(5, search.GetProperty("size").GetInt32());
            return Json("""{"value":[{"hitsContainers":[{"hits":[{"hitId":"hit1","summary":"<b>Morgan</b> &amp; launch","resource":{"@odata.type":"#microsoft.graph.chatMessage","id":"message","webLink":"https://teams.microsoft.com/l/message/1"}}]}]}]}""");
        });
        var evidence = await Service(handler).Evidence(Context(), "Morgan", [source], default);
        var passage = Assert.Single(evidence.Passages);
        Assert.Equal("hit1", passage.Id);
        Assert.Equal(" Morgan  & launch", passage.Content);
        Assert.Equal("https://teams.microsoft.com/l/message/1", passage.Url);
        Assert.Empty(evidence.Warnings);
    }

    [Fact]
    public async Task PeopleSearchPreservesBoundedPersonFieldsWhenSummaryIsEmpty()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("""
            {"value":[{"hitsContainers":[{"hits":[{
              "hitId":"person1","summary":"","resource":{
                "@odata.type":"#microsoft.graph.person","displayName":"<b>Morgan</b> &amp; Lee",
                "jobTitle":"Architect","department":"Finance","officeLocation":"London",
                "userPrincipalName":"morgan@example.test",
                "emailAddresses":[{"address":"contact@example.test","rank":1}],
                "internalSecret":"not-an-allowed-person-field"
              }
            }]}]}]}
            """)));
        var skill = new GraphSkill(Service(handler), Live);
        var result = await skill.Execute(Context(), "graph_search_people",
            JsonSerializer.SerializeToElement(new { query = "Morgan" }), ["people"], default);
        var item = Assert.Single(result.Items);
        Assert.Equal(" Morgan  & Lee", item.GetProperty("title").GetString());
        var content = item.GetProperty("bodyPreview").GetString()!;
        Assert.Contains("Architect", content);
        Assert.Contains("Finance", content);
        Assert.Contains("London", content);
        Assert.Contains("morgan@example.test", content);
        Assert.Contains("contact@example.test", content);
        Assert.DoesNotContain("<b>", content);
        Assert.DoesNotContain("not-an-allowed-person-field", content);
        Assert.True(content.Length <= 1000);
    }

    [Fact]
    public async Task TeamsSearchForbiddenStillFallsBackToTypedChats()
    {
        var calls = 0;
        using var handler = new Handler((request, ct) =>
        {
            calls++;
            if (calls == 1)
                return Task.FromResult(Json("""{"error":{"code":"Forbidden","message":"not consented"}}""", HttpStatusCode.Forbidden));
            Assert.Equal("/v1.0/me/chats", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("lastMessagePreview", query["$expand"]);
            Assert.Equal("10", query["$top"]);
            Assert.Equal("lastMessagePreview/createdDateTime desc", query["$orderby"]);
            return Task.FromResult(Json("""{"value":[{"id":"chat1","topic":"Launch","webUrl":"https://teams.microsoft.com/l/chat/1","lastMessagePreview":{"body":{"contentType":"html","content":"<b>Morgan</b> approved"}}}]}"""));
        });
        using var args = JsonDocument.Parse("""{"query":"Morgan"}""");
        var result = await new GraphSkill(Service(handler), Live).Execute(
            Context(), "graph_search_teams", args.RootElement, ["teams"], default);
        var item = Assert.Single(result.Items);
        Assert.Equal("Launch", item.GetProperty("subject").GetString());
        Assert.Equal("Morgan  approved", item.GetProperty("bodyPreview").GetString());
        Assert.True(result.Truncated);
        Assert.Contains("HTTP 403", result.Coverage);
        Assert.Contains("at most 10 recent chat previews", result.Coverage);
        Assert.Contains("not a full Teams search", result.Coverage);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("teams")]
    [InlineData("people")]
    public async Task SearchResultsUseSharedTextAndCredentialUrlSanitization(string source)
    {
        var hits = Enumerable.Range(0, 7).Select(i => new
        {
            hitId = $"hit{i}",
            summary = "<b>" + new string('x', 5000) + "</b>",
            resource = new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.chatMessage",
                ["id"] = $"message{i}",
                ["webLink"] = i == 0 ? "https://teams.microsoft.com/l/message/1" : "https://example.test?token=secret"
            }
        });
        using var handler = new Handler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        {
            value = new[] { new { hitsContainers = new[] { new { hits } } } }
        }))));
        using var args = JsonDocument.Parse("""{"query":"Morgan"}""");
        var result = await new GraphSkill(Service(handler), Live).Execute(
            Context(), $"graph_search_{source}", args.RootElement, [source], default);
        Assert.Equal(5, result.Items.Length);
        Assert.True(result.Truncated);
        Assert.Contains("at most 5 hits", result.Coverage);
        Assert.DoesNotContain("403", result.Coverage);
        Assert.Equal("https://teams.microsoft.com/l/message/1", result.Items[0].GetProperty("webLink").GetString());
        Assert.All(result.Items.Skip(1), item => Assert.False(item.TryGetProperty("webLink", out _)));
        Assert.All(result.Items, item =>
        {
            Assert.Equal(1000, item.GetProperty("bodyPreview").GetString()!.Length);
            Assert.DoesNotContain("<b>", item.GetProperty("bodyPreview").GetString());
        });
    }

    [Fact]
    public async Task TeamsFallbackNormalizesAndLimitsTheFirstPageBeforeRanking()
    {
        var chats = Enumerable.Range(0, 12).Select(i => new
        {
            id = $"chat{i}",
            topic = "<b>Recent chat</b>",
            webUrl = "https://example.test?access_token=secret",
            lastMessagePreview = new
            {
                body = new { contentType = "html", content = i > 9 ? "Morgan" : "<b>" + new string('x', 5000) + "</b>" }
            }
        });
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json("{}", HttpStatusCode.Forbidden)
            : Json(JsonSerializer.Serialize(new { value = chats }))));
        using var args = JsonDocument.Parse("""{"query":"Morgan"}""");
        var result = await new GraphSkill(Service(handler), Live).Execute(
            Context(), "graph_search_teams", args.RootElement, ["teams"], default);
        Assert.Equal(10, result.Items.Length);
        Assert.True(result.Truncated);
        Assert.Contains("HTTP 403", result.Coverage);
        Assert.Contains("not a full Teams search", result.Coverage);
        Assert.All(result.Items, item =>
        {
            Assert.DoesNotContain(item.GetProperty("id").GetString(), new[] { "chat10", "chat11" });
            Assert.Equal(" Recent chat ", item.GetProperty("subject").GetString());
            Assert.Equal(1000, item.GetProperty("bodyPreview").GetString()!.Length);
            Assert.False(item.TryGetProperty("webLink", out _));
        });
    }

    [Fact]
    public async Task EmptyTeamsFallbackStillDisclosesDeniedSearchAndIncompleteCoverage()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json("{}", HttpStatusCode.Forbidden)
            : Json("""{"value":[]}""")));
        using var args = JsonDocument.Parse("""{"query":"Morgan"}""");
        var result = await new GraphSkill(Service(handler), Live).Execute(
            Context(), "graph_search_teams", args.RootElement, ["teams"], default);
        Assert.Empty(result.Items);
        Assert.True(result.Truncated);
        Assert.Contains("HTTP 403", result.Coverage);
        Assert.Contains("not evidence of no matches", result.Coverage);
    }

    [Theory]
    [InlineData(401, "WWW-Authenticate", "Bearer error=\"insufficient_claims\", claims=\"opaque\"")]
    [InlineData(429, "Retry-After", "17")]
    [InlineData(403, "", "")]
    [InlineData(404, "", "")]
    [InlineData(503, "", "")]
    public async Task SdkErrorsRetainSafeStatusAndChallengeHeaders(int status, string header, string value)
    {
        var calls = 0;
        using var handler = new Handler((request, ct) =>
        {
            calls++;
            var response = Json("""{"error":{"code":"Failure","message":"sensitive upstream message"}}""", (HttpStatusCode)status);
            if (header.Length > 0) response.Headers.TryAddWithoutValidation(header, value);
            return Task.FromResult(response);
        });
        var context = Context();
        using var args = JsonDocument.Parse("{}");
        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            new GraphSkill(Service(handler), Live).Execute(context, "graph_list_todo_lists", args.RootElement, ["todo"], default));
        Assert.Equal(status, exception.Status);
        Assert.Equal("graph_error", exception.Code);
        Assert.DoesNotContain("sensitive", exception.Message);
        if (header.Length > 0) Assert.Equal(value, context.Response.Headers[header]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingRetryAfterRetainsFiveSecondDefault()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)));
        var context = Context();
        await Assert.ThrowsAsync<ApiException>(() => Service(handler).Browse(context, "sites", null, null, null, null, default));
        Assert.Equal("5", context.Response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/", "/v1.0/sites/contoso.sharepoint.com:/")]
    [InlineData(" https://CONTOSO.sharepoint.com/sites/Finance/ ", "/v1.0/sites/contoso.sharepoint.com:/sites/Finance")]
    [InlineData("https://contoso.sharepoint.com/teams/Project%20A", "/v1.0/sites/contoso.sharepoint.com:/teams/Project A")]
    public async Task BrowseSiteUrlResolvesSiteInsteadOfSearching(string url, string path)
    {
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal(path, Uri.UnescapeDataString(request.RequestUri.AbsolutePath));
            Assert.Equal("obo-caller", request.Headers.Authorization!.Parameter);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.False(query.ContainsKey("$search"));
            Assert.False(query.ContainsKey("search"));
            Assert.False(query.ContainsKey("$top"));
            Assert.Equal("id,displayName,webUrl", query["$select"]);
            return Task.FromResult(Json("""{"id":"resolved-site","displayName":"Finance","webUrl":"https://contoso.sharepoint.com/sites/Finance"}"""));
        });
        var result = await Service(handler).Browse(Context(), "sites", null, null, url, null, default);
        Assert.Equal("resolved-site", Assert.Single(result.Items).Id);
        Assert.Equal("resolved-site", result.ResolvedSiteId);
        Assert.Null(result.NextPage);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("http://contoso.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.com.evil.test/")]
    [InlineData("https://user@contoso.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.com:8443/")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance?query=1")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance#fragment")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/SitePages/Home.aspx")]
    [InlineData("//contoso.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.com/sites/a%2Fb")]
    [InlineData("https://contoso.sharepoint.com/sites/a%0Ab")]
    [InlineData("https://contoso.sharepoint.com/:f:/s/Finance/link")]
    [InlineData("https://")]
    public async Task BrowseRejectsInvalidSiteUrlsBeforeCallingGraph(string url)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No Graph call expected."));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            Service(handler).Browse(Context(), "sites", null, null, url, null, default));
        Assert.Equal(400, error.Status);
        Assert.Equal("invalid_site_url", error.Code);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task BrowseSiteLookupDoesNotFallBackToSearchOnErrors(int status)
    {
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            calls++;
            Assert.Contains("contoso.sharepoint.com:", Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            return Task.FromResult(Json("""{"error":{"code":"AccessDenied","message":"Upstream details"}}""", (HttpStatusCode)status));
        });
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            Service(handler).Browse(Context(), "sites", null, null, "https://contoso.sharepoint.com/", null, default));
        Assert.Equal(status, error.Status);
        Assert.Equal("graph_error", error.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BrowseSiteLookupRejectsContinuationAndInvalidResponses()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("""{"displayName":"No id"}""")));
        var graph = Service(handler);
        var continuation = await Assert.ThrowsAsync<ApiException>(() =>
            graph.Browse(Context(), "sites", null, null, "https://contoso.sharepoint.com/", "opaque", default));
        Assert.Equal("invalid_continuation", continuation.Code);
        var invalid = await Assert.ThrowsAsync<ApiException>(() =>
            graph.Browse(Context(), "sites", null, null, "https://contoso.sharepoint.com/", null, default));
        Assert.Equal(502, invalid.Status);
        Assert.Equal("invalid_graph_result", invalid.Code);
    }

    [Theory]
    [InlineData(null, "*")]
    [InlineData("", "*")]
    [InlineData("  ", "*")]
    [InlineData("  Finance  ", "Finance")]
    public async Task BrowseSiteKeywordsRemainSearches(string? input, string expected)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("/v1.0/sites", request.RequestUri!.AbsolutePath);
            Assert.Equal(expected, QueryHelpers.ParseQuery(request.RequestUri.Query)["$search"]);
            return Task.FromResult(Json("""{"value":[{"id":"site","displayName":"Finance"}]}"""));
        });
        var page = await Service(handler).Browse(Context(), "sites", null, null, input, null, default);
        Assert.Null(page.ResolvedSiteId);
    }

    [Fact]
    public async Task MockSiteLookupRequiresNoAzureAndHasTheSameResponseShape()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Network is forbidden."));
        var graph = new GraphService(new Factory(handler), TestConfiguration.Create(),
            (_, _) => throw new InvalidOperationException("Token acquisition is forbidden."));
        var page = await graph.Browse(Context(), "sites", null, null, "https://contoso.sharepoint.com/", null, default);
        Assert.Equal(Assert.Single(page.Items).Id, page.ResolvedSiteId);
        Assert.Null(page.NextPage);
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            graph.Browse(Context(), "sites", null, null, "https://evil.test/", null, default));
        Assert.Equal("invalid_site_url", error.Code);
    }

    [Fact]
    public async Task BrowseContinuationRemainsOpaqueCallerAndQueryBound()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .AddSingleton<ContinuationTokens>().BuildServiceProvider();
        var context = Context();
        context.RequestServices = services;
        var calls = 0;
        using var handler = new Handler((request, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("launch", QueryHelpers.ParseQuery(request.RequestUri!.Query)["$search"]);
                return Task.FromResult(Json("""{"value":[{"id":"site1","displayName":"Launch"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/sites?$skiptoken=a%2Bb%3D"}"""));
            }
            Assert.Equal("a+b=", QueryHelpers.ParseQuery(request.RequestUri!.Query)["$skiptoken"]);
            return Task.FromResult(Json("""{"value":[{"id":"site2","displayName":"Next"}]}"""));
        });
        var graph = Service(handler);
        var first = await graph.Browse(context, "sites", null, null, "launch", null, default);
        Assert.Equal("Launch", Assert.Single(first.Items).Name);
        Assert.NotNull(first.NextPage);
        Assert.DoesNotContain("skiptoken", first.NextPage);
        var second = await graph.Browse(context, "sites", null, null, "launch", first.NextPage, default);
        Assert.Null(second.NextPage);
        var changedQuery = await Assert.ThrowsAsync<ApiException>(() =>
            graph.Browse(context, "sites", null, null, "other", first.NextPage, default));
        Assert.Equal("invalid_continuation", changedQuery.Code);
        var other = Context("other");
        other.RequestServices = services;
        await Assert.ThrowsAsync<ApiException>(() => graph.Browse(other, "sites", null, null, "launch", first.NextPage, default));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("https://evil.test/v1.0/sites")]
    [InlineData("http://graph.microsoft.com/v1.0/sites")]
    [InlineData("https://graph.microsoft.com:8443/v1.0/sites")]
    [InlineData("https://user@graph.microsoft.com/v1.0/sites")]
    [InlineData("https://graph.microsoft.com/beta/sites")]
    [InlineData("https://graph.microsoft.com/v1.0/sites#fragment")]
    [InlineData("/v1.0/sites")]
    public async Task BrowseRejectsUnsafeSdkNextLinks(string link)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = link }))));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            Service(handler).Browse(Context(), "sites", null, null, null, null, default));
        Assert.Equal("invalid_continuation", error.Code);
    }

    [Theory]
    [InlineData("drives", "site", null, "/v1.0/sites/site/drives", """{"id":"drive","name":"Documents"}""", false)]
    [InlineData("items", null, "drive", "/v1.0/drives/drive/items/root/children", """{"id":"folder","name":"Folder","folder":{}}""", true)]
    [InlineData("items", "folder", "drive", "/v1.0/drives/drive/items/folder/children", """{"id":"file","name":"Brief","file":{}}""", false)]
    public async Task TypedBrowsePreservesDriveAndFolderShapes(
        string kind, string? parent, string? drive, string path, string item, bool folder)
    {
        using var handler = new Handler((request, ct) =>
        {
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            Assert.Equal("30", QueryHelpers.ParseQuery(request.RequestUri.Query)["$top"]);
            return Task.FromResult(Json($$"""{"value":[{{item}}]}"""));
        });
        var result = await Service(handler).Browse(Context(), kind, parent, drive, null, null, default);
        Assert.Equal(folder, Assert.Single(result.Items).Folder);
    }

    [Theory]
    [InlineData("https://evil.test/v1.0/me")]
    [InlineData("http://graph.microsoft.com/v1.0/me")]
    [InlineData("https://graph.microsoft.com/beta/me")]
    public async Task SdkAuthenticationCannotAttachTokenOutsideAllowedGraphOrigin(string url)
    {
        var request = new RequestInformation { URI = new Uri(url) };
        await Assert.ThrowsAsync<ApiException>(() =>
            new GraphSdkRequests.DelegatedAuthentication("delegated-token").AuthenticateRequestAsync(request));
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task CancelledGraphOperationDoesNotSendRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Network is forbidden."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(handler).Browse(Context(), "sites", null, null, null, null, cancellation.Token));
    }

    [Fact]
    public async Task MissingDownloadAnnotationFallsBackToContentRedirectWithoutLeakingBearer()
    {
        var calls = new List<string>();
        using var handler = new Handler((request, ct) =>
        {
            calls.Add(request.RequestUri!.Host + request.RequestUri.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1.0/drives/drive/items/item")
            {
                return Task.FromResult(Json("""{"name":"Brief.pdf","size":5,"eTag":"v1","webUrl":"https://tenant.sharepoint.com/Brief.pdf","file":{"mimeType":"application/pdf"}}"""));
            }
            if (request.RequestUri.AbsolutePath == "/v1.0/drives/drive/items/item/content")
            {
                Assert.Equal("obo-caller", request.Headers.Authorization!.Parameter);
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://tenant.sharepoint.com/download?token=preauthorized");
                return Task.FromResult(redirect);
            }
            Assert.Equal("tenant.sharepoint.com", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("brief") });
        });
        var file = await Service(handler).Download(Context(), new ImportRequest("drive", "item"), default);
        Assert.Equal("brief", Encoding.UTF8.GetString(file.Bytes));
        Assert.Equal(3, calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.OK, 502)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    public async Task MissingDownloadLocationIsReportedAsClientSafeError(HttpStatusCode status, int expected)
    {
        using var handler = new Handler((request, ct) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/content")
            ? new HttpResponseMessage(status)
            : Json("""{"name":"Brief.json","size":5,"eTag":"v1","file":{}}""")));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            Service(handler).Download(Context(), new ImportRequest("drive", "item"), default));
        Assert.Equal(expected, error.Status);
        Assert.Equal("download_unavailable", error.Code);
    }

    [Fact]
    public async Task ContentRedirectToUnsupportedHostIsRejected()
    {
        using var handler = new Handler((request, ct) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/content"))
            {
                return Task.FromResult(Json("""{"name":"Brief.json","size":5,"eTag":"v1","file":{}}"""));
            }
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.test/download");
            return Task.FromResult(redirect);
        });
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            Service(handler).Download(Context(), new ImportRequest("drive", "item"), default));
        Assert.Equal("unsupported_download_host", error.Code);
    }

    [Fact]
    public async Task SdkDownloadUsesTypedMetadataAndNeverForwardsBearerToSharePoint()
    {
        var calls = 0;
        using var handler = new Handler((request, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("/v1.0/drives/drive/items/item", request.RequestUri!.AbsolutePath);
                Assert.Contains("@microsoft.graph.downloadUrl", QueryHelpers.ParseQuery(request.RequestUri.Query)["$select"].ToString());
                Assert.Equal("obo-caller", request.Headers.Authorization!.Parameter);
                return Task.FromResult(Json("""{"name":"Brief.txt","size":5,"eTag":"v1","webUrl":"https://tenant.sharepoint.com/Brief.txt","file":{"mimeType":"text/plain"},"@microsoft.graph.downloadUrl":"https://tenant.sharepoint.com/download?token=preauthorized"}"""));
            }
            Assert.Equal("tenant.sharepoint.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("brief") });
        });
        var file = await Service(handler).Download(Context(), new ImportRequest("drive", "item"), default);
        Assert.Equal("brief", Encoding.UTF8.GetString(file.Bytes));
        Assert.Equal("Brief.txt", file.Name);
        Assert.Equal("v1", file.ETag);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("""{"name":"Folder","size":0,"folder":{}}""", "not_a_file")]
    [InlineData("""{"name":"Huge","size":20971521,"file":{}}""", "file_too_large")]
    [InlineData("""{"name":"Unsafe","size":5,"file":{},"@microsoft.graph.downloadUrl":"https://evil.test/download"}""", "unsupported_download_host")]
    public async Task DownloadGuardsStillRunBeforeDownload(string metadata, string code)
    {
        var calls = 0;
        using var handler = new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(metadata));
        });
        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            Service(handler).Download(Context(), new ImportRequest("drive", "item"), default));
        Assert.Equal(code, exception.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReauthorizationUsesCurrentCallerAndDetectsChangedEtag()
    {
        var tokens = new List<string?>();
        using var handler = new Handler((request, ct) =>
        {
            tokens.Add(request.Headers.Authorization?.Parameter);
            Assert.Equal("eTag", QueryHelpers.ParseQuery(request.RequestUri!.Query)["$select"]);
            return Task.FromResult(Json("""{"eTag":"v2"}"""));
        });
        var graph = Service(handler);
        var document = new DocumentRecord("doc", "scope", "Brief", DateTimeOffset.UtcNow, 1, "drive", "item", "v2");
        await graph.Reauthorize(Context("first"), document, default);
        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            graph.Reauthorize(Context("second"), document with { ETag = "v1" }, default));
        Assert.Equal("source_changed", exception.Code);
        Assert.Equal(["obo-first", "obo-second"], tokens);
    }

    [Fact]
    public async Task MockCallsRequireNeitherCredentialsNorNetwork()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Network is forbidden."));
        var configuration = TestConfiguration.Create();
        var graph = new GraphService(new Factory(handler), configuration);
        var context = new DefaultHttpContext();
        Assert.NotEmpty((await graph.Browse(context, "sites", null, null, null, null, default)).Items);
        Assert.NotEmpty((await graph.Download(context, new ImportRequest("drive", "item"), default)).Bytes);
        Assert.NotEmpty((await graph.Evidence(context, "Question", ["people"], default)).Passages);
        using var args = JsonDocument.Parse("{}");
        Assert.NotEmpty((await new GraphSkill(graph, configuration).Execute(context, "graph_list_todo_lists", args.RootElement, ["todo"], default)).Items);
    }

    private static DelegatedTokenProvider TokenProvider(HttpMessageHandler handler) =>
        new(ConfidentialClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111")
            .WithClientSecret("unit-test-only")
            .WithAuthority("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222")
            .WithInstanceDiscovery(false)
            .WithHttpClientFactory(new MsalFactory(handler))
            .Build());

    private static HttpResponseMessage Discovery() => Json("""
        {
          "authorization_endpoint":"https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/oauth2/v2.0/authorize",
          "token_endpoint":"https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/oauth2/v2.0/token",
          "issuer":"https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0"
        }
        """);

    [Fact]
    public async Task SharedOboProviderRequestsSeparateResourceScopesAndCachesTokens()
    {
        var tokenCalls = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Discovery();
            tokenCalls++;
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("incoming-api-token", form["assertion"]);
            Assert.Equal("on_behalf_of", form["requested_token_use"]);
            var scope = form["scope"].ToString();
            var resource = scope.Contains("graph.microsoft.com") ? "graph" : "adapter";
            Assert.Contains(resource == "graph" ? "https://graph.microsoft.com/.default" : "api://adapter/access_as_user", scope);
            var clientInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"uid":"caller","utid":"22222222-2222-2222-2222-222222222222"}"""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Json($$"""{"token_type":"Bearer","expires_in":3600,"access_token":"{{resource}}-obo-token","scope":"{{scope}}","client_info":"{{clientInfo}}"}""");
        });
        var provider = TokenProvider(handler);
        var context = Context();
        Assert.Equal("graph-obo-token", await provider.GetTokenAsync(context, ["https://graph.microsoft.com/.default"], default));
        Assert.Equal("graph-obo-token", await provider.GetTokenAsync(context, ["https://graph.microsoft.com/.default"], default));
        Assert.Equal("adapter-obo-token", await provider.GetTokenAsync(context, ["api://adapter/access_as_user"], default));
        Assert.Equal(2, tokenCalls);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/.default", "graph_consent_required")]
    [InlineData("api://adapter/access_as_user", "delegated_consent_required")]
    public async Task SharedOboProviderPreservesClaimsChallenge(string scope, string code)
    {
        const string claims = """{"access_token":{"polids":{"essential":true,"values":["test-policy"]}}}""";
        using var handler = new Handler((request, ct) => Task.FromResult(
            request.Method == HttpMethod.Get
                ? Discovery()
                : Json(JsonSerializer.Serialize(new
                {
                    error = "invalid_grant",
                    error_description = "Additional claims required.",
                    suberror = "basic_action",
                    claims
                }), HttpStatusCode.BadRequest)));
        var context = Context();
        var error = await Assert.ThrowsAsync<ApiException>(() => TokenProvider(handler).GetTokenAsync(context, [scope], default));
        Assert.Equal(401, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal($"Bearer claims=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(claims))}\", error=\"insufficient_claims\"",
            context.Response.Headers.WWWAuthenticate);
    }

    [Fact]
    public async Task SharedOboProviderRejectsMissingBearerBeforeNetwork()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Network is forbidden."));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            TokenProvider(handler).GetTokenAsync(new DefaultHttpContext(), ["https://graph.microsoft.com/.default"], default));
        Assert.Equal("missing_token", error.Code);
    }
}
