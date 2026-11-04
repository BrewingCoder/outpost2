using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Calendar;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Where the zone for exact mail times comes from (OP2-285): each provider's own source,
/// against mocked HTTP. The tool-surface half is in <see cref="MailProviderSeamTests"/>.
/// </summary>
public sealed class MailboxZoneSourceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r);
            return Task.FromResult(respond(r));
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class GoogleTokens(string? token = "g-token") : IGoogleTokenAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection c, CancellationToken ct = default) => Task.FromResult(token);
    }

    private sealed class GraphTokens : IMicrosoftConnectionServiceAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection c, CancellationToken ct = default) =>
            Task.FromResult<string?>("ms-token");
    }

    private static Connection Conn(ProviderKind provider) => new()
    {
        UserId = Guid.CreateVersion7(), Label = provider == ProviderKind.Google ? "personal" : "work",
        Provider = provider, Instance = "x", AuthKind = AuthKind.OAuth2, SealedCredential = "sealed",
        State = ConnectionState.Active,
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GoogleMailboxZoneSource Source, Handler Http) Google(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string? token = "g-token")
    {
        var http = new Handler(respond);
        return (new GoogleMailboxZoneSource(new GoogleTokens(token), new OneClient(http),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<GoogleMailboxZoneSource>.Instance), http);
    }

    private static (CalendarService Service, Handler Http) Graph(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new Handler(respond);
        return (new CalendarService(new GraphTokens(), new OneClient(http),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<CalendarService>.Instance), http);
    }

    private static TimeSpan OffsetOn(TimeZoneInfo zone, int month) =>
        zone.GetUtcOffset(new DateTimeOffset(2026, month, 15, 12, 0, 0, TimeSpan.Zero));

    // ── Google ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Google_reads_the_calendar_timezone_setting_with_the_connections_own_token()
    {
        var (source, http) = Google(_ => Json("""{"kind":"calendar#setting","id":"timezone","value":"America/New_York"}"""));

        var zone = await source.LookupAsync(Conn(ProviderKind.Google));

        Assert.True(zone.Known);
        Assert.Equal(TimeSpan.FromHours(-5), OffsetOn(zone.Zone, 1));
        Assert.Equal(TimeSpan.FromHours(-4), OffsetOn(zone.Zone, 7));

        var req = Assert.Single(http.Requests);
        Assert.Equal(GoogleMailboxZoneSource.Endpoint, req.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer g-token", req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Google_answer_is_cached_per_connection()
    {
        var (source, http) = Google(_ => Json("""{"value":"Europe/London"}"""));
        var c = Conn(ProviderKind.Google);

        await source.LookupAsync(c);
        await source.LookupAsync(c);

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Google_account_genuinely_in_utc_is_known_not_unknown()
    {
        var (source, _) = Google(_ => Json("""{"value":"UTC"}"""));

        var zone = await source.LookupAsync(Conn(ProviderKind.Google));

        Assert.True(zone.Known);
        Assert.Equal(TimeSpan.Zero, OffsetOn(zone.Zone, 7));
    }

    [Fact]
    public async Task Google_without_the_calendar_settings_scope_is_unknown_and_not_asked_again_for_a_while()
    {
        // A mail-only connection, made before the scope was asked for: Google answers 403.
        var (source, http) = Google(_ => Json(
            """{"error":{"code":403,"message":"Request had insufficient authentication scopes."}}""",
            HttpStatusCode.Forbidden));
        var c = Conn(ProviderKind.Google);

        var zone = await source.LookupAsync(c);
        await source.LookupAsync(c);

        Assert.Equal(MailboxZone.Unavailable, zone);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Google_having_a_bad_minute_is_unknown_but_not_remembered()
    {
        var (source, http) = Google(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var c = Conn(ProviderKind.Google);

        Assert.False((await source.LookupAsync(c)).Known);
        await source.LookupAsync(c);

        Assert.Equal(2, http.Requests.Count);
    }

    [Theory]
    [InlineData("""{"value":"Not/AZone"}""")]
    [InlineData("""{"value":""}""")]
    [InlineData("""{}""")]
    public async Task Google_zone_this_host_cannot_resolve_is_unknown_rather_than_silently_utc(string body)
    {
        var (source, _) = Google(_ => Json(body));

        Assert.Equal(MailboxZone.Unavailable, await source.LookupAsync(Conn(ProviderKind.Google)));
    }

    [Fact]
    public async Task Google_connection_needing_a_sign_in_is_unknown_and_makes_no_call()
    {
        var (source, http) = Google(_ => throw new InvalidOperationException("must not be called"), token: null);

        Assert.Equal(MailboxZone.Unavailable, await source.LookupAsync(Conn(ProviderKind.Google)));
        Assert.Empty(http.Requests);
    }

    // ── the router ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lookup_that_throws_is_unavailable_never_an_exception()
    {
        var (source, _) = Google(_ => throw new HttpRequestException("connection reset"));
        var zones = new MailboxZones([source], NullLogger<MailboxZones>.Instance);

        Assert.Equal(MailboxZone.Unavailable, await zones.ForAsync(Conn(ProviderKind.Google)));
    }

    [Fact]
    public async Task A_provider_with_no_source_is_unavailable_and_no_other_source_is_asked()
    {
        var (source, http) = Google(_ => Json("""{"value":"UTC"}"""));
        var zones = new MailboxZones([source], NullLogger<MailboxZones>.Instance);

        Assert.Equal(MailboxZone.Unavailable, await zones.ForAsync(Conn(ProviderKind.Microsoft)));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Two_sources_for_one_provider_is_a_startup_error()
    {
        var (a, _) = Google(_ => Json("{}"));
        var (b, _) = Google(_ => Json("{}"));

        Assert.Throws<InvalidOperationException>(() => new MailboxZones([a, b], NullLogger<MailboxZones>.Instance));
    }

    // ── Microsoft ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Microsoft_reads_mailbox_settings_as_before()
    {
        var (calendar, http) = Graph(_ => Json("""{"timeZone":"Eastern Standard Time"}"""));

        var zone = await new GraphMailboxZoneSource(calendar).LookupAsync(Conn(ProviderKind.Microsoft));

        Assert.True(zone.Known);
        Assert.Equal(TimeSpan.FromHours(-5), OffsetOn(zone.Zone, 1));
        Assert.Contains("mailboxSettings", Assert.Single(http.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Microsoft_mailbox_genuinely_set_to_utc_is_known()
    {
        var (calendar, _) = Graph(_ => Json("""{"timeZone":"UTC"}"""));

        Assert.Equal(new MailboxZone(TimeZoneInfo.Utc, true).Known,
            (await calendar.MailboxZoneAsync(Conn(ProviderKind.Microsoft))).Known);
    }

    [Fact]
    public async Task Microsoft_mailbox_settings_refused_is_unknown_not_a_mailbox_in_utc()
    {
        // MailboxSettings is an optional scope; a connection consented before it answers 403.
        // Before OP2-285 this came back as a plain UTC zone and printed unlabelled.
        var (calendar, _) = Graph(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var zone = await calendar.MailboxZoneAsync(Conn(ProviderKind.Microsoft));

        Assert.Equal(MailboxZone.Unavailable, zone);
    }

    // ── zone ids on either kind of host ──────────────────────────────────────

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Eastern Standard Time")]
    public void Iana_and_windows_spellings_resolve_to_the_same_rules(string id)
    {
        // Google answers IANA, Graph usually Windows; the container is Linux and the
        // developer machines are Windows. Both spellings must resolve on both.
        var zone = CalendarService.TryResolve(id);

        Assert.NotNull(zone);
        Assert.Equal(TimeSpan.FromHours(-5), OffsetOn(zone, 1));
        Assert.Equal(TimeSpan.FromHours(-4), OffsetOn(zone, 7));
    }

    [Fact]
    public void An_unknown_zone_id_resolves_to_nothing_rather_than_to_utc()
    {
        Assert.Null(CalendarService.TryResolve("Not/AZone"));
    }
}
