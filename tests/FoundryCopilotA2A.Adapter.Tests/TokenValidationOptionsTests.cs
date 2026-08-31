namespace FoundryCopilotA2A.Adapter.Tests;

/// <summary>
/// Entra issues v1.0 tokens from sts.windows.net and v2.0 tokens from
/// login.microsoftonline.com/&lt;tenant&gt;/v2.0. Which one arrives depends on the calling
/// client, not on this API, so the adapter must accept both forms for its own tenant while
/// still rejecting every other issuer. These tests pin that behaviour, because getting it
/// wrong shows up as an opaque 401 that is easy to "fix" by disabling issuer validation.
/// </summary>
public class TokenValidationOptionsTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";

    private static AuthenticationOptions ForTenant() => new()
    {
        Enabled = true,
        Authority = $"https://login.microsoftonline.com/{TenantId}/v2.0",
        Audience = "api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
    };

    [Fact]
    public void BothEntraIssuerFormsAreAcceptedForTheConfiguredTenant()
    {
        var issuers = ForTenant().ResolveValidIssuers();

        Assert.Contains($"https://login.microsoftonline.com/{TenantId}/v2.0", issuers);
        Assert.Contains($"https://sts.windows.net/{TenantId}/", issuers);
    }

    [Fact]
    public void IssuersFromAnotherTenantAreNotAccepted()
    {
        var issuers = ForTenant().ResolveValidIssuers();
        const string otherTenant = "99999999-8888-7777-6666-555555555555";

        Assert.DoesNotContain($"https://login.microsoftonline.com/{otherTenant}/v2.0", issuers);
        Assert.DoesNotContain($"https://sts.windows.net/{otherTenant}/", issuers);
    }

    [Fact]
    public void ExplicitIssuerAllowListWins()
    {
        var options = ForTenant();
        options.ValidIssuers = ["https://example.test/issuer"];

        Assert.Equal(["https://example.test/issuer"], options.ResolveValidIssuers());
    }

    [Fact]
    public void AnUnrecognisedAuthorityDoesNotSilentlyWidenValidation()
    {
        var options = new AuthenticationOptions { Enabled = true, Authority = "not-a-url" };

        // The point is that it stays strict: it must not fall back to "any issuer".
        var issuers = options.ResolveValidIssuers();

        Assert.Equal(["not-a-url"], issuers);
    }

    [Theory]
    [InlineData("api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    public void BothAudienceFormsAreAccepted(string presentedAudience)
    {
        Assert.Contains(presentedAudience, ForTenant().ResolveValidAudiences());
    }

    [Fact]
    public void BareClientIdAudienceAlsoAcceptsTheApiUriForm()
    {
        var options = ForTenant();
        options.Audience = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var audiences = options.ResolveValidAudiences();

        Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", audiences);
        Assert.Contains("api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", audiences);
    }

    [Fact]
    public void AnotherApplicationsAudienceIsNotAccepted()
    {
        Assert.DoesNotContain(
            "api://11111111-2222-3333-4444-555555555555",
            ForTenant().ResolveValidAudiences());
    }
}

