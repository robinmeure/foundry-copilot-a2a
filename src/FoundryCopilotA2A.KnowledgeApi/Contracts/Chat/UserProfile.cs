namespace FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;

/// <summary>Default profile properties of the signed-in user, read from Microsoft Graph.</summary>
public sealed record UserProfile(
    string? DisplayName = null,
    string? GivenName = null,
    string? Email = null,
    string? JobTitle = null,
    string? Department = null,
    string? OfficeLocation = null,
    string? PreferredLanguage = null,
    string? MailboxTimeZone = null,
    string? DateFormat = null,
    string? TimeFormat = null,
    string? WorkingHours = null);
