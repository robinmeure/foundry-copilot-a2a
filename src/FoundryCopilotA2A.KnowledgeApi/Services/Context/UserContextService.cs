using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using FoundryCopilotA2A.KnowledgeApi.Authentication;
using FoundryCopilotA2A.KnowledgeApi.Contracts.Chat;
using FoundryCopilotA2A.KnowledgeApi.Interfaces;
using FoundryCopilotA2A.KnowledgeApi.Middleware;
using Microsoft.Graph.Models;

namespace FoundryCopilotA2A.KnowledgeApi.Services.Context;

/// <summary>
/// Resolves the user's profile and effective time zone for each chat turn. Graph lookups are
/// best-effort and cached per user, so a missing permission never blocks a chat answer.
/// </summary>
public sealed partial class UserContextService
{
    private const int MaxCachedUsers = 1000;
    private const int MaxClientTimeZoneLength = 64;

    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(2);

    private static readonly string[] ProfileFields =
    [
        "displayName", "givenName", "mail", "userPrincipalName", "jobTitle",
        "department", "officeLocation", "preferredLanguage"
    ];

    private static readonly UserProfile MockProfile = new(
        DisplayName: "Mock User",
        GivenName: "Mock",
        Email: "mock.user@example.com",
        JobTitle: "Developer",
        PreferredLanguage: "en-US");

    private readonly IGraphService graph;
    private readonly IConfiguration configuration;
    private readonly ILogger<UserContextService> logger;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    public UserContextService(IGraphService graph, IConfiguration configuration, ILogger<UserContextService> logger)
        : this(graph, configuration, logger, TimeProvider.System)
    {
    }

    internal UserContextService(
        IGraphService graph,
        IConfiguration configuration,
        ILogger<UserContextService> logger,
        TimeProvider clock)
    {
        this.graph = graph;
        this.configuration = configuration;
        this.logger = logger;
        this.clock = clock;
    }

    public async Task<UserContext> ResolveAsync(
        HttpContext context,
        Caller caller,
        string? clientTimeZone,
        CancellationToken cancellationToken)
    {
        var profile = configuration["Knowledge:Backend"] == "Mock"
            ? MockProfile
            : await GetProfileAsync(context, caller, cancellationToken);
        var now = clock.GetUtcNow();

        if (FindTimeZone(profile?.MailboxTimeZone) is { } mailboxZone)
        {
            return new UserContext(profile, mailboxZone, UserContext.MailboxSource, now);
        }

        if (IsValidClientTimeZone(clientTimeZone) && FindTimeZone(clientTimeZone) is { } browserZone)
        {
            return new UserContext(profile, browserZone, UserContext.BrowserSource, now);
        }

        return new UserContext(profile, TimeZoneInfo.Utc, UserContext.DefaultSource, now);
    }

    private async Task<UserProfile?> GetProfileAsync(HttpContext context, Caller caller, CancellationToken cancellationToken)
    {
        var key = $"{caller.Tenant}\n{caller.User}";
        var now = clock.GetUtcNow();
        if (cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Profile;
        }

        var profile = await LoadProfileAsync(context, cancellationToken);
        Store(key, new CacheEntry(profile, now + (profile is null ? FailureLifetime : SuccessLifetime)), now);
        return profile;
    }

    private async Task<UserProfile?> LoadProfileAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var originalSignals = ResponseSignals.Capture(context);

        try
        {
            return await graph.WithClientAsync(context, async client =>
            {
                // Sequential on purpose: the Graph transport writes response headers on failures.
                var user = await TryReadAsync("profile", () => client.Me.GetAsync(
                    request => request.QueryParameters.Select = ProfileFields, cancellationToken), cancellationToken);
                var mailbox = await TryReadAsync("mailbox settings", () => client.Me.MailboxSettings.GetAsync(
                    cancellationToken: cancellationToken), cancellationToken);
                return user is null && mailbox is null ? null : ToProfile(user, mailbox);
            }, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "User context could not acquire a Graph token; continuing without profile ({Error})",
                exception.GetType().Name);
            return null;
        }
        finally
        {
            ResponseSignals.Restore(context, originalSignals);
        }
    }

    private async Task<T?> TryReadAsync<T>(string name, Func<Task<T?>> read, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "User context could not read Graph {Part}; continuing without it ({Error})",
                name,
                exception is ApiException api ? $"HTTP {api.Status}" : exception.GetType().Name);
            return null;
        }
    }

    private static UserProfile ToProfile(User? user, MailboxSettings? mailbox) => new(
        DisplayName: user?.DisplayName,
        GivenName: user?.GivenName,
        Email: user?.Mail ?? user?.UserPrincipalName,
        JobTitle: user?.JobTitle,
        Department: user?.Department,
        OfficeLocation: user?.OfficeLocation,
        PreferredLanguage: user?.PreferredLanguage ?? mailbox?.Language?.Locale,
        MailboxTimeZone: mailbox?.TimeZone,
        DateFormat: mailbox?.DateFormat,
        TimeFormat: mailbox?.TimeFormat,
        WorkingHours: DescribeWorkingHours(mailbox?.WorkingHours));

    private static string? DescribeWorkingHours(WorkingHours? hours)
    {
        if (hours?.StartTime is not { } start || hours.EndTime is not { } end)
        {
            return null;
        }

        var days = hours.DaysOfWeek?
            .Where(day => day.HasValue)
            .Select(day => day!.Value.ToString())
            .ToArray() ?? [];
        var zone = hours.TimeZone?.Name;
        return string.Create(CultureInfo.InvariantCulture,
            $"{(days.Length > 0 ? string.Join(", ", days) + " " : "")}{start.Hour:00}:{start.Minute:00}-{end.Hour:00}:{end.Minute:00}{(string.IsNullOrWhiteSpace(zone) ? "" : $" ({zone})")}");
    }

    private void Store(string key, CacheEntry entry, DateTimeOffset now)
    {
        if (cache.Count >= MaxCachedUsers && !cache.ContainsKey(key))
        {
            foreach (var expired in cache.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            {
                cache.TryRemove(expired, out _);
            }

            if (cache.Count >= MaxCachedUsers)
            {
                var oldest = cache.MinBy(item => item.Value.ExpiresAt).Key;
                cache.TryRemove(oldest, out _);
            }
        }

        cache[key] = entry;
    }

    internal static bool IsValidClientTimeZone(string? value) =>
        value is { Length: > 0 and <= MaxClientTimeZoneLength } && ClientTimeZonePattern().IsMatch(value);

    internal static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (id is "UTC" or "Etc/UTC" or "tzone://Microsoft/Utc")
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_+\-/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientTimeZonePattern();

    private sealed record CacheEntry(UserProfile? Profile, DateTimeOffset ExpiresAt);
}
