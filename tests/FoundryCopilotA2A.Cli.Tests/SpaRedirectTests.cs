using System.Net;
using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class SpaRedirectTests
{
    private const string ClientId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("http://localhost:5174/", "http://localhost:5174")]
    [InlineData("http://127.0.0.1:5174", "http://127.0.0.1:5174")]
    [InlineData("https://chat.example/signin", "https://chat.example/signin")]
    public void ValidatesSpaRedirects(string value, string expected) =>
        Assert.Equal(expected, FoundryAccessCommands.ValidateSpaRedirect(value));

    [Theory]
    [InlineData("http://chat.example")]
    [InlineData("https://user@chat.example")]
    [InlineData("https://chat.example?token=secret")]
    [InlineData("https://chat.example#fragment")]
    [InlineData("/relative")]
    public void RejectsUnsafeRedirects(string value) =>
        Assert.Throws<CliException>(() => FoundryAccessCommands.ValidateSpaRedirect(value));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesExistingRedirectsAndUpdatesOnlySpa(bool alreadyExists)
    {
        using var handler = new RedirectHandler(alreadyExists, verifyUpdate: true);
        using var client = new HttpClient(handler);
        await FoundryAccessCommands.AddSpaRedirectAsync(
            client, "test-token", ClientId, "http://localhost:5174", CancellationToken.None);

        Assert.Equal(alreadyExists ? 0 : 1, handler.Patches);
        if (!alreadyExists)
        {
            using var patch = JsonDocument.Parse(handler.PatchBody!);
            Assert.Equal("spa", Assert.Single(patch.RootElement.EnumerateObject()).Name);
            Assert.Equal(
                new[] { "https://existing.example", "http://localhost:5174" },
                patch.RootElement.GetProperty("spa").GetProperty("redirectUris")
                    .EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(2, handler.Gets);
        }
    }

    [Fact]
    public async Task MissingPersistedRedirectIsAnExplicitFailure()
    {
        using var handler = new RedirectHandler(false, verifyUpdate: false);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<CliException>(() =>
            FoundryAccessCommands.AddSpaRedirectAsync(
                client, "test-token", ClientId, "http://localhost:5174", CancellationToken.None));
        Assert.Contains("not all present", error.Message);
    }

    private sealed class RedirectHandler(bool alreadyExists, bool verifyUpdate) : HttpMessageHandler
    {
        public int Patches { get; private set; }
        public int Gets { get; private set; }
        public string? PatchBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Patch)
            {
                Patches++;
                PatchBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            Gets++;
            var spa = new
            {
                redirectUris = alreadyExists || (Gets > 1 && verifyUpdate)
                    ? new[] { "https://existing.example", "http://localhost:5174" }
                    : ["https://existing.example"]
            };
            var body = Gets == 1
                ? JsonSerializer.Serialize(new { value = new[] { new { id = "object-id", spa } } })
                : JsonSerializer.Serialize(new { spa });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
