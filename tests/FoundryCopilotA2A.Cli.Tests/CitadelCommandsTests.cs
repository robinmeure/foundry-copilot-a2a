using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class CitadelCommandsTests
{
    internal static CitadelConfiguration Configuration => new(
        "00000000-0000-0000-0000-000000000000",
        "resource-group", "gateway", "copilot-studio-local", "copilot-studio-local",
        "https://adapter.example",
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
        "http://localhost:5137", ["reverser"], false);

    [Fact]
    public void BrowserPolicyAllowsTheFrontendContractWithoutBuffering()
    {
        var policy = XElement.Parse(CitadelCommands.BuildApiPolicy(Configuration));
        var inbound = policy.Element("inbound")!;
        Assert.Equal("cors", inbound.Elements().First().Name.LocalName);
        var cors = inbound.Element("cors")!;
        Assert.Equal("false", cors.Attribute("allow-credentials")!.Value);
        Assert.Equal(Configuration.AllowedOrigin, cors.Descendants("origin").Single().Value);
        Assert.Contains(cors.Descendants("header"), header => header.Value == "X-Copilot-Agent");
        Assert.Contains(cors.Descendants("header"), header => header.Value == "X-A2A-Chain-Target");
        Assert.Contains(cors.Element("expose-headers")!.Elements(), header => header.Value == "X-Trace-Id");
        Assert.Equal("false", policy.Descendants("forward-request").Single().Attribute("buffer-response")!.Value);
        Assert.Empty(policy.Descendants("retry"));
        Assert.Empty(policy.Descendants("validate-content"));
    }

    [Theory]
    [InlineData(true, "60")]
    [InlineData(false, "600")]
    public void DelegatedPolicyPreservesTheUserTokenAndProtectsTraceReads(bool runtime, string calls)
    {
        var policyText = CitadelCommands.BuildDelegatedPolicy(Configuration, runtime);
        var policy = XElement.Parse(policyText);
        var jwt = policy.Descendants("validate-jwt").Single();
        Assert.Equal("Authorization", jwt.Attribute("header-name")!.Value);
        Assert.Contains(jwt.Descendants("audience"),
            audience => audience.Value == $"api://{Configuration.ApiClientId}");
        Assert.Contains(jwt.Descendants("audience"),
            audience => audience.Value == Configuration.ApiClientId);
        Assert.Equal("access_as_user",
            jwt.Descendants("claim").Single(claim => claim.Attribute("name")!.Value == "scp")
                .Element("value")!.Value);
        Assert.Equal(Configuration.TenantId,
            jwt.Descendants("claim").Single(claim => claim.Attribute("name")!.Value == "tid")
                .Element("value")!.Value);
        Assert.Empty(policy.Descendants("authentication-managed-identity"));
        Assert.Empty(policy.Descendants("set-header"));
        Assert.Equal(runtime, policy.Descendants("validate-content").Any());
        Assert.Equal(calls, policy.Descendants("rate-limit-by-key").Single().Attribute("calls")!.Value);
        Assert.NotNull(policy.Element("backend")!.Element("base"));
        Assert.NotNull(policy.Element("outbound")!.Element("base"));
        Assert.DoesNotContain("__", policyText);
        Assert.DoesNotContain("{{", policyText);
    }

    [Fact]
    public void DiscoveryPolicyAllowsPublicGetBeforeTheInheritedSpaCorsPolicy()
    {
        var policy = XElement.Parse(CitadelCommands.BuildDiscoveryPolicy());
        var inbound = policy.Element("inbound")!;
        Assert.Equal("cors", inbound.Elements().First().Name.LocalName);
        var cors = inbound.Element("cors")!;
        Assert.Equal("false", cors.Attribute("allow-credentials")!.Value);
        Assert.Equal("*", cors.Descendants("origin").Single().Value);
        Assert.Equal("GET", cors.Descendants("method").Single().Value);
        Assert.Equal(new[] { "Content-Type", "A2A-Version" },
            cors.Descendants("header").Select(header => header.Value));
        Assert.Empty(policy.Descendants("validate-jwt"));
        Assert.NotNull(inbound.Element("base"));
        Assert.NotNull(policy.Element("backend")!.Element("base"));
        Assert.NotNull(policy.Element("outbound")!.Element("base"));
    }

    [Theory]
    [InlineData("https://example-5099.euw.devtunnels.ms", true)]
    [InlineData("https://adapter.example", false)]
    [InlineData("https://devtunnels.ms.attacker.example", false)]
    public void OnlyDevTunnelBackendsGetTheNoninteractiveTunnelHeader(string backend, bool expected)
    {
        var policy = XElement.Parse(CitadelCommands.BuildApiPolicy(Configuration with { BackendUrl = backend }));
        Assert.Equal(expected, policy.Descendants("set-header").Any(
            header => header.Attribute("name")!.Value == "X-Tunnel-Skip-AntiPhishing-Page"));
    }

    [Fact]
    public void SpecialistApisKeepDiscoveryAndRuntimeBoundToTheSameBackend()
    {
        var apis = CitadelCommands.BuildApis(Configuration);
        Assert.Equal(2, apis.Count);
        var specialist = apis[1];
        Assert.Equal("copilot-studio-local-reverser", specialist.Id);
        Assert.Equal("copilot-studio-local/a2a-agents/reverser", specialist.Path);
        Assert.Equal("https://adapter.example/a2a-agents/reverser", specialist.BackendUrl);
        var operations = CitadelCommands.BuildOperations(specialist: true);
        Assert.Contains(operations, operation => operation.Path == "/.well-known/agent-card.json");
        Assert.Contains(operations, operation => operation.Path == "/a2a/.well-known/agent-card.json");
        Assert.Contains(operations, operation => operation.Path == "/.well-known/agent.json");
        Assert.Contains(operations, operation => operation.Path == "/a2a/.well-known/agent.json");
        Assert.True(operations.Single(operation => operation.Path == "/a2a").RequiresToken);
        Assert.DoesNotContain(operations, operation => operation.Path.Contains("{agentId}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--backend-url", "http://adapter.example")]
    [InlineData("--backend-url", "https://adapter.example?token=secret")]
    [InlineData("--backend-url", "https://user:secret@adapter.example")]
    [InlineData("--allowed-origin", "http://localhost:5137/path")]
    [InlineData("--tenant-id", "not-a-tenant")]
    [InlineData("--api-client-id", "not-an-application")]
    [InlineData("--api-path", "../hosted")]
    [InlineData("--agent-ids", "reverser/other")]
    public void InvalidConfigurationIsRejectedBeforeAzureAccess(string option, string value)
    {
        var options = new Dictionary<string, string>
        {
            ["--resource-group"] = "resource-group",
            ["--service-name"] = "gateway",
            ["--backend-url"] = Configuration.BackendUrl,
            ["--tenant-id"] = Configuration.TenantId,
            ["--api-client-id"] = Configuration.ApiClientId,
            [option] = value
        };
        var arguments = CommandArguments.Parse(options.SelectMany(pair => new[] { pair.Key, pair.Value }));
        Assert.Throws<CliException>(() => CitadelCommands.ParseConfiguration(arguments));
    }

    [Fact]
    public async Task CreationConfiguresAllProtectionBeforeOpeningEachApi()
    {
        var writes = new List<(string Path, string Body)>();
        using var handler = ManagementHandler(writes);
        using var client = new HttpClient(handler);

        var baseUrl = await CitadelCommands.ConfigureGatewayAsync(
            client, "management-token", Configuration, CancellationToken.None);

        Assert.Equal("https://gateway.example/copilot-studio-local", baseUrl);
        Assert.All(writes, write => Assert.Contains("/apis/copilot-studio-local", write.Path));
        foreach (var api in CitadelCommands.BuildApis(Configuration))
        {
            var policyPath = $"/apis/{api.Id}/policies/policy";
            var apiPolicies = writes.Where(write => write.Path.EndsWith(policyPath, StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, apiPolicies.Length);
            Assert.Contains("Gateway configuration in progress", apiPolicies[0].Body);
            Assert.Contains("cors", apiPolicies[1].Body);
            var openingIndex = writes.IndexOf(apiPolicies[1]);
            var authIndex = writes.FindIndex(write =>
                write.Path.EndsWith($"/apis/{api.Id}/operations/invoke-a2a-runtime/policies/policy", StringComparison.Ordinal));
            Assert.InRange(authIndex, 0, openingIndex - 1);
            var operation = writes.Single(write =>
                write.Path.EndsWith($"/apis/{api.Id}/operations/invoke-a2a-runtime", StringComparison.Ordinal));
            using var definition = JsonDocument.Parse(operation.Body);
            Assert.Equal("application/json",
                definition.RootElement.GetProperty("properties").GetProperty("request")
                    .GetProperty("representations")[0].GetProperty("contentType").GetString());
            var discoveryPolicies = writes.Where(write =>
                write.Path.Contains($"/apis/{api.Id}/operations/", StringComparison.Ordinal) &&
                write.Path.EndsWith("/policies/policy", StringComparison.Ordinal) &&
                write.Body.Contains("allowed-origins", StringComparison.Ordinal)).ToArray();
            Assert.Equal(api.AgentId is null ? 1 : 4, discoveryPolicies.Length);
            Assert.All(discoveryPolicies, write =>
            {
                Assert.EndsWith("agent-card/policies/policy", write.Path);
                Assert.InRange(writes.IndexOf(write), 0, openingIndex - 1);
            });
            Assert.DoesNotContain(writes, write =>
                write.Path.EndsWith($"/apis/{api.Id}/operations/get-agent-catalog/policies/policy",
                    StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExistingApisAreNeverOverwrittenWithoutOwnershipAndOptIn(bool replace, bool owned)
    {
        var writes = new List<(string Path, string Body)>();
        using var handler = ManagementHandler(writes, exists: true, owned: owned);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<CliException>(() => CitadelCommands.ConfigureGatewayAsync(
            client, "management-token", Configuration with { Replace = replace }, CancellationToken.None));

        Assert.Empty(writes);
    }

    [Fact]
    public async Task FailedPolicyInstallationLeavesTheNewApiClosed()
    {
        var writes = new List<(string Path, string Body)>();
        using var handler = ManagementHandler(writes, failProtection: true);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<CliException>(() => CitadelCommands.ConfigureGatewayAsync(
            client, "management-token", Configuration, CancellationToken.None));

        Assert.Contains(writes, write => write.Body.Contains("Gateway configuration in progress", StringComparison.Ordinal));
        Assert.DoesNotContain(writes, write =>
            !write.Path.Contains("/operations/", StringComparison.Ordinal) &&
            write.Body.Contains("allowed-origins", StringComparison.Ordinal));
    }

    private static CitadelTestHandler ManagementHandler(
        List<(string Path, string Body)> writes, bool exists = false, bool owned = false, bool failProtection = false) =>
        new(async request =>
        {
            Assert.Equal("management-token", request.Headers.Authorization!.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/service/gateway", StringComparison.Ordinal))
                {
                    return Json(new { properties = new { gatewayUrl = "https://gateway.example" } });
                }
                return exists
                    ? Json(new
                    {
                        properties = new
                        {
                            description = owned ? CitadelCommands.OwnershipMarker : "Existing hosted API",
                            path = "copilot-studio-local"
                        }
                    })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = await request.Content!.ReadAsStringAsync();
            writes.Add((path, body));
            return failProtection && body.Contains("validate-jwt", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : Json(new { properties = new { provisioningState = "Succeeded" } });
        });

    internal static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

internal sealed class CitadelTestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
