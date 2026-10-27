using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Chat;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using System.Text.Json;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class GraphSkillTests
{
    [Fact]
    public void CatalogExposesOnlySelectedSourcesAndStrictSchemas()
    {
        Assert.Empty(GraphSkill.Enabled([]));
        Assert.Equal(4, GraphSkill.Enabled(["todo", "planner"]).Length);
        Assert.Equal(["graph_list_recent_mail", "graph_search_mail"], GraphSkill.Enabled(["mail"]).Select(tool => tool.Name));
        Assert.All(GraphSkill.Catalog, tool =>
        {
            var definition = JsonSerializer.SerializeToElement(tool.Definition);
            Assert.Equal("function", definition.GetProperty("type").GetString());
            Assert.True(definition.GetProperty("strict").GetBoolean());
            Assert.False(tool.Parameters.GetProperty("additionalProperties").GetBoolean());
        });
    }

    [Theory]
    [InlineData("graph_list_todo_lists", "{}", "me/todo/lists")]
    [InlineData("graph_list_todo_tasks", """{"listId":"AQ-test=="}""", "me/todo/lists/AQ-test%3D%3D/tasks")]
    [InlineData("graph_list_my_planner_tasks", "{}", "me/planner/tasks")]
    [InlineData("graph_list_plan_tasks", """{"planId":"plan-test"}""", "planner/plans/plan-test/tasks")]
    [InlineData("graph_search_mail", """{"query":"from:alex budget"}""", "me/messages?$search=")]
    [InlineData("graph_list_recent_mail", """{"unreadOnly":false}""", "me/mailFolders/inbox/messages?$select=")]
    public void QueriesAreFixedReadOnlyRoutes(string name, string args, string expected)
    {
        using var json = JsonDocument.Parse(args);
        Assert.StartsWith(expected, GraphSkill.BuildPath(name, json.RootElement));
    }

    [Theory]
    [InlineData("graph_list_plan_tasks", """{"planId":"../users/other"}""")]
    [InlineData("graph_list_todo_tasks", """{"listId":"x?$filter=anything"}""")]
    [InlineData("graph_search_mail", """{"query":"mail","url":"https://evil.test"}""")]
    [InlineData("graph_list_recent_mail", """{"unreadOnly":"yes"}""")]
    [InlineData("graph_list_recent_mail", "{}")]
    [InlineData("graph_list_calendar", """{"start":"2026-10-26","end":"2026-10-27"}""")]
    [InlineData("graph_list_calendar", """{"start":"2026-10-26T00:00:00Z","end":"2026-12-27T00:00:00Z"}""")]
    [InlineData("graph_list_calendar", """{"start":"2026-10-27T00:00:00Z","end":"2026-10-26T00:00:00Z"}""")]
    public void UnsafeAndUnboundedArgumentsAreRejected(string name, string args)
    {
        using var json = JsonDocument.Parse(args);
        Assert.Throws<ApiException>(() => GraphSkill.BuildPath(name, json.RootElement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecentMailListsNewestInboxMessagesWithOptionalUnreadFilter(bool unreadOnly)
    {
        using var json = JsonDocument.Parse($$"""{"unreadOnly":{{(unreadOnly ? "true" : "false")}}}""");
        var path = Uri.UnescapeDataString(GraphSkill.BuildPath("graph_list_recent_mail", json.RootElement));
        Assert.StartsWith("me/mailFolders/inbox/messages?", path);
        Assert.Contains("$select=id,subject,from,receivedDateTime,isRead,bodyPreview,webLink", path);
        Assert.Contains("$orderby=receivedDateTime desc", path);
        Assert.Contains("$top=6", path);
        Assert.DoesNotContain("$search", path);
        Assert.Equal(unreadOnly, path.Contains("$filter=receivedDateTime ge 1900-01-01T00:00:00Z and isRead eq false"));
    }

    [Fact]
    public void ToDoRoutesAreExactlyTheGraphListAndTaskCollections()
    {
        using var noArguments = JsonDocument.Parse("{}");
        using var listArguments = JsonDocument.Parse("""{"listId":"AQMkAGNh_nk2QDYr-AAA="}""");
        Assert.Equal("me/todo/lists", GraphSkill.BuildPath("graph_list_todo_lists", noArguments.RootElement));
        Assert.Equal(
            "me/todo/lists/AQMkAGNh_nk2QDYr-AAA%3D/tasks",
            GraphSkill.BuildPath("graph_list_todo_tasks", listArguments.RootElement));
    }

    [Fact]
    public void CalendarUsesUtcRangeOrderedSelectionAndCapsWindow()
    {
        using var json = JsonDocument.Parse("""{"start":"2026-10-27T00:00:00+01:00","end":"2026-10-28T00:00:00+01:00"}""");
        var path = Uri.UnescapeDataString(GraphSkill.BuildPath("graph_list_calendar", json.RootElement));
        Assert.Equal(
            "me/calendarView?$select=id,subject,start,end,isAllDay,location,webLink,isCancelled,onlineMeeting,onlineMeetingUrl" +
            "&$orderby=start/dateTime&$top=50&startDateTime=2026-10-26T23:00:00.000Z&endDateTime=2026-10-27T23:00:00.000Z",
            path);
    }

    [Theory]
    [InlineData("""{"dateTime":"2026-10-04T22:00:00.0000000","timeZone":"UTC"}""", "W. Europe Standard Time", "2026-10-05")]
    [InlineData("""{"dateTime":"2026-10-04T22:00:00.0000000","timeZone":"UTC"}""", null, "2026-10-04")]
    [InlineData("""{"dateTime":"2026-10-05T00:00:00.0000000","timeZone":"W. Europe Standard Time"}""", "W. Europe Standard Time", "2026-10-05")]
    [InlineData("\"2026-10-05T22:30:00Z\"", "W. Europe Standard Time", "2026-10-06")]
    [InlineData("\"2026-10-05T22:30:00Z\"", null, "2026-10-05")]
    [InlineData("""{"dateTime":"not a date","timeZone":"UTC"}""", null, null)]
    public void DueDatesAreConvertedToTheUsersLocalDate(string dueDateTime, string? zone, string? expected)
    {
        using var json = JsonDocument.Parse(dueDateTime);
        var userZone = zone is null ? null : TimeZoneInfo.FindSystemTimeZoneById(zone);
        Assert.Equal(expected, GraphSkill.LocalDueDate(json.RootElement, userZone));
    }

    [Fact]
    public void ToDoTasksListOpenWorkFirstByLocalDueDate()
    {
        var page = JsonDocument.Parse("""
            {"value":[
              {"id":"done","title":"Done","status":"completed","dueDateTime":{"dateTime":"2026-09-01T22:00:00.0000000","timeZone":"UTC"}},
              {"id":"undated","title":"Someday","status":"notStarted","categories":[]},
              {"id":"styling","title":"Adhere to styling guidelines","status":"notStarted","isReminderOn":false,"hasAttachments":false,
               "categories":["Blue category"],"createdDateTime":"2026-09-28T10:25:11.2486166Z",
               "dueDateTime":{"dateTime":"2026-10-04T22:00:00.0000000","timeZone":"UTC"},
               "recurrence":{"pattern":{"type":"absoluteMonthly","interval":1,"dayOfMonth":5,"daysOfWeek":[]},"range":{"type":"noEnd","startDate":"2026-10-05"}}},
              {"id":"widget","title":"Add Tasks Widget","status":"notStarted","categories":[],
               "dueDateTime":{"dateTime":"2026-10-01T22:00:00.0000000","timeZone":"UTC"}}
            ]}
            """).RootElement;
        var amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        var result = GraphSkill.Normalize(page, "graph_list_todo_tasks", amsterdam);

        Assert.Equal(["widget", "styling", "undated", "done"], result.Items.Select(item => item.GetProperty("id").GetString()));
        var styling = result.Items[1];
        Assert.Equal("2026-10-05", styling.GetProperty("dueDate").GetString());
        Assert.Equal("Blue category", styling.GetProperty("categories")[0].GetString());
        Assert.Equal("absoluteMonthly", styling.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString());
        Assert.Equal("2026-10-02", result.Items[0].GetProperty("dueDate").GetString());
        Assert.Contains("Open tasks first", result.Coverage);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void PlannerTasksListOpenWorkFirstByLocalDueDate()
    {
        var page = JsonDocument.Parse("""
            {"value":[
              {"id":"finished","title":"Finished","percentComplete":100,"dueDateTime":"2026-10-01T10:00:00Z"},
              {"id":"later","title":"Later","percentComplete":50,"dueDateTime":"2026-10-20T10:00:00Z","priority":5},
              {"id":"soon","title":"Soon","percentComplete":0,"dueDateTime":"2026-10-05T22:30:00Z","planId":"plan"}
            ]}
            """).RootElement;
        var amsterdam = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        var result = GraphSkill.Normalize(page, "graph_list_my_planner_tasks", amsterdam);

        Assert.Equal(["soon", "later", "finished"], result.Items.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal("2026-10-06", result.Items[0].GetProperty("dueDate").GetString());
        Assert.Equal(5, result.Items[1].GetProperty("priority").GetInt32());
    }

    [Fact]
    public void CalendarEventsKeepMeetingDetailsAndFlattenLocation()
    {
        var page = JsonDocument.Parse("""
            {"value":[{
              "id":"e1","subject":"Sync on Today","isAllDay":false,"isCancelled":false,
              "webLink":"https://outlook.office365.com/owa/?itemid=AAMk&exvsurl=1&path=/calendar/item",
              "onlineMeetingUrl":null,"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/abc"},
              "start":{"dateTime":"2026-10-27T11:30:00.0000000","timeZone":"W. Europe Standard Time"},
              "end":{"dateTime":"2026-10-27T12:00:00.0000000","timeZone":"W. Europe Standard Time"},
              "location":{"displayName":"Room 4","locationType":"default","address":{},"coordinates":{}}
            }]}
            """).RootElement;

        var item = Assert.Single(GraphSkill.Normalize(page, "graph_list_calendar").Items);

        Assert.Equal("Room 4", item.GetProperty("location").GetString());
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/abc", item.GetProperty("onlineMeeting").GetProperty("joinUrl").GetString());
        Assert.False(item.GetProperty("isAllDay").GetBoolean());
        Assert.False(item.GetProperty("isCancelled").GetBoolean());
        Assert.False(item.TryGetProperty("onlineMeetingUrl", out _));
        Assert.Equal("2026-10-27T11:30:00.0000000", item.GetProperty("start").GetProperty("dateTime").GetString());
    }

    [Fact]
    public void ToolResultsAreBoundedAndExcludeUnselectedDataAndCredentialLinks()
    {
        var items = Enumerable.Range(1, 11).Select(i => new
        {
            id = i.ToString(),
            title = "<b>Task</b>",
            internalSecret = "do-not-return",
            webLink = "https://example.test?token=secret",
            body = new { content = new string('x', 5000) }
        });
        var result = GraphSkill.Normalize(JsonSerializer.SerializeToElement(new { value = items }));
        Assert.True(result.Truncated);
        Assert.Equal(10, result.Items.Length);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(" Task ", item.GetProperty("title").GetString());
            Assert.False(item.TryGetProperty("internalSecret", out _));
            Assert.False(item.TryGetProperty("webLink", out _));
            Assert.Equal(1000, item.GetProperty("body").GetProperty("content").GetString()!.Length);
        });
    }

    [Fact]
    public void SafeLinksAreNotTruncatedIntoDifferentUrls()
    {
        var url = "https://outlook.office.com/mail/" + new string('a', 1500);
        var result = GraphSkill.Normalize(JsonSerializer.SerializeToElement(new
        {
            value = new[] { new { id = "mail1", webLink = url } }
        }));
        Assert.Equal(url, result.Items[0].GetProperty("webLink").GetString());
    }

    [Fact]
    public void NumericCitationsMustReferenceTheEvidenceSet()
    {
        ResponsesCoordinator.ValidateCitations("A [[cite:1]] and B [[cite:2]].", 2);
        Assert.Throws<ApiException>(() => ResponsesCoordinator.ValidateCitations("Invented [[cite:3]]", 2));
        Assert.Throws<ApiException>(() => ResponsesCoordinator.ValidateCitations("Invalid [[cite:0]]", 1));
        Assert.Throws<ApiException>(() => ResponsesCoordinator.ValidateCitations("Invalid [[cite:nope]]", 1));
        Assert.Throws<ApiException>(() => ResponsesCoordinator.ValidateCitations("Invalid [[cite:99999999999999]]", 1));
        Assert.Throws<ApiException>(() => ResponsesCoordinator.ValidateCitations("Invalid [[cite:01]]", 1));
        Assert.Throws<ApiException>(() => ResponsesCoordinator.ValidateCitations("Invalid [[cite:+1]]", 1));
    }

    [Theory]
    [InlineData("Use items[0] or matrix[2][3].")]
    [InlineData("A singleton array: [42].")]
    [InlineData("```js\nreturn items[999];\n```")]
    [InlineData("The link is [42](https://example.test).")]
    [InlineData("Use `items[0]` and `[0]`, not an evidence reference.")]
    public void OrdinaryBracketsAreNotEvidenceReferences(string answer)
    {
        ResponsesCoordinator.ValidateCitations(answer, 0);
        ResponsesCoordinator.ValidateCitations(answer, 1);
    }
}
