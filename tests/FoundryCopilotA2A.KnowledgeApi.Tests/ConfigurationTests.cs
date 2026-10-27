using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryCopilotA2A.KnowledgeApi.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Client;

namespace FoundryCopilotA2A.KnowledgeApi.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("Knowledge:Backend", "Unknown", "Knowledge:Backend")]
    [InlineData("Knowledge:MaxFileBytes", "0", "Upload bytes")]
    [InlineData("Knowledge:MaxFileBytes", "20971521", "Upload bytes")]
    [InlineData("Knowledge:RetentionDays", "0", "retention days")]
    [InlineData("Knowledge:RetentionDays", "31", "retention days")]
    public void StartupRetainsBackendAndResourceLimits(string key, string value, string error)
    {
        using var api = CreateApi(new Dictionary<string, string?> { [key] = value });
        var exception = Assert.Throws<InvalidOperationException>(() => api.CreateClient());
        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public void AnonymousMockModeRemainsDevelopmentOnly()
    {
        using var api = CreateApi([], "Production");
        var exception = Assert.Throws<InvalidOperationException>(() => api.CreateClient());
        Assert.Contains("Anonymous mock mode is restricted to Development", exception.Message);
    }

    [Theory]
    [InlineData("A2A:AuthenticationMode", "GuessFromUnvalidatedToken", "A2A:AuthenticationMode")]
    [InlineData("A2A:DelegatedScope", "", "A2A:DelegatedScope")]
    [InlineData("A2A:AllowedAgentIds:0", "", "A2A:AllowedAgentIds")]
    [InlineData("A2A:AllowedAgentIds:1", "allowed", "A2A:AllowedAgentIds")]
    public void StartupRetainsA2aAuthorizationRules(string key, string value, string error)
    {
        var values = A2aConfiguration();
        values[key] = value;
        using var api = CreateApi(values);
        var exception = Assert.Throws<InvalidOperationException>(() => api.CreateClient());
        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public void SharedAudienceRejectsAnIgnoredOboScope()
    {
        var values = A2aConfiguration();
        values["A2A:AuthenticationMode"] = "SameAudience";
        using var api = CreateApi(values);
        var exception = Assert.Throws<InvalidOperationException>(() => api.CreateClient());
        Assert.Contains("A2A:DelegatedScope must be empty", exception.Message);
    }

    [Theory]
    [InlineData("OnBehalfOf")]
    [InlineData("SameAudience")]
    public async Task ServicesUseConfigurationWithoutCustomDestinationFormatChecks(string authenticationMode)
    {
        var values = A2aConfiguration();
        values["A2A:AuthenticationMode"] = authenticationMode;
        if (authenticationMode == "SameAudience")
        {
            values["A2A:DelegatedScope"] = "";
        }
        values["A2A:AdapterBaseUrl"] = "http://adapter.internal/custom";
        values["Kamervragen:Enabled"] = "true";
        values["Kamervragen:Endpoint"] = "http://search.internal/custom";
        values["Kamervragen:Index"] = "shared.reference";
        values["Knowledge:MaxFileBytes"] = "1024";
        using var api = CreateApi(values);
        using var client = api.CreateClient();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/capabilities");
        Assert.Equal("Azure", capabilities.GetProperty("backend").GetString());
        Assert.Equal(1024, capabilities.GetProperty("maxFileBytes").GetInt32());
        Assert.True(capabilities.GetProperty("a2a").GetBoolean());
        Assert.True(capabilities.GetProperty("kamervragen").GetBoolean());
    }

    [Fact]
    public async Task ConfiguredUploadLimitIsEnforcedByTheDocumentEndpoint()
    {
        using var api = CreateApi(new Dictionary<string, string?> { ["Knowledge:MaxFileBytes"] = "3" });
        using var client = api.CreateClient();
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent("four"u8.ToArray()), "file", "brief.txt");
        using var response = await client.PostAsync($"/api/conversations/{Guid.NewGuid()}/documents", upload);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static Dictionary<string, string?> A2aConfiguration() => new()
    {
        ["Knowledge:Backend"] = "Azure",
        ["A2A:Enabled"] = "true",
        ["A2A:AuthenticationMode"] = "OnBehalfOf",
        ["A2A:DelegatedScope"] = "api://adapter/access_as_user",
        ["A2A:AllowedAgentIds:0"] = "allowed"
    };

    private static WebApplicationFactory<Program> CreateApi(
        Dictionary<string, string?> values,
        string environment = "Development") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(TestConfiguration.Create(values: values).AsEnumerable()));
            builder.ConfigureServices(services =>
            {
                // Configuration tests must not warm credentials or call external services.
                services.RemoveAll<IHostedService>();
                services.Replace(ServiceDescriptor.Singleton(new DelegatedTokenProvider(
                    ConfidentialClientApplicationBuilder.Create("11111111-1111-1111-1111-111111111111").Build())));
            });
        });
}
