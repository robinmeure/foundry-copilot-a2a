using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Files;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Services.Graph;
using FoundryCopilotA2A.KnowledgeApi.Services.OfficeDocuments;
using FoundryCopilotA2A.KnowledgeApi.Services.Skills;
using FoundryCopilotA2A.KnowledgeApi.Stores;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ApiException = FoundryCopilotA2A.KnowledgeApi.Middleware.ApiException;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class OfficeDocumentsTests
{
    private const string WordJson = """
        {
          "fileName": "Q3 / review: draft?",
          "title": "Quarterly review",
          "subtitle": "Prepared for the leadership team",
          "sections": [
            {
              "heading": "Summary",
              "paragraphs": ["Revenue grew **12%** year over year.", "Costs were flat."],
              "bullets": ["Growth in **cloud**", "Stable support load"],
              "table": { "columns": [], "rows": [] }
            },
            {
              "heading": "Numbers",
              "paragraphs": [],
              "bullets": [],
              "table": { "columns": ["Metric", "Value"], "rows": [["Revenue", "120"], ["Cost", "80"]] }
            }
          ]
        }
        """;

    private const string PresentationJson = """
        {
          "fileName": "Launch deck",
          "title": "Product launch",
          "subtitle": "October plan",
          "slides": [
            { "title": "Goals", "bullets": ["Reach **10k** users", "Ship on time"], "notes": "Mention the beta results." },
            { "title": "Timeline", "bullets": ["Beta in week 1", "GA in week 4", "Retro in week 6"], "notes": "" }
          ]
        }
        """;

    private sealed class Api : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "document-tests-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            TestConfiguration.ConfigureMock(builder, directory);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IEnumerable<ValidationErrorInfo> Validate(OpenXmlPackage package) =>
        new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2016).Validate(package);

    [Fact]
    public void WordDocumentIsValidOpenXmlWithTheOutlineContent()
    {
        var document = OfficeDocumentRenderer.RenderWord(OfficeDocumentOutline.ParseWord(Parse(WordJson)));

        Assert.Equal("Q3 review draft.docx", document.Name);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", document.MediaType);
        using var stream = new MemoryStream(document.Content);
        using var word = WordprocessingDocument.Open(stream, false);
        Assert.Empty(Validate(word));
        var text = word.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains("Quarterly review", text);
        Assert.Contains("Revenue grew 12% year over year.", text);
        Assert.DoesNotContain("**", text);
        Assert.Single(word.MainDocumentPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>());
        Assert.Equal("Quarterly review", word.PackageProperties.Title);
    }

    [Fact]
    public void PresentationIsValidOpenXmlWithATitleSlideAndNotes()
    {
        var document = OfficeDocumentRenderer.RenderPresentation(OfficeDocumentOutline.ParsePresentation(Parse(PresentationJson)));

        Assert.Equal("Launch deck.pptx", document.Name);
        using var stream = new MemoryStream(document.Content);
        using var deck = PresentationDocument.Open(stream, false);
        Assert.Empty(Validate(deck));
        var slides = deck.PresentationPart!.SlideParts.ToArray();
        Assert.Equal(3, slides.Length);
        var texts = slides.Select(slide => slide.Slide!.InnerText).ToArray();
        Assert.Contains(texts, text => text.Contains("Product launch"));
        Assert.Contains(texts, text => text.Contains("Reach 10k users") && !text.Contains("**"));
        Assert.Single(slides, slide => slide.NotesSlidePart is not null);
    }

    [Theory]
    [InlineData("""{"fileName":"a","title":"","subtitle":"","sections":[]}""")]
    [InlineData("""{"fileName":"a","title":"t","subtitle":"","sections":[],"extra":1}""")]
    [InlineData("""{"fileName":"a","title":"t","subtitle":""}""")]
    [InlineData("""[]""")]
    public void InvalidWordOutlinesAreRejected(string json)
    {
        var failure = Assert.Throws<ApiException>(() => OfficeDocumentOutline.ParseWord(Parse(json)));
        Assert.Equal("invalid_tool_arguments", failure.Code);
    }

    [Fact]
    public void PresentationsRejectTooManySlides()
    {
        var slides = Enumerable.Range(0, OfficeDocumentOutline.MaxSlides + 1)
            .Select(index => new { title = $"Slide {index}", bullets = new[] { "Point" }, notes = "" });
        var json = JsonSerializer.SerializeToElement(new { fileName = "a", title = "t", subtitle = "", slides });

        var failure = Assert.Throws<ApiException>(() => OfficeDocumentOutline.ParsePresentation(json));
        Assert.Equal("invalid_tool_arguments", failure.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOutlinesNeverReportFileCreationProgress(bool slides)
    {
        var scope = new SkillScope(["documents"], null);
        var tool = new OfficeDocumentsConnector().GetTools(TestSkills.Get("office-documents"), scope)
            .Single(candidate => candidate.Name == (slides
                ? OfficeDocumentOutline.PresentationToolName : OfficeDocumentOutline.WordToolName));
        var updates = new List<string>();
        var call = new SkillToolCall(new DefaultHttpContext(), scope, UserContext.Utc(DateTimeOffset.UtcNow),
            Parse("{}"), (message, _) => { updates.Add(message); return ValueTask.CompletedTask; });

        var failure = await Assert.ThrowsAsync<ApiException>(() => tool.InvokeAsync(call, default));
        Assert.Equal("invalid_tool_arguments", failure.Code);
        Assert.Empty(updates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingAtTheRenderingStageDoesNotCreateAFile(bool slides)
    {
        var scope = new SkillScope(["documents"], null);
        var tool = new OfficeDocumentsConnector().GetTools(TestSkills.Get("office-documents"), scope)
            .Single(candidate => candidate.Name == (slides
                ? OfficeDocumentOutline.PresentationToolName : OfficeDocumentOutline.WordToolName));
        using var cancellation = new CancellationTokenSource();
        var updates = new List<string>();
        var call = new SkillToolCall(new DefaultHttpContext(), scope, UserContext.Utc(DateTimeOffset.UtcNow),
            Parse(slides ? PresentationJson : WordJson), (message, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                updates.Add(message);
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => tool.InvokeAsync(call, cancellation.Token));
        Assert.StartsWith("Creating", Assert.Single(updates));
    }

    [Fact]
    public void GeneratedFilesAreScopedAndExpire()
    {
        var clock = new ManualClock();
        var store = new GeneratedFileStore(clock);
        var file = Assert.Single(store.Add("scope-a", [new GeneratedDocument("a.docx", "application/octet-stream", [1, 2, 3])]));

        Assert.True(GeneratedFileStore.IsValidId(file.Id));
        Assert.Equal(3, file.Size);
        Assert.Null(store.Get(file.Id, "scope-b"));
        Assert.NotNull(store.Get(file.Id, "scope-a"));
        Assert.NotNull(store.Get(file.Id, "scope-a"));

        clock.Now += GeneratedFileStore.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(store.Get(file.Id, "scope-a"));
    }

    [Fact]
    public void LocalOriginalsAreScopedSingleUseAndExpire()
    {
        var clock = new ManualClock();
        var store = new GeneratedFileStore(clock);
        var original = new GeneratedDocument("notes.md", "text/markdown", Encoding.UTF8.GetBytes("Original notes."));
        store.AddUpload("scope-a", "document-1", original);

        Assert.Null(store.UploadExpiresAt("scope-b", "document-1"));
        Assert.Null(store.TakeUpload("scope-b", "document-1"));
        Assert.Equal(clock.Now + GeneratedFileStore.Lifetime, store.UploadExpiresAt("scope-a", "document-1"));
        Assert.Equal(original, store.TakeUpload("scope-a", "document-1"));
        Assert.Null(store.TakeUpload("scope-a", "document-1"));
        Assert.Null(store.UploadExpiresAt("scope-a", "document-1"));

        store.AddUpload("scope-a", "document-2", original);
        clock.Now += GeneratedFileStore.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(store.UploadExpiresAt("scope-a", "document-2"));
        Assert.Null(store.TakeUpload("scope-a", "document-2"));
    }

    [Fact]
    public void OneDriveFolderIsSanitized()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Documents:OneDriveFolder"] = @"Reports\..\2026: Q3/" })
            .Build();

        var folder = OfficeDocumentsConnector.Folder(configuration);

        Assert.DoesNotContain("..", folder);
        Assert.DoesNotContain(":", folder);
        Assert.StartsWith("Reports/", folder);
        Assert.Equal(OfficeDocumentsConnector.DefaultFolder, OfficeDocumentsConnector.Folder(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task SaveUploadsToTheUsersDriveWithoutOverwriting()
    {
        var requests = new List<HttpRequestMessage>();
        var configuration = TestConfiguration.Create("Azure");
        var actions = new GraphActions(new GraphService(new Factory(new Handler(request =>
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get
                    ? """{"id":"drive-1"}"""
                    : """{"id":"item","name":"Launch deck 1.pptx","webUrl":"https://contoso-my.sharepoint.com/personal/x/Launch.pptx"}""",
                    Encoding.UTF8, "application/json")
            });
        })), configuration, (_, _) => Task.FromResult("obo-token")), configuration);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", "tenant"), new Claim("oid", "caller")], "test"))
        };

        var result = await actions.SaveFileAsync(context, "id",
            new GeneratedDocument("Launch deck.pptx", "application/octet-stream", [1, 2, 3]), "Knowledge Chat", default);

        Assert.Equal(2, requests.Count);
        Assert.Equal("/v1.0/me/drive", requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Put, requests[1].Method);
        Assert.Equal("/v1.0/drives/drive-1/root:/Knowledge Chat/Launch deck.pptx:/content", requests[1].RequestUri!.AbsolutePath.Replace("%20", " "));
        Assert.Contains("%40microsoft.graph.conflictBehavior=rename", requests[1].RequestUri!.Query.Replace("@", "%40"));
        Assert.Equal("completed", result.Status);
        Assert.Contains("Launch deck 1.pptx", result.Message);
        Assert.Equal("https://contoso-my.sharepoint.com/personal/x/Launch.pptx", result.WebLink);
    }

    [Fact]
    public async Task MockChatCreatesADownloadableDeckAndStagesASave()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();

        var chat = await client.PostAsJsonAsync("/api/chat", new
        {
            conversationId = conversation,
            text = "Create a presentation about the launch",
            sources = new[] { "documents" }
        });
        chat.EnsureSuccessStatusCode();
        var body = await chat.Content.ReadFromJsonAsync<JsonElement>();
        var file = Assert.Single(body.GetProperty("files").EnumerateArray());
        Assert.EndsWith(".pptx", file.GetProperty("name").GetString());
        var action = Assert.Single(body.GetProperty("actions").EnumerateArray());
        Assert.Equal(OfficeDocumentsConnector.SaveKind, action.GetProperty("kind").GetString());

        var fileId = file.GetProperty("id").GetString();
        var other = await client.GetAsync($"/api/conversations/{Guid.NewGuid()}/files/{fileId}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        var download = await client.GetAsync($"/api/conversations/{conversation}/files/{fileId}");
        download.EnsureSuccessStatusCode();
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", download.Content.Headers.ContentType!.MediaType);
        using (var deck = PresentationDocument.Open(new MemoryStream(await download.Content.ReadAsByteArrayAsync()), false))
        {
            Assert.Empty(Validate(deck));
        }

        var confirmed = await client.PostAsJsonAsync($"/api/actions/{action.GetProperty("id").GetString()}/confirm", new { conversationId = conversation });
        confirmed.EnsureSuccessStatusCode();
        var result = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Contains("No Microsoft Graph call was made", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task LocalUploadsOfferAnExplicitOneDriveUploadBesideTheDocument()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();
        var bytes = Encoding.UTF8.GetBytes("Launch notes: the review is on Friday.");
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(bytes), "file", "Launch #notes.md");

        var response = await client.PostAsync($"/api/conversations/{conversation}/documents", upload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Launch #notes.md", body.GetProperty("name").GetString());
        Assert.Equal("Ready", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("actions", out _));
        Assert.True(body.GetProperty("oneDriveUploadExpiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        var documentId = body.GetProperty("id").GetString();
        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation}/documents");
        Assert.Equal(body.GetProperty("oneDriveUploadExpiresAt").GetString(),
            Assert.Single(listed.EnumerateArray()).GetProperty("oneDriveUploadExpiresAt").GetString());

        var elsewhere = await client.PostAsync($"/api/conversations/{Guid.NewGuid()}/documents/{documentId}/onedrive", null);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);

        var uploaded = await client.PostAsync($"/api/conversations/{conversation}/documents/{documentId}/onedrive", null);
        uploaded.EnsureSuccessStatusCode();
        var result = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Contains("Mock: would save \"Launch notes.md\"", result.GetProperty("message").GetString());
        var repeated = await client.PostAsync($"/api/conversations/{conversation}/documents/{documentId}/onedrive", null);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation}/documents");
        Assert.False(Assert.Single(after.EnumerateArray()).TryGetProperty("oneDriveUploadExpiresAt", out _));
    }

    [Fact]
    public async Task RemovedDocumentsCannotBeUploadedToOneDrive()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Notes.")), "file", "notes.txt");
        var response = await client.PostAsync($"/api/conversations/{conversation}/documents", upload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var documentId = body.GetProperty("id").GetString();
        (await client.DeleteAsync($"/api/conversations/{conversation}/documents/{documentId}")).EnsureSuccessStatusCode();

        var save = await client.PostAsync($"/api/conversations/{conversation}/documents/{documentId}/onedrive", null);
        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
    }

    [Fact]
    public async Task SharePointImportsDoNotOfferAOneDriveUpload()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();
        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation}/sharepoint",
            new { driveId = "mock-drive", itemId = "mock-file" });
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation}/documents");
        Assert.False(Assert.Single(listed.EnumerateArray()).TryGetProperty("oneDriveUploadExpiresAt", out _));

        var upload = await client.PostAsync(
            $"/api/conversations/{conversation}/documents/{document.GetProperty("id").GetString()}/onedrive", null);
        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
    }

    [Fact]
    public async Task MockChatWithoutTheDocumentsSourceCreatesNoFile()
    {
        using var api = new Api();
        using var client = api.CreateClient();

        var body = await (await client.PostAsJsonAsync("/api/chat", new
        {
            conversationId = Guid.NewGuid().ToString(),
            text = "Write a report about the launch",
            sources = new[] { "mail" }
        })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("files").EnumerateArray());
    }
}
