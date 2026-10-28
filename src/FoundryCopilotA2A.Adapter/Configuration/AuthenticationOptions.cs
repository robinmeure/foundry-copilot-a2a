namespace FoundryCopilotA2A.Adapter;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; set; }

    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Explicit issuer allow-list. When empty, the v1.0 and v2.0 issuer forms are derived
    /// from the tenant in <see cref="Authority"/>.
    /// </summary>
    public string[] ValidIssuers { get; set; } = [];

    /// <summary>
    /// Explicit audience allow-list. When empty, both the "api://&lt;id&gt;" and bare-id forms
    /// of <see cref="Audience"/> are accepted.
    /// </summary>
    public string[] ValidAudiences { get; set; } = [];

    public IReadOnlyCollection<string> ResolveValidIssuers()
    {
        if (ValidIssuers.Length > 0)
        {
            return ValidIssuers;
        }

        var tenantId = ExtractTenantId(Authority);
        if (tenantId is null)
        {
            // Unrecognised authority shape: fall back to the configured value verbatim rather
            // than guessing, so validation stays strict.
            return [Authority];
        }

        return
        [
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            $"https://sts.windows.net/{tenantId}/"
        ];
    }

    public IReadOnlyCollection<string> ResolveValidAudiences()
    {
        if (ValidAudiences.Length > 0)
        {
            return ValidAudiences;
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            return [];
        }

        var bare = Audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
            ? Audience["api://".Length..]
            : Audience;

        return bare == Audience ? [Audience, $"api://{Audience}"] : [Audience, bare];
    }

    private static string? ExtractTenantId(string authority)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segment = uri.Segments
            .Select(s => s.Trim('/'))
            .FirstOrDefault(s => Guid.TryParse(s, out _) || s.Contains('.', StringComparison.Ordinal));

        return string.IsNullOrEmpty(segment) ? null : segment;
    }
}
