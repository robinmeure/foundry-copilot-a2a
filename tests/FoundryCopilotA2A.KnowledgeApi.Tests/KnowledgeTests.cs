using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using FoundryCopilotA2A.KnowledgeApi.Services.Documents;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class KnowledgeTests
{
    private sealed class Api : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "knowledge-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task UploadChatIsolateDelete()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("The lunar project password-free fact is Lavender-27.")), "file", "brief.txt");
        var response = await client.PostAsync($"/api/conversations/{conversation}/documents", upload);
        response.EnsureSuccessStatusCode();
        var document = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var answer = await Chat(client, conversation, "What is the lunar project fact?");
        Assert.Contains("Lavender-27", answer.GetProperty("answer").GetString());
        Assert.Equal("1", answer.GetProperty("citations").GetProperty("schemaVersion").GetString());
        Assert.Single(answer.GetProperty("citations").GetProperty("sources").EnumerateArray());
        var other = Guid.NewGuid().ToString();
        Assert.DoesNotContain("Lavender-27", (await Chat(client, other, "lunar")).GetProperty("answer").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/conversations/{other}/documents/{document}")).StatusCode);
        (await client.DeleteAsync($"/api/conversations/{conversation}/documents/{document}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain("Lavender-27", (await Chat(client, conversation, "lunar")).GetProperty("answer").GetString());
    }

    [Fact]
    public async Task PickerImportAndLiveSourcesHaveMockVerticalSlice()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();
        foreach (var kind in new[] { "sites", "drives", "items" })
        {
            var browse = await client.GetFromJsonAsync<JsonElement>($"/api/graph/{kind}");
            Assert.Single(browse.GetProperty("items").EnumerateArray());
        }
        (await client.PostAsJsonAsync($"/api/conversations/{conversation}/sharepoint", new { driveId = "mock-drive", itemId = "mock-file" }))
            .EnsureSuccessStatusCode();
        var result = await Chat(client, conversation, "budget", ["mail", "calendar", "teams", "people"]);
        Assert.Equal(5, result.GetProperty("citations").GetProperty("sources").GetArrayLength());
        Assert.Contains("42000", result.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task TaskSourcesWorkWithoutAzureAndStayOptIn()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var conversation = Guid.NewGuid().ToString();
        var disabled = await Chat(client, conversation, "tasks");
        Assert.Empty(disabled.GetProperty("citations").GetProperty("sources").EnumerateArray());
        var enabled = await Chat(client, conversation, "tasks", ["todo", "planner"]);
        var sources = enabled.GetProperty("citations").GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(3, sources.Length);
        Assert.Contains(sources, source => source.GetProperty("title").GetString() == "Prepare launch notes");
        Assert.Contains(sources, source => source.GetProperty("title").GetString() == "Review release gates");
        var documents = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation}/documents");
        Assert.Empty(documents.EnumerateArray());
    }

    [Fact]
    public async Task RejectsInvalidConversationFilesAndSources()
    {
        using var api = new Api();
        using var client = api.CreateClient();
        var invalid = await client.PostAsJsonAsync("/api/chat", new { conversationId = "../x", text = "question" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_conversation", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        var badSource = await client.PostAsJsonAsync("/api/chat",
            new { conversationId = Guid.NewGuid().ToString(), text = "question", sources = new[] { "everything" } });
        Assert.Equal(HttpStatusCode.BadRequest, badSource.StatusCode);
        using var file = new MultipartFormDataContent();
        file.Add(new ByteArrayContent("not a pdf"u8.ToArray()), "file", "fake.pdf");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"/api/conversations/{Guid.NewGuid()}/documents", file)).StatusCode);
    }

    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/chat/stream")]
    [InlineData("/api/conversations/11111111-1111-1111-1111-111111111111/sharepoint")]
    [InlineData("/api/actions/invalid/confirm")]
    [InlineData("/api/actions/invalid/cancel")]
    public async Task JsonEndpointsRejectMalformedNullAndFailedBinding(string path)
    {
        using var api = new Api();
        using var client = api.CreateClient();
        foreach (var body in new[] { "{broken", "null", "[]", """{"conversationId":42,"threadId":42,"driveId":42}""" })
        {
            using var response = await client.PostAsync(path,
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_request", problem.GetProperty("title").GetString());
        }
    }

    [Fact]
    public void ScopeIsBoundToTenantUserAndConversation()
    {
        var conversation = Guid.NewGuid().ToString();
        var a = new Caller("tenant", "alice").Scope(conversation);
        Assert.NotEqual(a, new Caller("tenant", "bob").Scope(conversation));
        Assert.NotEqual(a, new Caller("other", "alice").Scope(conversation));
        Assert.NotEqual(a, new Caller("tenant", "alice").Scope(Guid.NewGuid().ToString()));
        Assert.Throws<ApiException>(() => Caller.From(new ClaimsPrincipal(), false));
    }

    [Fact]
    public void ContinuationsAreCallerAndQueryBound()
    {
        var tokens = new ContinuationTokens(new EphemeralDataProtectionProvider());
        var caller = new Caller("t", "a");
        var token = tokens.Write(caller, "sites", "sites?$skiptoken=opaque");
        Assert.Equal("sites?$skiptoken=opaque", tokens.Read(token, caller, "sites"));
        Assert.Throws<ApiException>(() => tokens.Read(token, new("t", "b"), "sites"));
        Assert.Throws<ApiException>(() => tokens.Read(token, caller, "drives"));
        Assert.Throws<ApiException>(() => tokens.Read("https://evil.test", caller, "sites"));
    }

    [Fact]
    public void ChunksKeepPageLocatorsAndConservativeUtf8Bounds()
    {
        var text = string.Concat(Enumerable.Repeat("A multilingual paragraph. 漢字 😀\n", 500));
        var chunks = Chunker.Split("document", "report.txt", [new(3, text)]);
        Assert.True(chunks.Length > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(chunk.Content), 1, 2400);
            Assert.StartsWith("Page 3", chunk.Locator);
        });
        Assert.Equal(text.Trim(), string.Concat(chunks.Select(c => c.Content)));
        Assert.Equal(chunks, Chunker.Split("document", "report.txt", [new(3, text)]));
        Assert.Throws<ApiException>(() => Chunker.Split("empty", "empty.txt", [new(1, "  ")]));
    }

    [Fact]
    public async Task ByteLimitAndEncodingFailExplicitly()
    {
        await Assert.ThrowsAsync<ApiException>(() => Files.ReadLimited(new MemoryStream(new byte[11]), 10, default));
        Assert.Throws<ApiException>(() => Files.Text([0xff]));
        Assert.Throws<ApiException>(() => Files.Validate("../file.txt", [1]));
    }

    [Fact]
    public async Task AzureModeRequiresApiAudienceAndDelegatedScope()
    {
        const string tenant = "11111111-1111-1111-1111-111111111111";
        const string audience = "22222222-2222-2222-2222-222222222222";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-signing-key-at-least-thirty-two-bytes-long"));
        var issuer = $"https://login.microsoftonline.com/{tenant}/v2.0";
        using var api = new Api();
        using var live = api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:Backend"] = "Azure",
                ["Authentication:TenantId"] = tenant,
                ["Authentication:ClientId"] = audience,
                ["Authentication:ClientSecret"] = "unit-test-only",
                ["OpenAI:Endpoint"] = "https://example.openai.azure.com/",
                ["Search:Endpoint"] = "https://example.search.windows.net/",
                ["DocumentIntelligence:Endpoint"] = "https://example.cognitiveservices.azure.com/"
            }));
            builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>("Bearer", jwt =>
            {
                jwt.Configuration = new OpenIdConnectConfiguration { Issuer = issuer };
                jwt.Configuration.SigningKeys.Add(key);
                jwt.TokenValidationParameters.ValidIssuer = issuer;
                jwt.TokenValidationParameters.IssuerSigningKey = key;
            }));
        });
        using var client = live.CreateClient();
        var caps = await client.GetFromJsonAsync<JsonElement>("/api/capabilities");
        Assert.Equal("Azure", caps.GetProperty("backend").GetString());
        var path = $"/api/conversations/{Guid.NewGuid()}/documents";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        string Token(string target, bool delegated) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer, target, [new("tid", tenant), new("oid", "alice"), new(delegated ? "scp" : "roles", "access_as_user")],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(key, SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("https://graph.microsoft.com", true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(audience, false));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(audience, true));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    private static async Task<JsonElement> Chat(HttpClient client, string conversation, string text, string[]? sources = null)
    {
        var response = await client.PostAsJsonAsync("/api/chat", new { conversationId = conversation, text, sources });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
