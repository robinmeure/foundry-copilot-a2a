using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class CopilotStudioConsentBypassCommandsTests
{
    private static readonly Guid EnvironmentId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BotId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task ReadsThePerAgentSettingWithAnAdministratorToken()
    {
        HttpRequestMessage? captured = null;
        using var client = CreateClient(request =>
        {
            captured = Clone(request);
            return Json("""{"adminConsentBypass":true}""");
        });

        var enabled = await CopilotStudioConsentBypassCommands.ReadSettingAsync(
            client, "admin-token", EnvironmentId, BotId, CancellationToken.None);

        Assert.True(enabled);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            "https://api.powerplatform.com/copilotstudio/environments/" +
            "11111111-1111-1111-1111-111111111111/bots/" +
            "22222222-2222-2222-2222-222222222222/api/connectorConsentBypass" +
            "?api-version=2022-03-01-preview",
            captured.RequestUri!.AbsoluteUri);
        Assert.Equal(
            new AuthenticationHeaderValue("Bearer", "admin-token"),
            captured.Headers.Authorization);
        Assert.Contains(
            captured.Headers.Accept,
            value => value.MediaType == "application/json");
    }

    [Fact]
    public async Task WritesAndConfirmsThePerAgentSetting()
    {
        HttpRequestMessage? captured = null;
        using var client = CreateClient(request =>
        {
            captured = Clone(request);
            return Json("""{"adminConsentBypass":true}""");
        });

        var enabled = await CopilotStudioConsentBypassCommands.WriteSettingAsync(
            client, "admin-token", EnvironmentId, BotId, true, CancellationToken.None);

        Assert.True(enabled);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured.Method);
        using var body = JsonDocument.Parse(
            await captured.Content!.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("adminConsentBypass").GetBoolean());
    }

    [Fact]
    public async Task RejectsAResponseThatDoesNotConfirmTheRequestedSetting()
    {
        using var client = CreateClient(_ =>
            Json("""{"adminConsentBypass":false}"""));

        var exception = await Assert.ThrowsAsync<CliException>(
            () => CopilotStudioConsentBypassCommands.WriteSettingAsync(
                client, "admin-token", EnvironmentId, BotId, true, CancellationToken.None));

        Assert.Contains("after true was requested", exception.Message);
    }

    [Fact]
    public async Task ForbiddenResponseExplainsTheRequiredAdministratorBoundary()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden")
        });

        var exception = await Assert.ThrowsAsync<CliException>(
            () => CopilotStudioConsentBypassCommands.ReadSettingAsync(
                client, "admin-token", EnvironmentId, BotId, CancellationToken.None));

        Assert.Contains("Power Platform Administrator", exception.Message);
        Assert.Contains("CopilotStudio.AdminActions.Invoke", exception.Message);
    }

    [Fact]
    public void TargetRequiresTheDocumentedGuids()
    {
        var arguments = CommandArguments.Parse(
        [
            "--tenant-id", "not-a-guid",
            "--admin-client-id", Guid.NewGuid().ToString(),
            "--environment-id", EnvironmentId.ToString(),
            "--bot-id", BotId.ToString()
        ]);

        var exception = Assert.Throws<CliException>(
            () => CopilotStudioConsentBypassCommands.ParseTarget(arguments));

        Assert.Contains("--tenant-id", exception.Message);
    }

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                Encoding.UTF8,
                request.Content.Headers.ContentType?.MediaType ?? "text/plain");
        }

        return clone;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
