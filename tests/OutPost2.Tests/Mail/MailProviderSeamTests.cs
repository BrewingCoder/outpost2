using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Calendar;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web;
using OutPost2.Web.Mcp;
using OutPost2.Tests;

namespace OutPost2.Tests.Mail;

/// <summary>
/// A second mail provider behind <see cref="IMailService"/> (OP2-278) — the seam Gmail
/// (OP2-80) plugs into, proved here with a fake one before any Google code exists.
/// <para>
/// Driven through the real <see cref="MailTools"/>, because that is where an assumption
/// that "a mail connection is a Microsoft connection" would hide: the tools are what call
/// the router, and the zone lookup for exact timestamps is a Graph call made in the tool.
/// </para>
/// </summary>
public sealed class MailProviderSeamTests
{
    private static readonly Guid User = Guid.CreateVersion7();

    /// <summary>A mail provider that serves a fixed inbox and records what it was asked.</summary>
    private sealed class FakeProvider(ProviderKind kind, string tag) : IMailProvider
    {
        public ProviderKind Provider => kind;
        public List<string> Calls { get; } = [];
        public List<OutgoingMail> Sent { get; } = [];

        private MailSummary Summary(Connection c) => new()
        {
            Id = $"{tag}-1", ConnectionLabel = c.Label, Subject = $"Hello from {tag}",
            From = $"Someone <someone@{tag}.example>",
            ReceivedAt = new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.Zero),
            IsRead = false, HasAttachments = false, Preview = "",
        };

        public Task<IReadOnlyList<MailSummary>> ListAsync(Connection c, string folder = "inbox", int top = 25,
            bool unreadOnly = false, CancellationToken ct = default)
        {
            Calls.Add($"list {c.Label}");
            return Task.FromResult<IReadOnlyList<MailSummary>>([Summary(c)]);
        }

        public Task<MailSearchResult> SearchAsync(Connection c, string query, int top = 25, string? folderId = null,
            string? continueFrom = null, CancellationToken ct = default)
        {
            Calls.Add($"search {c.Label} {query}");
            return Task.FromResult(new MailSearchResult { Items = [Summary(c)] });
        }

        public Task<MailMessage?> ReadAsync(Connection c, string messageId, CancellationToken ct = default)
        {
            Calls.Add($"read {c.Label} {messageId}");
            return Task.FromResult<MailMessage?>(new MailMessage
            {
                Id = messageId, ConnectionLabel = c.Label, Subject = $"Hello from {tag}",
                From = $"someone@{tag}.example", To = ["me@example.com"], Cc = [],
                ReceivedAt = new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.Zero),
                IsRead = true, HasAttachments = false, Body = $"Body from {tag}.", QuotedHistoryStripped = true,
            });
        }

        public Task SendAsync(Connection c, OutgoingMail mail, CancellationToken ct = default)
        {
            Calls.Add($"send {c.Label}");
            Sent.Add(mail);
            return Task.CompletedTask;
        }

        // Not part of the seam under test.
        public Task<ReplySent> SendReplyAsync(Connection c, string m, ReplyKind k, string b, IReadOnlyList<string>? f = null,
            RecipientEdit? e = null, MailImportance i = MailImportance.Normal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ClearPreview> PreviewClearAsync(Connection c, ClearTarget t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ClearOutcome> ClearAsync(Connection c, ClearTarget t, int ceiling = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ClearOutcome> RecoverAsync(Connection c, string from, int ceiling = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RecoverableCountAsync(Connection c, string from, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkReadAsync(Connection c, string m, bool read, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> MoveAsync(Connection c, string m, string d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Connection c, CancellationToken ct = default)
        {
            // search_mail asks, to say which folder each hit is in.
            Calls.Add($"folders {c.Label}");
            return Task.FromResult<IReadOnlyList<MailFolder>>([]);
        }
        public Task<MailFolder> CreateFolderAsync(Connection c, string n, string? p = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MailFolder> RenameFolderAsync(Connection c, string f, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(Connection c, string f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MarkAsJunkAsync(Connection c, string m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task FlagAsync(Connection c, string m, bool f, DateTimeOffset? d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetCategoriesAsync(Connection c, string m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetCategoriesAsync(Connection c, string m, IReadOnlyList<string> cs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailCategory>?> ListCategoriesAsync(Connection c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MailCategory?> CreateCategoryAsync(Connection c, string n, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailAttachment>> ListAttachmentsAsync(Connection c, string m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream?> OpenAttachmentAsync(Connection c, string m, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static Connection Conn(string label, ProviderKind provider, bool isDefault = false)
    {
        var c = new Connection
        {
            UserId = User, Label = label, Provider = provider, Instance = "x",
            AuthKind = AuthKind.OAuth2, SealedCredential = "sealed", State = ConnectionState.Active,
            IsDefaultForProvider = isDefault,
        };
        foreach (var name in CapabilityCatalog.DeclaredFor(provider))
            c.Capabilities.Add(new Capability { Name = name, Granted = true, Enabled = true });
        return c;
    }

    private sealed class World
    {
        public required MailTools Tools { get; init; }
        public required FakeProvider Microsoft { get; init; }
        public required FakeProvider? Google { get; init; }
        public required Stub<ICalendarService> Calendar { get; init; }
    }

    private static World Build(bool googleRegistered = true, IMailboxZoneSource? googleZone = null,
        MailboxZone? graphZone = null)
    {
        var work = Conn("work", ProviderKind.Microsoft);
        var personal = Conn("personal", ProviderKind.Google);
        IReadOnlyList<Connection> all = [work, personal];

        var microsoft = new FakeProvider(ProviderKind.Microsoft, "outlook");
        var google = googleRegistered ? new FakeProvider(ProviderKind.Google, "gmail") : null;
        var router = new MailServiceRouter(google is null ? [microsoft] : [microsoft, google]);

        var (store, _) = Stub<IConnectionStore>.Create(new()
        {
            ["ListAsync"] = _ => Task.FromResult(all),
            ["FindByLabelAsync"] = a => Task.FromResult(all.FirstOrDefault(c => c.Label == (string)a[1]!)),
        });
        var (calendar, calendarStub) = Stub<ICalendarService>.Create(new()
        {
            ["TimeZoneAsync"] = _ => Task.FromResult(TimeZoneInfo.Utc),
            ["MailboxZoneAsync"] = _ => Task.FromResult(graphZone ?? new MailboxZone(TimeZoneInfo.Utc, true)),
        });
        var zones = new MailboxZones(
            googleZone is null ? [new GraphMailboxZoneSource(calendar)] : [new GraphMailboxZoneSource(calendar), googleZone],
            NullLogger<MailboxZones>.Instance);
        var (jobs, _) = Stub<IJobService>.Create([]);
        var (downloads, _) = Stub<IDownloadTicketService>.Create([]);

        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("op2:uid", User.ToString())], "test")),
            },
        };

        var tools = new MailTools(http, store, router, new ItemIndex(), new SearchContinuations(),
            new AttachmentExtractor(NullLogger<AttachmentExtractor>.Instance), jobs, zones, downloads,
            new PublicUrls("https://op2.example"));

        return new World { Tools = tools, Microsoft = microsoft, Google = google, Calendar = calendarStub };
    }

    // ── through the real tools ───────────────────────────────────────────────

    [Fact]
    public async Task Listing_every_mailbox_asks_each_provider_for_its_own()
    {
        var w = Build();

        var said = await w.Tools.ListMail("all");

        Assert.Contains("Hello from outlook", said);
        Assert.Contains("Hello from gmail", said);
        Assert.Equal(["list work"], w.Microsoft.Calls);
        Assert.Equal(["list personal"], w.Google!.Calls);
    }

    [Fact]
    public async Task Searching_every_mailbox_asks_each_provider_for_its_own()
    {
        var w = Build();

        var said = await w.Tools.SearchMail("invoice", "all");

        Assert.Contains("Hello from outlook", said);
        Assert.Contains("Hello from gmail", said);
        Assert.Contains("search work invoice", w.Microsoft.Calls);
        Assert.Contains("search personal invoice", w.Google!.Calls);
    }

    [Fact]
    public async Task Reading_a_gmail_handle_reads_from_gmail_and_never_asks_graph_for_its_zone()
    {
        var w = Build();
        await w.Tools.ListMail("personal");

        var said = await w.Tools.ReadMail("1");

        Assert.Contains("Body from gmail.", said);
        Assert.Contains("read personal gmail-1", w.Google!.Calls);
        Assert.Empty(w.Microsoft.Calls.Where(c => c.StartsWith("read")));

        // The exact-timestamp zone is a Microsoft Graph lookup; a Google connection must
        // never be handed to it.
        Assert.DoesNotContain(w.Calendar.Calls, c => c.Name is "TimeZoneAsync" or "MailboxZoneAsync"
            && ((Connection)c.Args[0]!).Provider == ProviderKind.Google);
    }

    [Fact]
    public async Task Reading_an_outlook_handle_still_reads_from_outlook()
    {
        var w = Build();
        await w.Tools.ListMail("work");

        var said = await w.Tools.ReadMail("1");

        Assert.Contains("Body from outlook.", said);
        Assert.Contains(w.Calendar.Calls, c => c.Name == "MailboxZoneAsync");
    }

    [Fact]
    public async Task Sending_from_a_gmail_connection_sends_through_gmail()
    {
        var w = Build();

        var said = await w.Tools.SendMail("a@example.com", "Hi", "Hello.", connection: "personal");

        Assert.StartsWith("Sent from personal", said);
        Assert.Single(w.Google!.Sent);
        Assert.Empty(w.Microsoft.Sent);
    }

    [Fact]
    public async Task A_provider_with_no_implementation_is_said_plainly_rather_than_thrown()
    {
        // Gmail connections can exist before Gmail mail does (or a build can lack it).
        var w = Build(googleRegistered: false);

        var listed = await w.Tools.ListMail("personal");
        Assert.Contains("can't do mail on it yet", listed);

        var sent = await w.Tools.SendMail("a@example.com", "Hi", "Hello.", connection: "personal");
        Assert.Contains("can't do mail on it yet", sent);
    }

    // ── the routers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_router_names_the_connection_and_provider_it_cannot_serve()
    {
        var router = new MailServiceRouter([new FakeProvider(ProviderKind.Microsoft, "outlook")]);

        var ex = await Assert.ThrowsAsync<MailProviderNotSupportedException>(
            () => router.ListAsync(Conn("personal", ProviderKind.Google)));

        Assert.Equal("personal is a Gmail connection, and Outpost can't do mail on it yet.", ex.Message);
        Assert.IsAssignableFrom<MailException>(ex);
    }

    [Fact]
    public void Two_implementations_for_one_provider_is_a_startup_error_not_a_coin_toss()
    {
        Assert.Throws<InvalidOperationException>(() => new MailServiceRouter(
            [new FakeProvider(ProviderKind.Microsoft, "a"), new FakeProvider(ProviderKind.Microsoft, "b")]));
    }

    [Fact]
    public async Task Sending_with_attachments_on_an_unsupported_provider_is_a_plain_non_retryable_failure()
    {
        var router = new LargeAttachmentSenderRouter([]);

        var outcome = await router.SendAsync(Conn("personal", ProviderKind.Google),
            new OutgoingMail { To = ["a@example.com"], Subject = "s", Body = "b" }, []);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Retryable);
        Assert.False(outcome.MaybeSent);
        Assert.Contains("Nothing was sent", outcome.Error);
    }

    // ── what a Google connection may be chosen for ───────────────────────────

    [Fact]
    public void A_google_connection_declares_mail_and_nothing_else()
    {
        Assert.Equal(
            [Capabilities.MailRead, Capabilities.MailWrite, Capabilities.MailSend],
            CapabilityCatalog.DeclaredFor(ProviderKind.Google));
    }

    [Theory]
    [InlineData(Capabilities.CalendarRead)]
    [InlineData(Capabilities.CalendarWrite)]
    [InlineData(Capabilities.PeopleRead)]
    [InlineData(Capabilities.ContactsWrite)]
    [InlineData(Capabilities.ChatRead)]
    [InlineData(Capabilities.ChatSend)]
    public void The_microsoft_only_tools_can_never_pick_a_google_connection(string capability)
    {
        var google = Conn("personal", ProviderKind.Google, isDefault: true);

        Assert.False(ConnectionResolver.Resolve([google], capability, null, isWrite: false).IsActionable);
        Assert.False(ConnectionResolver.Resolve([google], capability, "personal", isWrite: false).IsActionable);
    }

    [Fact]
    public void A_default_mailbox_on_each_provider_means_ask_rather_than_pick_one()
    {
        // Defaults are per provider, so with mail on two providers there can be two.
        var work = Conn("work", ProviderKind.Microsoft, isDefault: true);
        var personal = Conn("personal", ProviderKind.Google, isDefault: true);

        var both = ConnectionResolver.Resolve([work, personal], Capabilities.MailRead, null, isWrite: false);
        Assert.Equal(ResolutionKind.Ambiguous, both.Kind);

        var one = ConnectionResolver.Resolve(
            [work, Conn("personal", ProviderKind.Google)], Capabilities.MailRead, null, isWrite: false);
        Assert.Equal(ResolutionKind.Single, one.Kind);
        Assert.Equal("work", one.Connection!.Label);
    }

    // ── whose zone a time prints in (OP2-285) ────────────────────────────────

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    /// <summary>A Google zone source with a fixed answer, recording that it was asked.</summary>
    private sealed class FixedZone(Func<MailboxZone> answer) : IMailboxZoneSource
    {
        public ProviderKind Provider => ProviderKind.Google;
        public List<string> Asked { get; } = [];

        public Task<MailboxZone> LookupAsync(Connection c, CancellationToken ct = default)
        {
            Asked.Add(c.Label);
            return Task.FromResult(answer());
        }
    }

    [Fact]
    public async Task A_microsoft_mailbox_still_prints_in_its_graph_zone()
    {
        var w = Build(graphZone: new MailboxZone(Eastern, true));

        var said = await w.Tools.ListMail("work");

        Assert.Contains("2026-11-03 04:30 -05:00", said);
        Assert.DoesNotContain("mailbox zone unavailable", said);
    }

    [Fact]
    public async Task A_gmail_mailbox_prints_in_its_own_zone_from_its_own_provider()
    {
        var google = new FixedZone(() => new MailboxZone(Eastern, true));
        var w = Build(googleZone: google);

        var listed = await w.Tools.ListMail("personal");
        var read = await w.Tools.ReadMail("1");
        var found = await w.Tools.SearchMail("hello", "personal");

        foreach (var said in new[] { listed, read, found })
        {
            Assert.Contains("-05:00", said);
            Assert.DoesNotContain("mailbox zone unavailable", said);
        }
        Assert.Contains("personal", google.Asked);
        Assert.DoesNotContain(w.Calendar.Calls, c => ((Connection)c.Args[0]!).Provider == ProviderKind.Google);
    }

    [Fact]
    public async Task A_gmail_zone_lookup_that_throws_still_reads_and_labels_the_utc_instant()
    {
        var w = Build(googleZone: new FixedZone(() => throw new HttpRequestException("Google is down")));

        var listed = await w.Tools.ListMail("personal");
        var read = await w.Tools.ReadMail("1");

        Assert.Contains("Hello from gmail", listed);
        Assert.Contains("2026-11-03 09:30 +00:00 — UTC, mailbox zone unavailable", listed);
        Assert.Contains("Body from gmail.", read);
        Assert.Contains("mailbox zone unavailable", read);
    }

    [Fact]
    public async Task A_mailbox_genuinely_set_to_utc_is_not_labelled_unknown()
    {
        var w = Build(googleZone: new FixedZone(() => new MailboxZone(TimeZoneInfo.Utc, true)));

        var said = await w.Tools.ListMail("personal");

        Assert.Contains("2026-11-03 09:30 +00:00", said);
        Assert.DoesNotContain("mailbox zone unavailable", said);
    }

    [Fact]
    public async Task Listing_every_mailbox_labels_only_the_one_whose_zone_could_not_be_had()
    {
        var w = Build(graphZone: new MailboxZone(Eastern, true),
            googleZone: new FixedZone(() => MailboxZone.Unavailable));

        var said = await w.Tools.ListMail("all");

        var lines = said.Split('\n');
        Assert.Contains(lines, l => l.Contains("Hello from outlook") && l.Contains("-05:00") && !l.Contains("unavailable"));
        Assert.Contains(lines, l => l.Contains("Hello from gmail") && l.Contains("unavailable"));
    }
}
