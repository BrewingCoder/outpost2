using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Calendar;

/// <summary>A mailbox's zone, and whether it is really the mailbox's (OP2-264, OP2-285).</summary>
/// <param name="Zone">The zone to print times in. UTC when <paramref name="Known"/> is false.</param>
/// <param name="Known">
/// Whether the lookup succeeded. Never inferred from <paramref name="Zone"/> being UTC: a
/// mailbox genuinely set to UTC is not an unknown one and must not be labelled as one.
/// </param>
public readonly record struct MailboxZone(TimeZoneInfo Zone, bool Known)
{
    /// <summary>The fallback: UTC is still an exact instant, and it is labelled as not the mailbox's.</summary>
    public static readonly MailboxZone Unavailable = new(TimeZoneInfo.Utc, false);
}

/// <summary>
/// One provider's answer to "what zone does this account live in" (OP2-285).
/// <para>
/// A seam of its own, the way mail got <c>IMailProvider</c>, so the answer comes from the
/// provider that owns the account. Before this the lookup was Microsoft Graph or nothing,
/// and every Gmail time printed as UTC.
/// </para>
/// </summary>
public interface IMailboxZoneSource
{
    ProviderKind Provider { get; }

    /// <summary>
    /// The account's zone. Implementations cache, because this is asked on every listing;
    /// they may throw, and <see cref="MailboxZones"/> turns any failure into
    /// <see cref="MailboxZone.Unavailable"/>.
    /// </summary>
    Task<MailboxZone> LookupAsync(Connection connection, CancellationToken ct = default);
}

public interface IMailboxZones
{
    /// <summary>
    /// The connection's zone from its own provider. Never throws except on cancellation: a
    /// zone lookup must never be able to break a read.
    /// </summary>
    Task<MailboxZone> ForAsync(Connection connection, CancellationToken ct = default);
}

/// <summary>
/// Sends each lookup to the source for the connection's provider, and nowhere else — a
/// Graph call must never be made with somebody's Google token (OP2-278). A provider with no
/// source is simply unknown, labelled UTC.
/// </summary>
public sealed class MailboxZones(IEnumerable<IMailboxZoneSource> sources, ILogger<MailboxZones> log) : IMailboxZones
{
    private readonly Dictionary<ProviderKind, IMailboxZoneSource> _by = Index(sources);

    private static Dictionary<ProviderKind, IMailboxZoneSource> Index(IEnumerable<IMailboxZoneSource> sources)
    {
        var by = new Dictionary<ProviderKind, IMailboxZoneSource>();
        foreach (var s in sources)
            if (!by.TryAdd(s.Provider, s))
                throw new InvalidOperationException($"Two mailbox zone sources are registered for {s.Provider}.");
        return by;
    }

    public async Task<MailboxZone> ForAsync(Connection connection, CancellationToken ct = default)
    {
        if (!_by.TryGetValue(connection.Provider, out var source)) return MailboxZone.Unavailable;

        try { return await source.LookupAsync(connection, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogInformation(ex, "Mailbox zone lookup failed for {Label}; printing UTC.", connection.Label);
            return MailboxZone.Unavailable;
        }
    }
}

/// <summary>
/// Microsoft: the mailbox-settings zone the calendar already reads and caches, so mail and
/// calendar can never disagree about where a mailbox lives.
/// </summary>
public sealed class GraphMailboxZoneSource(ICalendarService calendar) : IMailboxZoneSource
{
    public ProviderKind Provider => ProviderKind.Microsoft;

    public Task<MailboxZone> LookupAsync(Connection connection, CancellationToken ct = default) =>
        calendar.MailboxZoneAsync(connection, ct);
}

/// <summary>
/// Google: the account's Calendar time-zone setting (OP2-285).
/// <para>
/// Gmail itself has no zone setting an API can read; the zone a Google account lives in is
/// its Calendar setting, <c>GET calendar/v3/users/me/settings/timezone</c>, which answers
/// an IANA id such as <c>America/New_York</c>. That call needs a Calendar scope, and a
/// connection made for mail only does not carry one, so Outpost asks for the narrowest that
/// covers it — <c>calendar.settings.readonly</c>, which reads settings and no events — as an
/// optional extra. A connection without it (made before it was asked for, or with the box
/// unticked) answers 403 here, and prints labelled UTC until it is reconnected.
/// </para>
/// </summary>
public sealed class GoogleMailboxZoneSource(
    IGoogleTokenAccessor tokens,
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<GoogleMailboxZoneSource> log) : IMailboxZoneSource
{
    public const string Endpoint = "https://www.googleapis.com/calendar/v3/users/me/settings/timezone";

    /// <summary>As long as Graph's, for the same reason: people reset their zone when they travel, not often.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    /// <summary>
    /// A failure is remembered too, or every listing would make a doomed call per Google
    /// mailbox — but for less long, so a reconnect that grants the scope shows within the hour.
    /// </summary>
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Addressable, because a reconnect has to clear it: the whole point of granting the
    /// scope is that the next read prints the real zone, not that it does an hour later.
    /// </summary>
    public static string CacheKey(Guid connectionId) => $"op2:gzone:{connectionId}";

    private static string Key(Connection c) => CacheKey(c.Id);

    public ProviderKind Provider => ProviderKind.Google;

    public async Task<MailboxZone> LookupAsync(Connection connection, CancellationToken ct = default)
    {
        if (cache.TryGetValue<MailboxZone>(Key(connection), out var cached)) return cached;

        var token = await tokens.GetAccessTokenAsync(connection, ct);
        if (token is null) return MailboxZone.Unavailable; // needs reconnecting; nothing to cache

        using var http = httpFactory.CreateClient("google");
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogInformation(
                "Google account zone unavailable for {Label} (HTTP {Status}{Hint}); using UTC.",
                connection.Label, (int)resp.StatusCode,
                resp.StatusCode == HttpStatusCode.Forbidden ? ", calendar.settings.readonly not granted" : "");
            // A refusal (no scope, no Calendar for this account) will say the same next time;
            // Google having a bad minute will not, so only the first is remembered.
            var lasting = (int)resp.StatusCode is >= 400 and < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests;
            return lasting ? Remember(connection, MailboxZone.Unavailable, FailureLifetime) : MailboxZone.Unavailable;
        }

        var setting = await resp.Content.ReadFromJsonAsync<Setting>(ct);
        var zone = string.IsNullOrWhiteSpace(setting?.Value) ? null : CalendarService.TryResolve(setting.Value);

        if (zone is null)
        {
            log.LogWarning("Google returned a zone for {Label} this host cannot resolve: {Zone}.",
                connection.Label, setting?.Value);
            return Remember(connection, MailboxZone.Unavailable, FailureLifetime);
        }

        return Remember(connection, new MailboxZone(zone, true), Lifetime);
    }

    private MailboxZone Remember(Connection connection, MailboxZone zone, TimeSpan lifetime)
    {
        cache.Set(Key(connection), zone, lifetime);
        return zone;
    }

    private sealed record Setting([property: JsonPropertyName("value")] string? Value);
}
