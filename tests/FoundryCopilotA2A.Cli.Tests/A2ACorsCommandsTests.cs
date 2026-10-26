using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace FoundryCopilotA2A.Cli.Tests;

public sealed class A2ACorsCommandsTests
{
    private const string Policy = """
        <policies>
          <inbound>
            <cors allow-credentials="false">
              <allowed-origins><origin>https://frontend.example</origin></allowed-origins>
              <allowed-methods><method>POST</method></allowed-methods>
              <allowed-headers><header>Authorization</header><header>Content-Type</header></allowed-headers>
              <expose-headers><header>X-Trace-Id</header></expose-headers>
            </cors>
            <base />
            <validate-jwt header-name="Authorization"><audiences><audience>existing-audience</audience></audiences></validate-jwt>
            <set-backend-service base-url="{{existing-backend}}" />
          </inbound>
          <backend><forward-request buffer-response="false" /></backend>
          <outbound><base /></outbound>
          <on-error><base /></on-error>
        </policies>
        """;

    [Fact]
    public void AddsOnlyTheTwoHeadersAndIsIdempotent()
    {
        var patched = A2ACorsCommands.Patch(Policy);
        Assert.Equal(patched, A2ACorsCommands.Patch(patched));
        var document = XDocument.Parse(patched);
        foreach (var section in new[] { "allowed-headers", "expose-headers" })
        {
            var list = document.Descendants(section).Single();
            foreach (var header in new[] { "A2A-Extensions", "X-A2A-Extensions" })
            {
                Assert.Single(list.Elements("header"), element => element.Value == header);
            }
        }
        document.Descendants("header").Where(header => header.Value is "A2A-Extensions" or "X-A2A-Extensions").Remove();
        Assert.True(A2ACorsCommands.Equivalent(Policy, document.ToString()));
    }

    [Fact]
    public void ExistingCaseInsensitiveHeadersAreNotDuplicated()
    {
        var current = Policy.Replace("<header>Authorization</header>", "<header>a2a-extensions</header>");
        var document = XDocument.Parse(A2ACorsCommands.Patch(current));
        Assert.Single(document.Descendants("allowed-headers").Single().Elements(),
            element => element.Value.Equals("A2A-Extensions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreservesEscapedExpressionsAndSignificantWhitespace()
    {
        var original = Policy.Replace("<base />", """<set-variable name="value" value="@(context.Variables[&quot;example&quot;])" /><set-body> </set-body>""");
        var patched = XDocument.Parse(A2ACorsCommands.Patch(original), LoadOptions.PreserveWhitespace);
        Assert.All(patched.Descendants("set-body"), element => Assert.Equal(" ", element.Value));
        Assert.All(patched.Descendants("set-variable"),
            element => Assert.Equal("""@(context.Variables["example"])""", element.Attribute("value")?.Value));
    }

    [Fact]
    public void CreatesMissingExposeHeadersWithoutChangingOrigins()
    {
        var document = XDocument.Parse(Policy);
        document.Descendants("expose-headers").Remove();
        var patched = XDocument.Parse(A2ACorsCommands.Patch(document.ToString()));
        Assert.Equal(2, patched.Descendants("expose-headers").Single().Elements("header").Count());
        Assert.Equal("https://frontend.example", patched.Descendants("origin").Single().Value);
    }

    [Theory]
    [InlineData("<policies><inbound><base /></inbound></policies>")]
    [InlineData("<policies><inbound><cors /><cors /></inbound></policies>")]
    [InlineData("<policies><inbound><cors /></inbound></policies>")]
    [InlineData("<not-valid")]
    [InlineData("<!DOCTYPE policies [<!ENTITY x SYSTEM 'file:///missing'>]><policies>&x;</policies>")]
    public void UnsafeOrAmbiguousPoliciesAreRefused(string policy) =>
        Assert.Throws<CliException>(() => A2ACorsCommands.Patch(policy));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewDoesNotWriteAndApplyUsesETagAndReadBack(bool apply)
    {
        using var handler = new PolicyHandler();
        using var client = new HttpClient(handler);
        using var output = new StringWriter();
        await A2ACorsCommands.UpdateAsync(client, "test-token", "https://management.example/apis",
            ["first", "second"], apply, output, CancellationToken.None);
        Assert.Equal(apply ? 2 : 0, handler.Writes);
        Assert.Equal(apply ? 4 : 2, handler.Reads);
        Assert.DoesNotContain("test-token", output.ToString());
        Assert.DoesNotContain("existing-audience", output.ToString());
        if (apply)
        {
            await A2ACorsCommands.UpdateAsync(client, "test-token", "https://management.example/apis",
                ["first", "second"], true, output, CancellationToken.None);
            Assert.Equal(2, handler.Writes);
        }
    }

    [Fact]
    public async Task MissingSecondPolicyPreventsAllWrites()
    {
        using var handler = new PolicyHandler { MissingSecond = true };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<CliException>(() => A2ACorsCommands.UpdateAsync(client, "test-token",
            "https://management.example/apis", ["first", "second"], true, TextWriter.Null, CancellationToken.None));
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MissingETagAndConcurrentModificationFailClosed(bool omitETag, bool conflict)
    {
        using var handler = new PolicyHandler { OmitETag = omitETag, Conflict = conflict };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<CliException>(() => A2ACorsCommands.UpdateAsync(client, "test-token",
            "https://management.example/apis", ["first"], true, TextWriter.Null, CancellationToken.None));
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task UnexpectedReadBackIsNotReportedAsSuccess()
    {
        using var handler = new PolicyHandler { IgnoreWrite = true };
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<CliException>(() => A2ACorsCommands.UpdateAsync(client, "test-token",
            "https://management.example/apis", ["first"], true, TextWriter.Null, CancellationToken.None));
        Assert.Contains("Read-back verification failed", exception.Message);
    }

    private sealed class PolicyHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _policies = [];
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public bool MissingSecond { get; init; }
        public bool OmitETag { get; init; }
        public bool Conflict { get; init; }
        public bool IgnoreWrite { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Contains("format=xml", request.RequestUri!.Query);
            var key = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("\"version-1\"", Assert.Single(request.Headers.GetValues("If-Match")));
                if (Conflict) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("xml", body.RootElement.GetProperty("properties").GetProperty("format").GetString());
                if (!IgnoreWrite) _policies[key] = body.RootElement.GetProperty("properties").GetProperty("value").GetString()!;
                Writes++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Reads++;
            if (MissingSecond && key.Contains("/second/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { properties = new { value = _policies.GetValueOrDefault(key, Policy) } })
            };
            if (!OmitETag) response.Headers.ETag = new EntityTagHeaderValue("\"version-1\"");
            return response;
        }
    }
}
