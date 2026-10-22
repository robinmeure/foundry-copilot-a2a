using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace FoundryCopilotA2A.Adapter.Tests;

public class FileInputTests : IClassFixture<A2AAdapterFactory>
{
    private readonly HttpClient _client;
    public FileInputTests(A2AAdapterFactory factory) => _client = factory.CreateClient();

    private static object Raw(string bytes = "aGVsbG8=", string name = "notes.txt",
        string mediaType = "text/plain") => new { raw = bytes, filename = name, mediaType };

    private static JsonElement Message(params object[] parts) =>
        JsonSerializer.SerializeToElement(new { parts });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothProtocolVersionsDeliverFileBytesToMock(bool legacy)
    {
        object file = legacy
            ? new { kind = "file", file = new { bytes = "aGVsbG8=", name = "notes.txt", mimeType = "text/plain" } }
            : Raw();
        using var response = await Send(Guid.NewGuid().ToString(), legacy, file);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        Assert.Contains("mock received notes.txt (5 bytes)", body);
        Assert.Contains("mock mode does not analyze", body);
        Assert.DoesNotContain("aGVsbG8=", body);
    }

    [Fact]
    public async Task FileOnlyRequestGetsAnExplicitSummaryPrompt()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id = "file-only", method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER", messageId = Guid.NewGuid().ToString(),
                        contextId = Guid.NewGuid().ToString(), parts = new[] { Raw() }
                    }
                }
            })
        };
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Please summarize the attached file.", body);
        Assert.Contains("mock received notes.txt", body);
    }

    [Fact]
    public async Task ReplayingChangedFileBytesConflictsEvenWithTheSameText()
    {
        var id = Guid.NewGuid().ToString();
        using var first = await Send(id, false, Raw());
        using var replay = await Send(id, false, Raw("d29ybGQ="));
        Assert.Contains("mock received", await first.Content.ReadAsStringAsync());
        Assert.Contains("different content", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public void HashCoversBytesNamesAndTypesButNormalizesProtocolShape()
    {
        var original = PayloadHash.Compute(Message(new { text = "prompt" }, Raw()));
        Assert.NotEqual(original, PayloadHash.Compute(Message(new { text = "prompt" }, Raw("d29ybGQ="))));
        Assert.NotEqual(original, PayloadHash.Compute(Message(new { text = "prompt" }, Raw(name: "other.txt"))));
        Assert.NotEqual(original, PayloadHash.Compute(Message(new { text = "prompt" }, Raw(name: "notes.csv", mediaType: "text/csv"))));
        Assert.Equal(original, PayloadHash.Compute(Message(new { text = "prompt" },
            new { kind = "file", file = new { bytes = "aGVsbG8=", name = "notes.txt", mimeType = "text/plain" } })));
    }

    [Theory]
    [InlineData("bad", "notes.txt", "text/plain")]
    [InlineData("", "notes.txt", "text/plain")]
    [InlineData("aGVsbG8=", "../notes.txt", "text/plain")]
    [InlineData("aGVsbG8=", "notes.exe", "text/plain")]
    [InlineData("aGVsbG8=", "notes.txt", "application/pdf")]
    public void InvalidFilesFailExplicitly(string bytes, string name, string mediaType)
    {
        Assert.Throws<AdapterRequestException>(() =>
            A2AInputFiles.Read(Message(Raw(bytes, name, mediaType))));
    }

    [Fact]
    public void SizeLimitIsMeasuredOnDecodedBytes()
    {
        var bytes = new byte[A2AInputFiles.MaximumBytes];
        Assert.Equal(bytes.Length, Assert.Single(A2AInputFiles.Read(
            Message(Raw(Convert.ToBase64String(bytes))))).Bytes.Length);
        Assert.Throws<AdapterRequestException>(() => A2AInputFiles.Read(Message(
            Raw(Convert.ToBase64String(new byte[A2AInputFiles.MaximumBytes + 1])))));
    }

    [Fact]
    public async Task InvalidFileReturnsProtocolErrorRatherThanCallingBackend()
    {
        using var response = await Send(Guid.NewGuid().ToString(), false, Raw("invalid-base64!"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("base64", body);
        Assert.DoesNotContain("mock-copilot-studio", body);
    }

    [Fact]
    public async Task UnsupportedProviderDoesNotSilentlyDropFiles()
    {
        using var factory = new A2AAdapterFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Foundry:Agents:test-foundry:DisplayName", "Test Foundry");
            builder.UseSetting("Foundry:Agents:test-foundry:Endpoint",
                "https://example.test/endpoint/protocols/a2a");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id = "unsupported-file", method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER", messageId = Guid.NewGuid().ToString(),
                        contextId = Guid.NewGuid().ToString(),
                        parts = new[] { (object)new { text = "Read this" }, Raw() }
                    }
                }
            })
        };
        request.Headers.Add("A2A-Version", "1.0");
        request.Headers.Add(AdapterConstants.AgentHeaderName, "test-foundry");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.Contains("only for Copilot Studio", StringComparison.Ordinal), body);
    }

    [Fact]
    public void RemoteFilesMultipleFilesAndAmbiguousPartsAreRejected()
    {
        Assert.Throws<AdapterRequestException>(() => A2AInputFiles.Read(Message(new { url = "https://private.example/file" })));
        Assert.Throws<AdapterRequestException>(() => A2AInputFiles.Read(Message(new { file = new { uri = "https://private.example/file" } })));
        Assert.Throws<AdapterRequestException>(() => A2AInputFiles.Read(Message(Raw(), Raw())));
        Assert.Throws<AdapterRequestException>(() => A2AInputFiles.Read(Message(new
        {
            raw = "aGVsbG8=", text = "ambiguous", filename = "notes.txt", mediaType = "text/plain"
        })));
    }

    [Fact]
    public void MessageFactoryPreservesFilesOnEverySend()
    {
        var files = A2AInputFiles.Read(Message(Raw()));
        foreach (var activity in new[]
        {
            A2AInputFiles.CreateMessage("prompt", files),
            A2AInputFiles.CreateMessage("prompt", files)
        })
        {
            var file = Assert.Single(activity.Attachments);
            Assert.Equal("notes.txt", file.Name);
            Assert.Equal("text/plain", file.ContentType);
            Assert.Equal("data:text/plain;base64,aGVsbG8=", file.ContentUrl);
        }
        Assert.DoesNotContain("aGVsbG8=", files[0].ToString());
    }

    [Fact]
    public void FramedPromptTiesTheQuestionToTheAttachedFile()
    {
        var files = A2AInputFiles.Read(Message(Raw()));
        var framed = A2AInputFiles.FramePrompt("  What are the action items?  ", files);

        Assert.Equal(
            "What are the action items?\n\n(This is about the attached text document \"notes.txt\".)",
            framed);
        Assert.DoesNotContain("aGVsbG8=", framed);
        Assert.StartsWith("Please summarize the attached file.\n\n",
            A2AInputFiles.FramePrompt(" ", files), StringComparison.Ordinal);
        Assert.Equal("plain", A2AInputFiles.FramePrompt("plain", []));
    }

    [Fact]
    public void FramedPromptStaysNextToTheQuestionWhenHistoryIsReplayed()
    {
        var files = A2AInputFiles.Read(Message(Raw()));
        var replayed = ConversationTranscript.Prepend(
            A2AInputFiles.FramePrompt("And the deadline?", files),
            [new A2AConversationTurn(A2AConversationTurn.UserRole, "Hi")]);

        Assert.StartsWith("Conversation so far:", replayed, StringComparison.Ordinal);
        Assert.Contains("---\nAnd the deadline?\n\n(This is about the attached", replayed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TracesRedactAllAttachmentRepresentations()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test")
        {
            Content = new StringContent(
                """{"attachments":[{"name":"private.txt","content":"hidden-file","contentUrl":"data:hidden"}],"parts":[{"raw":"raw-secret"},{"file":{"bytes":"byte-secret"}}]}""",
                Encoding.UTF8, "application/json")
        };
        var sanitized = await TraceSanitizer.ReadSanitizedRequestBodyAsync(request, CancellationToken.None);
        Assert.DoesNotContain("private.txt", sanitized);
        Assert.DoesNotContain("hidden-file", sanitized);
        Assert.DoesNotContain("raw-secret", sanitized);
        Assert.DoesNotContain("byte-secret", sanitized);
    }

    private async Task<HttpResponseMessage> Send(string id, bool legacy, object file)
    {
        var text = legacy ? (object)new { kind = "text", text = "Summarize this file" }
            : new { text = "Summarize this file" };
        using var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id, method = legacy ? "message/send" : "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = legacy ? "user" : "ROLE_USER",
                        kind = "message",
                        messageId = id, contextId = "files-" + id, parts = new[] { text, file }
                    }
                }
            })
        };
        if (!legacy) request.Headers.Add("A2A-Version", "1.0");
        return await _client.SendAsync(request);
    }
}
