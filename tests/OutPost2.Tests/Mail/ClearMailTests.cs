using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Clearing out everything from one sender (OP2-224).
/// <para>
/// Filed because the alternative was unusable: Outlook's own "select all from sender" times
/// out on a 27,000-message inbox, and an agent doing it by hand spends a model turn per
/// message — slow, expensive, and with no way to tell whether it finished. The whole point
/// of this tool is that one call clears the lot.
/// </para>
/// <para>
/// Which means one call can also delete several hundred of the wrong messages, so the gate
/// is the real subject here: nothing is deleted without <c>confirm</c>, and the proposal has
/// to name the senders rather than only count them. The mechanics are driven through a
/// stubbed transport, because what matters is the conversation with Graph — how many deletes
/// went per request, and what was counted as success.
/// </para>
/// </summary>
public class ClearMailTests
{
    private static readonly Connection Work = new()
    {
        UserId = Guid.Empty, Label = "work", Provider = ProviderKind.Microsoft,
        Instance = "tenant", AuthKind = AuthKind.OAuth2, SealedCredential = "sealed",
        State = ConnectionState.Active,
    };

    private sealed record Call(string Url, string? Body);

    private sealed class Transport(Func<Call, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage r, CancellationToken ct)
        {
            var call = new Call(
                r.RequestUri!.AbsoluteUri,
                r.Content is null ? null : await r.Content.ReadAsStringAsync(ct));

            Calls.Add(call);
            return respond(call);
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Tokens : IMicrosoftConnectionServiceAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection c, CancellationToken ct = default) =>
            Task.FromResult<string?>("token");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>A page of messages, all from one sender.</summary>
    private static HttpResponseMessage Ids(int count, int from = 0, int? total = null) =>
        Json($$"""
            {
              {{(total is null ? "" : $"\"@odata.count\": {total},")}}
              "value": [{{string.Join(",", Enumerable.Range(from, count).Select(i => $$"""
                {
                  "id": "m{{i}}",
                  "subject": "Newsletter {{i}}",
                  "from": { "emailAddress": { "name": "LinkedIn", "address": "news@linkedin.com" } },
                  "receivedDateTime": "2026-0{{(i % 9) + 1}}-01T09:00:00Z",
                  "isRead": true,
                  "hasAttachments": false,
                  "bodyPreview": "p"
                }
                """))}}]
            }
            """);

    /// <summary>A batch reply where every sub-request succeeded.</summary>
    private static HttpResponseMessage AllDeleted(string requestBody)
    {
        var n = requestBody.Split("\"method\"").Length - 1;
        return Json($$"""
            { "responses": [{{string.Join(",", Enumerable.Range(1, n)
                .Select(i => $"{{\"id\":\"{i}\",\"status\":201}}"))}}] }
            """);
    }

    /// <summary>
    /// The waits are recorded rather than taken. Proving a backoff by actually sleeping
    /// through it makes the suite slow enough that people stop running it, and the thing
    /// worth asserting is the duration anyway — not that time passed.
    /// </summary>
    private static (MailService Service, Transport Wire, List<TimeSpan> Waits) Waiting(
        Func<Call, HttpResponseMessage> respond)
    {
        var wire = new Transport(respond);
        var waits = new List<TimeSpan>();

        var service = new MailService(
            new Tokens(), new OneClient(wire), NullLogger<MailService>.Instance,
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        return (service, wire, waits);
    }

    private static (MailService Service, Transport Wire) Service(Func<Call, HttpResponseMessage> respond)
    {
        var (service, wire, _) = Waiting(respond);
        return (service, wire);
    }

    // ── the filter ───────────────────────────────────────────────────────────

    [Fact]
    public void An_address_is_matched_exactly()
    {
        // contains() on a full address would make news@linkedin.com also match
        // xnews@linkedin.com.evil.example, and nobody says a whole address by accident.
        Assert.Equal(
            "from/emailAddress/address eq 'news@linkedin.com'",
            MailService.SenderFilter(new ClearTarget { From = "news@linkedin.com" }));
    }

    [Fact]
    public void A_fragment_is_matched_loosely_against_the_address()
    {
        // "linkedin" is what somebody says. Asked live, contains() on the address is
        // allowed; endsWith is refused outright, so a domain cannot be anchored.
        Assert.Equal(
            "contains(from/emailAddress/address,'linkedin')",
            MailService.SenderFilter(new ClearTarget { From = "linkedin" }));
    }

    [Fact]
    public void A_quote_in_the_address_cannot_break_out_of_the_filter()
    {
        // OData escapes a quote by doubling it. Unescaped, the literal closes early and
        // the rest is read as filter syntax — on a call whose job is deleting mail.
        var f = MailService.SenderFilter(new ClearTarget { From = "o'brien@example.com" });

        Assert.Equal("from/emailAddress/address eq 'o''brien@example.com'", f);
    }

    [Fact]
    public void A_cut_off_is_appended_in_utc_and_in_the_shape_graph_takes()
    {
        var f = MailService.SenderFilter(new ClearTarget
        {
            From = "news@linkedin.com",
            Before = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5)),
        });

        Assert.Equal(
            "from/emailAddress/address eq 'news@linkedin.com' "
            + "and receivedDateTime lt 2025-03-04T10:06:07Z", f);
    }

    // ── the deleting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Moves_are_sent_twenty_at_a_time()
    {
        // Measured against the live service: 20 is accepted, 21 comes back 400 "Number of
        // requests inside batch exceed the limit". This is the number that turns 500 model
        // turns into one.
        var served = 0;

        var (mail, wire) = Service(c =>
        {
            if (c.Url.Contains("$batch")) return AllDeleted(c.Body!);

            // A real mailbox shrinks as this runs, so the stub does too: each listing
            // returns what is left, up to whatever $top asked for.
            var n = Math.Min(20, 50 - served);
            served += n;
            return Ids(n, served - n);
        });

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "news@linkedin.com" });

        Assert.Equal(50, done.Deleted);
        Assert.Equal(0, done.Failed);

        var batches = wire.Calls.Where(c => c.Url.Contains("$batch")).ToList();
        Assert.Equal(3, batches.Count);

        // 20, 20, 10 — never 21.
        foreach (var b in batches)
            Assert.InRange(b.Body!.Split("\"method\"").Length - 1, 1, MailService.BatchLimit);
    }

    [Fact]
    public async Task Each_batch_moves_to_deleted_items_rather_than_deleting()
    {
        var served = 0;
        var (mail, wire) = Service(c =>
        {
            if (c.Url.Contains("$batch")) return AllDeleted(c.Body!);
            var n = served == 0 ? 3 : 0;
            served++;
            return Ids(n);
        });

        await mail.ClearAsync(Work, new ClearTarget { From = "news@linkedin.com" });

        var batch = wire.Calls.Single(c => c.Url.Contains("$batch"));

        var flat = batch.Body!.Replace(" ", "");

        // A move, not a DELETE, and this is the assertion that OP2-228 turns on. Graph's
        // DELETE answers 204 and puts the message in Recoverable Items, which is not where
        // anybody looks and is not what this tool told the user it had done.
        Assert.Contains("\"method\":\"POST\"", flat);
        Assert.Contains("/me/messages/m0/move", batch.Body);
        Assert.Contains("\"destinationId\":\"deleteditems\"", flat);

        Assert.DoesNotContain("\"method\":\"DELETE\"", flat);
        Assert.DoesNotContain("permanentDelete", batch.Body);
    }

    [Fact]
    public async Task A_sub_request_that_failed_is_counted_as_failed_not_as_deleted()
    {
        // A batch returns 200 with per-request statuses inside it, so "the call succeeded"
        // says nothing about the mail. Reporting 20 deleted when 2 are still there is the
        // exact failure this tool was built to stop.
        var served = 0;
        var (mail, _) = Service(c =>
        {
            if (c.Url.Contains("$batch"))
                return Json("""
                    { "responses": [
                      {"id":"1","status":201},
                      {"id":"2","status":404},
                      {"id":"3","status":201}
                    ] }
                    """);

            var n = served == 0 ? 3 : 0;
            served++;
            return Ids(n);
        });

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "news@linkedin.com" });

        Assert.Equal(2, done.Deleted);
        Assert.Equal(1, done.Failed);
    }

    [Fact]
    public async Task A_page_where_nothing_could_be_deleted_stops_rather_than_spinning()
    {
        // The list is re-asked each pass, so a page that refuses every delete would be
        // handed back for ever — an unkillable loop inside a tool call, in a car.
        var (mail, wire) = Service(c => c.Url.Contains("$batch")
            ? Json("""{ "responses": [{"id":"1","status":403}] }""")
            : Ids(1));

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "news@linkedin.com" });

        Assert.Equal(0, done.Deleted);
        Assert.Equal(1, done.Failed);
        Assert.True(wire.Calls.Count < 10, $"gave up after {wire.Calls.Count} calls");
    }

    [Fact]
    public async Task The_ceiling_is_honoured_and_what_is_left_is_reported()
    {
        var (mail, _) = Service(c => c.Url.Contains("$batch")
            ? AllDeleted(c.Body!)
            : c.Url.Contains("%24count=true") || c.Url.Contains("$count=true")
                ? Ids(1, total: 940)
                : Ids(20));

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "linkedin" }, ceiling: 40);

        Assert.Equal(40, done.Deleted);
        Assert.Equal(940, done.Remaining);
    }

    // ── throttling (OP2-238) ─────────────────────────────────────────────────

    private static HttpResponseMessage Throttle(int? retryAfterSeconds = 0)
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"error":{"code":"ApplicationThrottled","message":"Application is over its MailboxConcurrency limit."}}"""),
        };

        if (retryAfterSeconds is { } s) r.Headers.Add("Retry-After", s.ToString());
        return r;
    }

    [Fact]
    public async Task A_brief_throttle_is_waited_out_rather_than_handed_back()
    {
        // A short wait is worth absorbing: handing a two-second delay back to the caller
        // turns one tool call into two for no reason.
        var served = 0;
        var throttled = 0;

        var (mail, _, waits) = Waiting(c =>
        {
            if (c.Url.Contains("$batch"))
                return throttled++ == 0 ? Throttle(3) : AllDeleted(c.Body!);

            var n = served == 0 ? 2 : 0;
            served++;
            return Ids(n);
        });

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "linkedin" });

        Assert.Equal(2, done.Deleted);
        Assert.False(done.Throttled);
        Assert.Equal(2, throttled);

        // Waited exactly as long as Graph asked, not a guess of its own.
        Assert.Equal([TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public async Task A_long_throttle_stops_the_job_but_keeps_what_was_already_moved()
    {
        // The one that matters. Throwing here would lose the count of everything that
        // really did move, and the caller would start again from the beginning — doing the
        // work twice against the very limit that just refused it.
        var pages = 0;

        var (mail, _) = Service(c =>
        {
            if (c.Url.Contains("$batch"))
                // First batch fine, then a wait too long to sit on.
                return pages <= 1 ? AllDeleted(c.Body!) : Throttle(120);

            pages++;
            return Ids(20, (pages - 1) * 20);
        });

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "linkedin" });

        Assert.Equal(20, done.Deleted);
        Assert.True(done.Throttled);
        Assert.Equal(TimeSpan.FromSeconds(120), done.RetryAfter);
    }

    [Fact]
    public async Task A_throttle_with_no_retry_after_header_still_reports_itself()
    {
        var pages = 0;

        var (mail, _) = Service(c =>
        {
            if (c.Url.Contains("$batch"))
                return pages <= 1 ? AllDeleted(c.Body!) : Throttle(null);

            pages++;
            return Ids(20, (pages - 1) * 20);
        });

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "linkedin" });

        Assert.Equal(20, done.Deleted);
        Assert.True(done.Throttled);
    }

    [Fact]
    public async Task Recovering_also_keeps_what_it_managed_before_a_throttle()
    {
        var pages = 0;

        var (mail, _) = Service(c =>
        {
            if (c.Url.Contains("$batch"))
                return pages <= 1 ? AllDeleted(c.Body!) : Throttle(90);

            pages++;
            return Ids(20, (pages - 1) * 20);
        });

        var done = await mail.RecoverAsync(Work, "news@linkedin.com");

        Assert.Equal(20, done.Deleted);
        Assert.True(done.Throttled);
    }

    [Fact]
    public async Task A_throttle_on_an_ordinary_call_says_to_wait_and_how_long()
    {
        // Not a bulk job — a single read. Before this, a 429 anywhere else in the mail
        // service became "could not do that", which invites an immediate retry: the one
        // response that makes a throttle worse.
        var (mail, _) = Service(_ => Throttle(45));

        var ex = await Assert.ThrowsAsync<MailThrottledException>(
            () => mail.PreviewClearAsync(Work, new ClearTarget { From = "linkedin" }));

        Assert.Equal(45, ex.Seconds);
        Assert.Contains("rate-limiting", ex.Message);
        Assert.Contains("45 seconds", ex.Message);
        Assert.Contains("smaller pieces", ex.Message);
    }

    [Fact]
    public async Task A_throttle_is_still_a_mail_exception_so_every_tool_already_handles_it()
    {
        // Twenty tools catch MailException and speak it. If this did not derive from one,
        // every throttle outside the two tools that know about it would be a 500.
        var (mail, _) = Service(_ => Throttle(30));

        var ex = await Assert.ThrowsAsync<MailThrottledException>(
            () => mail.ListFoldersAsync(Work));

        Assert.IsAssignableFrom<MailException>(ex);
    }

    [Fact]
    public void A_throttle_report_names_a_wait_and_tells_the_caller_not_to_retry()
    {
        // Written for the agent as much as the listener: told only "that didn't finish",
        // an agent tries again at once.
        var said = MailTools.Slow(TimeSpan.FromSeconds(42));

        Assert.Contains("42 seconds", said);
        Assert.Contains("Nothing was lost", said);
        Assert.Contains("don't retry immediately", said);
    }

    [Fact]
    public void A_throttle_report_with_no_wait_given_still_names_a_number()
    {
        // "Wait a while" is not actionable. Something has to be said.
        var said = MailTools.Slow(null);

        Assert.Matches(@"\d+ seconds", said);
    }

    [Fact]
    public void A_wait_shorter_than_five_seconds_is_still_said_as_five()
    {
        // A one-second wait read out as "wait about 1 second" invites the retry that the
        // throttle is there to prevent, and the round trip costs more than that anyway.
        Assert.Contains("5 seconds", MailTools.Slow(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_preview_counts_without_deleting_anything()
    {
        var (mail, wire) = Service(_ => Ids(5, total: 412));

        var preview = await mail.PreviewClearAsync(Work, new ClearTarget { From = "linkedin" });

        Assert.Equal(412, preview.Matched);
        Assert.True(preview.Sampled);
        Assert.Equal("news@linkedin.com", Assert.Single(preview.Senders).Address);

        // The one assertion that matters: nothing was deleted to find that out.
        Assert.DoesNotContain(wire.Calls, c => c.Url.Contains("$batch"));
        Assert.All(wire.Calls, c => Assert.Null(c.Body));
    }

    // ── where a delete really goes (OP2-228) ─────────────────────────────────

    [Fact]
    public async Task Recovering_moves_mail_out_of_recoverable_items_and_into_the_inbox()
    {
        // The repair for the first version of this tool. It used DELETE, which answers 204
        // and leaves the message in Recoverable Items — so 188 of the user's messages went
        // somewhere he had been told they had not gone. They are still there, and this is
        // how they come back.
        var served = 0;
        var (mail, wire) = Service(c =>
        {
            if (c.Url.Contains("$batch")) return AllDeleted(c.Body!);
            var n = served == 0 ? 4 : 0;
            served++;
            return Ids(n);
        });

        var done = await mail.RecoverAsync(Work, "news@linkedin.com");

        Assert.Equal(4, done.Deleted);

        // Looked for in the right place…
        Assert.Contains(wire.Calls, c => c.Url.Contains("recoverableitemsdeletions"));

        // …and put back somewhere the user will see it.
        var batch = wire.Calls.Single(c => c.Url.Contains("$batch"));
        Assert.Contains("\"destinationId\":\"inbox\"", batch.Body!.Replace(" ", ""));
    }

    [Fact]
    public async Task The_recoverable_count_is_asked_of_recoverable_items()
    {
        var (mail, wire) = Service(_ => Ids(0, total: 188));

        Assert.Equal(188, await mail.RecoverableCountAsync(Work, "news@linkedin.com"));
        Assert.Contains(wire.Calls, c => c.Url.Contains("recoverableitemsdeletions"));
    }

    // ── what is in scope (OP2-227) ───────────────────────────────────────────

    private static MailFolder Folder(string path, string? wellKnown = null, string? id = null) => new()
    {
        Id = id ?? "id-" + path,
        Name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
        Path = path,
        WellKnownName = wellKnown,
    };

    private static readonly MailFolder[] Mailbox =
    [
        Folder("Inbox", "inbox"),
        Folder("Clutter"),
        Folder("Archive", "archive"),
        Folder("Inbox/Receipts"),
    ];

    [Fact]
    public void By_default_only_the_places_mail_lands_are_cleared()
    {
        // OP2-227: clearing everything also emptied the folder he had just filed that
        // sender's receipts into. Filing something is an act of keeping it, so a folder the
        // user put mail into is out of scope unless they ask for it by name. Clutter is in,
        // because Outlook files there on his behalf rather than on his instruction.
        var (scope, where, problem) = MailTools.Scope(Mailbox, null, "home");

        Assert.Null(problem);
        Assert.NotNull(scope);
        Assert.Equal(["id-Inbox", "id-Clutter"], scope!);
        Assert.Contains("Inbox and Clutter", where);
    }

    [Fact]
    public void All_is_the_way_to_ask_for_everything_and_it_says_so()
    {
        var (scope, where, problem) = MailTools.Scope(Mailbox, "all", "home");

        Assert.Null(problem);
        Assert.Null(scope);

        // Said out loud, because "everywhere" is the dangerous one.
        Assert.Contains("filed into", where);
    }

    [Fact]
    public void One_named_folder_narrows_it_to_that_folder()
    {
        var (scope, where, problem) = MailTools.Scope(Mailbox, "Receipts", "home");

        Assert.Null(problem);
        Assert.Equal(["id-Inbox/Receipts"], scope!);
        Assert.Contains("Inbox/Receipts", where);
    }

    [Fact]
    public void A_folder_that_is_not_there_is_refused_rather_than_widened()
    {
        // Falling back to the whole mailbox on an unrecognised folder name would turn a
        // narrowing instruction into the widest possible one.
        var (scope, _, problem) = MailTools.Scope(Mailbox, "Receits", "home");

        Assert.NotNull(problem);
        Assert.Null(scope);
    }

    // ── the gate ─────────────────────────────────────────────────────────────

    private static ClearPreview Preview(
        int matched, int everywhere = 0, IEnumerable<string>? skipped = null,
        params SenderTally[] senders) => new()
    {
        Matched = matched,
        Everywhere = everywhere == 0 ? matched : everywhere,
        SkippedFolderIds = skipped?.ToList() ?? [],
        Senders = senders,
        Sampled = (everywhere == 0 ? matched : everywhere) > senders.Sum(s => s.Count),
    };

    [Fact]
    public void A_proposal_says_the_count_the_sender_and_that_nothing_has_happened_yet()
    {
        var said = MailTools.Proposal("linkedin", "work", " in the Inbox and Clutter", "",
            Preview(412, senders: new SenderTally("LinkedIn", "news@linkedin.com", 412)), Mailbox);

        Assert.Contains("412 messages", said);
        Assert.Contains("news@linkedin.com", said);
        Assert.Contains("Nothing has been moved", said);
        Assert.Contains("Deleted Items", said);

        // The promise has to match what the code does, which is the lesson of OP2-228.
        Assert.Contains("until you empty it", said);
    }

    [Fact]
    public void Several_senders_are_all_named_because_that_is_the_misheard_name_caught()
    {
        // "Everything from Dave" matching three Daves has to be heard before several
        // hundred messages move. A bare count cannot say this.
        var said = MailTools.Proposal("dave", "work", " in the Inbox and Clutter", "",
            Preview(30, senders: [
                new SenderTally("Dave Smith", "dave@acme.example", 12),
                new SenderTally("Dave Jones", "dave.jones@other.example", 9),
                new SenderTally("Davenport Ltd", "accounts@davenport.example", 9)]), Mailbox);

        Assert.Contains("3 different senders", said);
        Assert.Contains("dave@acme.example", said);
        Assert.Contains("dave.jones@other.example", said);
        Assert.Contains("accounts@davenport.example", said);
    }

    [Fact]
    public void Mail_the_user_has_filed_is_counted_named_and_left_alone()
    {
        // The whole of OP2-227. He had filed a few receipts from this sender into a folder
        // minutes earlier; the clear-out took them and the dry run never mentioned that any
        // match was outside the Inbox.
        var said = MailTools.Proposal("linkedin", "home", " in the Inbox and Clutter", "",
            Preview(188, everywhere: 191, skipped: ["id-Inbox/Receipts"],
                senders: new SenderTally("LinkedIn", "news@linkedin.com", 50)), Mailbox);

        Assert.Contains("another 3", said);
        Assert.Contains("Inbox/Receipts", said);
        Assert.Contains("leaving those alone", said);
        Assert.Contains("everywhere", said);
    }

    [Fact]
    public void Nothing_filed_elsewhere_means_no_warning_to_wade_through()
    {
        var said = MailTools.Proposal("linkedin", "home", " in the Inbox and Clutter", "",
            Preview(188, senders: new SenderTally("LinkedIn", "news@linkedin.com", 50)), Mailbox);

        Assert.DoesNotContain("leaving those alone", said);
    }

    [Fact]
    public void A_scope_with_nothing_in_it_still_reports_what_is_filed_elsewhere()
    {
        // The case that reads worst if it is wrong: "nothing to delete" while 12 of that
        // sender's messages sit in a folder, which would send the user looking for a bug.
        var said = MailTools.Proposal("linkedin", "home", " in the Inbox and Clutter", "",
            Preview(0, everywhere: 12, skipped: ["id-Archive"]), Mailbox);

        Assert.Contains("Nothing from", said);
        Assert.Contains("another 12", said);
        Assert.Contains("Archive", said);
        Assert.DoesNotContain("Say go ahead", said);
    }

    [Fact]
    public void A_tally_from_a_sample_does_not_claim_to_be_the_whole_list()
    {
        var said = MailTools.Proposal("dave", "work", " in the Inbox and Clutter", "",
            Preview(900, senders: [
                new SenderTally("Dave Smith", "dave@acme.example", 30),
                new SenderTally("Dave Jones", "dave.jones@other.example", 20)]), Mailbox);

        Assert.Contains("sample", said);
    }

    [Fact]
    public void One_sender_needs_no_caveat_about_sampling()
    {
        var said = MailTools.Proposal("linkedin", "work", " in the Inbox and Clutter", "",
            Preview(412, senders: new SenderTally("LinkedIn", "news@linkedin.com", 50)), Mailbox);

        Assert.DoesNotContain("sample", said);
    }

    // ── the cut-off ──────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2025-01-01", "2025-01-01")]
    [InlineData("2024-06-15", "2024-06-15")]
    public void An_iso_date_is_taken_as_written(string said, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected + "T00:00:00Z"),
            MailTools.Older(said, Now));
    }

    [Theory]
    [InlineData("6 months ago", "2026-05-01")]
    [InlineData("older than 6 months", "2026-05-01")]
    [InlineData("a year ago", "2025-11-01")]
    [InlineData("two years ago", "2024-11-01")]
    [InlineData("30 days ago", "2026-10-02")]
    [InlineData("older than 2 weeks", "2026-10-18")]
    public void A_spoken_age_is_counted_back_from_now(string said, string expected)
    {
        var got = MailTools.Older(said, Now);

        Assert.NotNull(got);
        Assert.Equal(DateTimeOffset.Parse(expected + "T12:00:00Z"), got);
    }

    [Theory]
    [InlineData("whenever")]
    [InlineData("ages ago")]
    [InlineData("last Thursday")]
    [InlineData("0 months ago")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_unreadable_is_refused_rather_than_guessed(string? said)
    {
        // A date this guesses at is a date that deletes the wrong mail. There is no
        // sensible default for "before", so there is no default.
        Assert.Null(MailTools.Older(said, Now));
    }
}
