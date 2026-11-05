using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Calendar;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Calendar;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;
using OutPost2.Infrastructure.People;
using OutPost2.Web;
using OutPost2.Web.Jobs;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// One test per assumption the OP2-294 sweep found aged — each a place that was right while
/// Microsoft was the only mail provider and became wrong, silently, when Gmail and personal
/// Microsoft accounts arrived. <see cref="ProviderParityMatrixTests"/> is what stops the
/// next one; these pin the ones already found.
/// </summary>
public sealed class ProviderParityFixTests
{
    private static Connection Conn(
        string label, ProviderKind provider, bool personal = false, bool grantAll = true,
        IEnumerable<string>? ungranted = null)
    {
        var c = new Connection
        {
            UserId = Guid.Empty, Label = label, Provider = provider,
            Instance = personal ? Connection.ConsumerTenantId : provider == ProviderKind.Microsoft ? "72f988bf-86f1-41af-91ab-2d7cd011db47" : "google",
            AuthKind = AuthKind.OAuth2, SealedCredential = "sealed", State = ConnectionState.Active,
            IsPersonalAccount = personal, IsDefaultForProvider = true,
        };
        var off = new HashSet<string>(ungranted ?? []);
        foreach (var name in CapabilityCatalog.DeclaredFor(provider))
            c.Capabilities.Add(new Capability { Name = name, Granted = grantAll && !off.Contains(name), Enabled = true });
        return c;
    }

    // ── the resolver names the provider ──────────────────────────────────────

    [Fact]
    public void Naming_a_gmail_connection_for_the_calendar_says_gmail_has_no_calendar_here()
    {
        // Was: "No connection called gmail. You have gmail." — the OP2-166 shape again.
        var gmail = Conn("gmail", ProviderKind.Google);

        var r = ConnectionResolver.Resolve([gmail], Capabilities.CalendarRead, "gmail", isWrite: false);

        Assert.Equal(ResolutionKind.Unavailable, r.Kind);
        Assert.Contains("Gmail", r.SpokenSummary);
        Assert.Contains("read your calendar", r.SpokenSummary);
        Assert.DoesNotContain("No connection called", r.SpokenSummary);
    }

    [Fact]
    public void Asking_for_the_calendar_with_only_gmail_connected_says_why()
    {
        var r = ConnectionResolver.Resolve(
            [Conn("gmail", ProviderKind.Google)], Capabilities.CalendarRead, null, isWrite: false);

        Assert.Equal(ResolutionKind.None, r.Kind);
        Assert.StartsWith("No connection set up for that yet.", r.SpokenSummary);
        Assert.Contains("Gmail (gmail)", r.SpokenSummary);
    }

    [Fact]
    public void With_nothing_connected_the_sentence_is_unchanged()
    {
        var r = ConnectionResolver.Resolve([], Capabilities.CalendarRead, null, isWrite: false);
        Assert.Equal("No connection set up for that yet.", r.SpokenSummary);
    }

    [Fact]
    public void An_unknown_label_is_still_unknown()
    {
        var r = ConnectionResolver.Resolve(
            [Conn("gmail", ProviderKind.Google)], Capabilities.CalendarRead, "holiday", isWrite: false);
        Assert.Contains("No connection called holiday", r.SpokenSummary);
    }

    [Fact]
    public void A_personal_account_is_never_sent_to_a_tenant_administrator()
    {
        var personal = Conn("home", ProviderKind.Microsoft, personal: true, ungranted: [Capabilities.MailSend]);
        var work = Conn("work", ProviderKind.Microsoft, ungranted: [Capabilities.MailSend]);

        var home = ConnectionResolver.Resolve([personal], Capabilities.MailSend, "home", isWrite: true);
        var office = ConnectionResolver.Resolve([work], Capabilities.MailSend, "work", isWrite: true);

        Assert.DoesNotContain("administrator", home.SpokenSummary);
        Assert.Contains("tenant administrator", office.SpokenSummary);
    }

    // ── mail words, per provider ─────────────────────────────────────────────

    [Fact]
    public void Every_mail_provider_has_words_of_its_own()
    {
        // MailWords.For used to be "Google ? Gmail : Outlook": a third provider would have
        // spoken Outlook. Now an unnamed one gets Neutral, and this fails until it has words.
        var mail = Enum.GetValues<ProviderKind>()
            .Where(p => CapabilityCatalog.DeclaredFor(p).Contains(Capabilities.MailRead));

        foreach (var p in mail)
            Assert.False(ReferenceEquals(MailWords.For(p), MailWords.Neutral), $"{p} has no MailWords.");

        Assert.Same(MailWords.Neutral, MailWords.For(ProviderKind.YouTrack));
    }

    private static readonly IReadOnlyList<MailFolder> GmailLabels =
    [
        new() { Id = "INBOX", Name = "Inbox", Path = "Inbox", WellKnownName = "inbox", MessageCount = 3 },
        new() { Id = "SPAM", Name = "Spam", Path = "Spam", WellKnownName = "junkemail" },
        new() { Id = "TRASH", Name = "Trash", Path = "Trash", WellKnownName = "deleteditems" },
        new() { Id = "Label_1", Name = "Receipts", Path = "Receipts", MessageCount = 4 },
    ];

    [Fact]
    public void Gmail_system_labels_are_not_called_outlooks_own()
    {
        var said = MailTools.Listing(GmailLabels, root: null, "personal", MailWords.Gmail);

        Assert.Contains("Spam — 0 messages (Gmail's own)", said);
        Assert.DoesNotContain("Outlook", said);
    }

    [Fact]
    public void A_gmail_throttle_is_googles_not_microsofts()
    {
        var said = MailTools.Slow(TimeSpan.FromSeconds(20), MailWords.Gmail);

        Assert.StartsWith("Google then started rate-limiting", said);
        Assert.DoesNotContain("Microsoft", said);
        Assert.StartsWith("Microsoft", MailTools.Slow(null));
    }

    [Fact]
    public void A_finished_gmail_clear_job_does_not_promise_trash_keeps_mail_forever()
    {
        var gmail = Conn("personal", ProviderKind.Google);

        var done = JobRunner.ClearSummary(200, 0, "linkedin", gmail);
        var paused = JobRunner.Paused(80, TimeSpan.FromSeconds(30), gmail);

        Assert.Contains("moved to Trash", done);
        Assert.Contains("30 days", done);
        Assert.DoesNotContain("until you empty it", done);
        Assert.Contains("because Google is rate-limiting", paused);

        Assert.Contains("until you empty it", JobRunner.ClearSummary(1, 0, "x", Conn("work", ProviderKind.Microsoft)));
    }

    [Fact]
    public void A_gmail_clear_scoped_to_trash_says_trash()
    {
        var (_, where, problem) = MailTools.Scope(GmailLabels, "trash", "personal", ProviderKind.Google);

        Assert.Null(problem);
        Assert.Equal(" in Trash", where);
    }

    [Fact]
    public void A_missing_gmail_label_is_called_a_label()
    {
        var said = MailTools.NoFolder("personal", "Taxes", GmailLabels, MailWords.Gmail);

        Assert.StartsWith("No label called \"Taxes\"", said);
        Assert.DoesNotContain("folder", said);
    }

    // ── create_event and Teams links ─────────────────────────────────────────

    private static (CalendarTools Tools, Stub<ICalendarService> Calendar) CalendarOver(
        Connection c, string? joinUrl = null)
    {
        var (store, _) = Stub<IConnectionStore>.Create(new()
        {
            ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<Connection>>([c]),
            ["FindByLabelAsync"] = _ => Task.FromResult<Connection?>(c),
        });

        var (calendar, stub) = Stub<ICalendarService>.Create(new()
        {
            ["TimeZoneAsync"] = _ => Task.FromResult(TimeZoneInfo.Utc),
            ["CreateAsync"] = a => Task.FromResult(new CalendarEvent
            {
                Id = "e1", ConnectionLabel = c.Label, Subject = "Sync", Organiser = "me",
                Start = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2026, 11, 10, 14, 30, 0, TimeSpan.Zero),
                IsAllDay = false, MyResponse = default, ShowAs = default, IsRecurring = false,
                IOrganised = true, Attendees = [], Body = "", OnlineMeetingUrl = joinUrl,
            }),
        });
        var (people, _) = Stub<IPeopleService>.Create([]);

        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("op2:uid", Guid.Empty.ToString())], "test")),
            },
        };

        return (new CalendarTools(http, store, calendar, people, new ItemIndex(), new PublicUrls("https://op2.example")), stub);
    }

    [Fact]
    public async Task A_teams_link_on_a_personal_account_is_refused_before_anyone_is_invited()
    {
        var (tools, calendar) = CalendarOver(Conn("home", ProviderKind.Microsoft, personal: true));

        var said = await tools.CreateEvent("Sync", "2026-11-10T14:00", invite: "a@example.com",
            online: true, connection: "home");

        Assert.Contains("personal Microsoft account", said);
        Assert.Contains("Nothing was created", said);
        Assert.Equal(0, calendar.Count("CreateAsync"));
    }

    [Fact]
    public async Task A_teams_link_asked_for_and_not_returned_is_said()
    {
        var (tools, _) = CalendarOver(Conn("work", ProviderKind.Microsoft), joinUrl: null);

        var said = await tools.CreateEvent("Sync", "2026-11-10T14:00", online: true, connection: "work");

        Assert.Contains("No Teams link came back", said);
    }

    [Fact]
    public async Task A_teams_link_that_came_back_is_still_reported_as_attached()
    {
        var (tools, _) = CalendarOver(Conn("work", ProviderKind.Microsoft), joinUrl: "https://teams.example/j/1");

        var said = await tools.CreateEvent("Sync", "2026-11-10T14:00", online: true, connection: "work");

        Assert.Contains("Teams link attached", said);
        Assert.DoesNotContain("No Teams link", said);
    }

    // ── Gmail's daily limits are not a thirty-second throttle ────────────────

    private sealed class Wire(HttpResponseMessage answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(answer);
    }

    private sealed class OneClient(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private sealed class Tokens : IGoogleTokenAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default) =>
            Task.FromResult<string?>("ya29.token");
    }

    private static GmailService Gmail(HttpStatusCode status, string json) =>
        new(new Tokens(), new OneClient(new Wire(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        })), NullLogger<GmailService>.Instance);

    [Fact]
    public async Task Googles_daily_project_quota_is_not_reported_as_a_short_throttle()
    {
        // Google's error guide: 403 dailyLimitExceeded, "your project reaches its API limit".
        var gmail = Gmail(HttpStatusCode.Forbidden, """
            {"error":{"code":403,"message":"Daily Limit Exceeded",
              "errors":[{"domain":"usageLimits","reason":"dailyLimitExceeded"}]}}
            """);

        var ex = await Assert.ThrowsAsync<MailException>(() => gmail.ListAsync(Conn("personal", ProviderKind.Google)));

        Assert.IsNotType<MailThrottledException>(ex);
        Assert.Contains("daily limit", ex.Message);
        Assert.Contains("won't help", ex.Message);
    }

    [Fact]
    public async Task Gmails_daily_sending_limit_is_not_reported_as_a_short_throttle()
    {
        // Google's error guide: a 429 "can occur due to daily per-user limits", e.g.
        // "Too many requests: User-rate limit exceeded (Mail sending)".
        var gmail = Gmail(HttpStatusCode.TooManyRequests, """
            {"error":{"code":429,"message":"User-rate limit exceeded (Mail sending)",
              "errors":[{"message":"User-rate limit exceeded (Mail sending)","domain":"usageLimits","reason":"rateLimitExceeded"}],
              "status":"RESOURCE_EXHAUSTED"}}
            """);

        var ex = await Assert.ThrowsAsync<MailException>(() => gmail.ListAsync(Conn("personal", ProviderKind.Google)));

        Assert.IsNotType<MailThrottledException>(ex);
        Assert.Contains("daily sending limit", ex.Message);
    }

    [Fact]
    public async Task An_ordinary_gmail_429_is_still_a_throttle()
    {
        var gmail = Gmail(HttpStatusCode.TooManyRequests, """
            {"error":{"code":429,"message":"Too many concurrent requests for user",
              "errors":[{"reason":"rateLimitExceeded"}],"status":"RESOURCE_EXHAUSTED"}}
            """);

        var ex = await Assert.ThrowsAsync<MailThrottledException>(() => gmail.ListAsync(Conn("personal", ProviderKind.Google)));
        Assert.Contains("Google is rate-limiting", ex.Message);
    }
}
