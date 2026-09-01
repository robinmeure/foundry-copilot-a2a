using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FoundryCopilotA2A.Adapter.Tests;

/// <summary>
/// Contract tests for the security properties the adapter is supposed to guarantee.
///
/// These run with authentication enabled and real, distinct caller identities. That matters:
/// the rest of the suite runs in anonymous development mode, where every caller collapses into
/// one identity partition, so it cannot observe cross-caller isolation at all.
/// </summary>
public sealed class SecurityContractTests : IClassFixture<AuthenticatedAdapterFactory>
{
    private readonly AuthenticatedAdapterFactory _factory;

    public SecurityContractTests(AuthenticatedAdapterFactory factory) => _factory = factory;

    /// <summary>
    /// The disclosure defect in its original form: two callers, one messageId. Each caller must
    /// get its own delegated invocation, never the other caller's cached response.
    /// </summary>
    [Fact]
    public async Task TwoCallersSharingAMessageIdAreIsolated()
    {
        var messageId = NewId();
        var before = Invoker.InvocationCount;

        using var victim = await SendAsync(Caller.Victim, messageId, "ctx-shared", "VICTIM SECRET QUESTION");
        using var attacker = await SendAsync(Caller.Attacker, messageId, "ctx-shared", "VICTIM SECRET QUESTION");

        var victimBody = await victim.Content.ReadAsStringAsync();
        var attackerBody = await attacker.Content.ReadAsStringAsync();

        // Identical content, so the payload hash matches and neither request is refused.
        // Isolation must instead come from the caller identity being part of the cache key.
        Assert.Equal(HttpStatusCode.OK, victim.StatusCode);
        Assert.Equal(HttpStatusCode.OK, attacker.StatusCode);
        Assert.Equal(before + 2, Invoker.InvocationCount);
        Assert.NotEqual(ResponseText(victimBody), ResponseText(attackerBody));
    }

    /// <summary>
    /// "oid" is unique only within a tenant, so the identity partition must be scoped by "tid".
    /// </summary>
    [Fact]
    public async Task SameObjectIdInDifferentTenantsIsADifferentCaller()
    {
        var messageId = NewId();
        var before = Invoker.InvocationCount;

        var tenantA = new Caller("tenant-a", "shared-object-id");
        var tenantB = new Caller("tenant-b", "shared-object-id");

        using var first = await SendAsync(tenantA, messageId, "ctx-tenant", "same text");
        using var second = await SendAsync(tenantB, messageId, "ctx-tenant", "same text");

        Assert.Equal(before + 2, Invoker.InvocationCount);
        Assert.NotEqual(
            ResponseText(await first.Content.ReadAsStringAsync()),
            ResponseText(await second.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// Replay protection still applies within a single caller.
    /// </summary>
    [Fact]
    public async Task ReplayWithinOneCallerIsStillRefused()
    {
        var messageId = NewId();

        using var first = await SendAsync(Caller.Victim, messageId, "ctx-self-replay", "original");
        using var second = await SendAsync(Caller.Victim, messageId, "ctx-self-replay", "changed");

        var body = await second.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(-32600, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>
    /// A retry by the same caller must be served from cache rather than repeating the side effect.
    /// </summary>
    [Fact]
    public async Task RetryBySameCallerDoesNotRepeatTheSideEffect()
    {
        var messageId = NewId();
        var before = Invoker.InvocationCount;

        using var first = await SendAsync(Caller.Victim, messageId, "ctx-retry", "do it once");
        using var second = await SendAsync(Caller.Victim, messageId, "ctx-retry", "do it once");

        Assert.Equal(before + 1, Invoker.InvocationCount);
        Assert.Equal(
            ResponseText(await first.Content.ReadAsStringAsync()),
            ResponseText(await second.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// Fail closed: an authenticated deployment that cannot establish the caller must refuse the
    /// request rather than fall back to a shared partition.
    /// </summary>
    [Fact]
    public async Task AuthenticatedRequestWithoutIdentityClaimsIsRefused()
    {
        var before = Invoker.InvocationCount;

        using var response = await SendAsync(Caller.NoClaims, NewId(), "ctx-anonymous", "who am I");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("mock-copilot-studio", body);
        Assert.Equal(before, Invoker.InvocationCount);
    }

    [Fact]
    public async Task TraceCanOnlyBeReadByTheCallerWhoCreatedIt()
    {
        using var victimResponse = await SendAsync(
            Caller.Victim,
            NewId(),
            "ctx-private-trace",
            "private trace");
        victimResponse.EnsureSuccessStatusCode();
        Assert.True(
            victimResponse.Headers.TryGetValues(
                AdapterConstants.TraceHeaderName,
                out var traceIds));
        var traceId = Assert.Single(traceIds);

        using var victimClient = _factory.CreateAuthenticatedClient(Caller.Victim);
        using var victimTrace = await victimClient.GetAsync(
            $"{AdapterConstants.TracesPath}/{traceId}");
        Assert.Equal(HttpStatusCode.OK, victimTrace.StatusCode);

        using var attackerClient = _factory.CreateAuthenticatedClient(Caller.Attacker);
        using var attackerTrace = await attackerClient.GetAsync(
            $"{AdapterConstants.TracesPath}/{traceId}");
        Assert.Equal(HttpStatusCode.NotFound, attackerTrace.StatusCode);
    }

    /// <summary>
    /// An unauthenticated caller must not reach the runtime at all when authentication is enabled.
    /// </summary>
    [Fact]
    public async Task UnauthenticatedRequestIsRejectedWhenAuthenticationIsEnabled()
    {
        var before = Invoker.InvocationCount;
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            AdapterConstants.RuntimePath,
            BuildEnvelope(NewId(), "ctx-unauth", "let me in"),
            WebOptions);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401/403 but got {(int)response.StatusCode}.");
        Assert.Equal(before, Invoker.InvocationCount);
    }

    /// <summary>
    /// The agent card is the contract Foundry reads. Both versions must stay correct, because the
    /// preview tool fetches it without a version header and parses it as 0.3.
    /// </summary>
    [Fact]
    public async Task AgentCardVersionContractIsStable()
    {
        using var client = _factory.CreateAuthenticatedClient(Caller.Victim);

        using var v03Response = await client.GetAsync("/.well-known/agent-card.json");
        var v03 = JsonDocument.Parse(await v03Response.Content.ReadAsStringAsync()).RootElement;

        using var v1Request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/agent-card.json");
        v1Request.Headers.Add("A2A-Version", "1.0");
        using var v1Response = await client.SendAsync(v1Request);
        var v1 = JsonDocument.Parse(await v1Response.Content.ReadAsStringAsync()).RootElement;

        // 0.3 shape: a flat "url" plus "protocolVersion", which is what the preview tool binds to.
        Assert.Equal("0.3.0", v03.GetProperty("protocolVersion").GetString());
        Assert.Equal($"https://adapter.test{AdapterConstants.RuntimePath}", v03.GetProperty("url").GetString());

        // 1.0 shape: interfaces carry the transport and version.
        var interfaces = v1.GetProperty("supportedInterfaces");
        Assert.NotEqual(0, interfaces.GetArrayLength());
        var primary = interfaces[0];
        Assert.Equal("JSONRPC", primary.GetProperty("protocolBinding").GetString());
        Assert.Equal($"https://adapter.test{AdapterConstants.RuntimePath}", primary.GetProperty("url").GetString());

        // Both must advertise the same runtime URL, or Foundry will call an endpoint that does
        // not exist depending on which version it negotiated.
        Assert.Equal(v03.GetProperty("url").GetString(), primary.GetProperty("url").GetString());
    }

    /// <summary>
    /// Representative load: many distinct callers and messages at once must all be served
    /// correctly, with exactly one delegated invocation each.
    /// </summary>
    [Fact]
    public async Task ConcurrentDistinctCallersEachGetExactlyOneInvocation()
    {
        const int callerCount = 25;
        var before = Invoker.InvocationCount;
        var messageId = NewId();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, callerCount).Select(index =>
                SendAsync(new Caller("load-tenant", $"caller-{index}"), messageId, "ctx-load", "same text")));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(before + callerCount, Invoker.InvocationCount);

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        // Every caller must receive a distinct delegated answer, never a neighbour's.
        Assert.Equal(callerCount, bodies.Select(ResponseText).Distinct().Count());

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private MockCopilotStudioInvoker Invoker =>
        Assert.IsType<MockCopilotStudioInvoker>(
            _factory.Services.GetRequiredService<ICopilotStudioInvoker>());

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static string NewId() => $"msg-{Guid.NewGuid():N}";

    private static string? ResponseText(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("message", out var message)
            && message.TryGetProperty("parts", out var parts)
            && parts.GetArrayLength() > 0
            ? parts[0].GetProperty("text").GetString()
            : null;
    }

    private static object BuildEnvelope(string messageId, string contextId, string text) => new
    {
        jsonrpc = "2.0",
        id = messageId,
        method = "SendMessage",
        @params = new
        {
            message = new
            {
                role = "ROLE_USER",
                parts = new[] { new { text } },
                messageId,
                contextId
            }
        }
    };

    private Task<HttpResponseMessage> SendAsync(
        Caller caller,
        string messageId,
        string contextId,
        string text)
    {
        var client = _factory.CreateAuthenticatedClient(caller);
        var request = new HttpRequestMessage(HttpMethod.Post, AdapterConstants.RuntimePath)
        {
            Content = JsonContent.Create(BuildEnvelope(messageId, contextId, text), options: WebOptions)
        };
        request.Headers.Add("A2A-Version", "1.0");
        return client.SendAsync(request);
    }
}

/// <summary>
/// Contract tests that need their own host configuration.
/// </summary>
public sealed class TimeoutContractTests
{
    /// <summary>
    /// A delegated call that never returns must not pin a request, a thread, or a cache slot
    /// forever. The configured request timeout has to actually bound it.
    /// </summary>
    [Fact]
    public async Task SlowBackendIsBoundedByTheConfiguredRequestTimeout()
    {
        using var factory = new AuthenticatedAdapterFactory(
            configure: builder =>
            {
                builder.UseSetting("Adapter:RequestTimeoutSeconds", "1");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ICopilotStudioInvoker>();
                    services.AddSingleton<ICopilotStudioInvoker>(new StallingInvoker());
                });
            });

        var client = factory.CreateAuthenticatedClient(Caller.Victim);
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.PostAsJsonAsync(
            AdapterConstants.RuntimePath,
            new
            {
                jsonrpc = "2.0",
                id = "timeout-1",
                method = "SendMessage",
                @params = new
                {
                    message = new
                    {
                        role = "ROLE_USER",
                        parts = new[] { new { text = "this will stall" } },
                        messageId = "timeout-1",
                        contextId = "ctx-timeout"
                    }
                }
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        stopwatch.Stop();

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("mock-copilot-studio", body);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Request took {stopwatch.Elapsed}, so the timeout did not bound it.");
    }

    private sealed class StallingInvoker : ICopilotStudioInvoker
    {
        public async IAsyncEnumerable<CopilotInvocationUpdate> StreamAsync(
            string prompt,
            A2ARequestMetadata metadata,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}

public sealed record Caller(string? TenantId, string? ObjectId)
{
    public static readonly Caller Victim = new("tenant-1", "victim-oid");
    public static readonly Caller Attacker = new("tenant-1", "attacker-oid");
    public static readonly Caller NoClaims = new(null, null);
}

/// <summary>
/// Hosts the adapter with authentication enabled and a test scheme that mints the caller
/// identity from request headers, so isolation can be exercised without a real token service.
/// </summary>
public sealed class AuthenticatedAdapterFactory : WebApplicationFactory<Program>
{
    private readonly Action<IWebHostBuilder>? _configure;

    public AuthenticatedAdapterFactory()
        : this(null)
    {
    }

    // Internal: xUnit requires a class fixture to expose exactly one public constructor.
    internal AuthenticatedAdapterFactory(Action<IWebHostBuilder>? configure) => _configure = configure;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Adapter:Backend", "Mock");
        builder.UseSetting("Adapter:PublicBaseUrl", "https://adapter.test");
        builder.UseSetting("Adapter:AllowAnonymousDevelopmentMode", "false");
        builder.UseSetting("Authentication:Enabled", "true");
        builder.UseSetting("Authentication:Authority", "https://login.microsoftonline.com/tenant-1/v2.0");
        builder.UseSetting("Authentication:Audience", "api://adapter-test");

        builder.ConfigureTestServices(services =>
        {
            services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    _ => { });
        });

        _configure?.Invoke(builder);
    }

    public HttpClient CreateAuthenticatedClient(Caller caller)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.AuthenticateHeader, "true");

        if (caller.TenantId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.TenantHeader, caller.TenantId);
        }

        if (caller.ObjectId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ObjectIdHeader, caller.ObjectId);
        }

        return client;
    }
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";
    public const string AuthenticateHeader = "X-Test-Authenticated";
    public const string TenantHeader = "X-Test-Tid";
    public const string ObjectIdHeader = "X-Test-Oid";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(AuthenticateHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>();

        if (Request.Headers.TryGetValue(TenantHeader, out var tenantId))
        {
            claims.Add(new Claim("tid", tenantId.ToString()));
        }

        // Deliberately allows a token with no "oid": that is the fail-closed case under test.
        if (Request.Headers.TryGetValue(ObjectIdHeader, out var objectId))
        {
            claims.Add(new Claim("oid", objectId.ToString()));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
