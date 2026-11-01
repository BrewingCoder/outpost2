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
                .Select(i => $"{{\"id\":\"{i}\",\"status\":204}}"))}}] }
            """);
    }

    private static (MailService Service, Transport Wire) Service(Func<Call, HttpResponseMessage> respond)
    {
        var wire = new Transport(respond);
        return (new MailService(new Tokens(), new OneClient(wire), NullLogger<MailService>.Instance), wire);
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
    public async Task Deletes_are_sent_twenty_at_a_time()
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
    public async Task Each_batch_is_a_batch_of_deletes()
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

        Assert.Contains("\"method\":\"DELETE\"", batch.Body!.Replace(" ", ""));
        Assert.Contains("/me/messages/m0", batch.Body);

        // A plain DELETE moves a message to Deleted Items. permanentDelete exists and is
        // deliberately not used: a misheard sender has to be recoverable.
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
                      {"id":"1","status":204},
                      {"id":"2","status":404},
                      {"id":"3","status":204}
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

    [Fact]
    public async Task Throttling_is_waited_out_rather_than_failing_the_whole_job()
    {
        // Bulk deleting is precisely what Graph throttles. Giving up on a 429 halfway
        // through would leave the mailbox half cleared with no way to tell.
        var served = 0;
        var throttled = 0;

        var (mail, _) = Service(c =>
        {
            if (c.Url.Contains("$batch"))
            {
                if (throttled++ == 0)
                {
                    var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    r.Headers.Add("Retry-After", "0");
                    return r;
                }
                return AllDeleted(c.Body!);
            }

            var n = served == 0 ? 2 : 0;
            served++;
            return Ids(n);
        });

        var done = await mail.ClearAsync(Work, new ClearTarget { From = "linkedin" });

        Assert.Equal(2, done.Deleted);
        Assert.Equal(2, throttled);
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

    // ── the gate ─────────────────────────────────────────────────────────────

    private static ClearPreview Preview(int matched, params SenderTally[] senders) => new()
    {
        Matched = matched,
        Senders = senders,
        Sampled = matched > senders.Sum(s => s.Count),
    };

    [Fact]
    public void A_proposal_says_the_count_the_sender_and_that_nothing_has_happened_yet()
    {
        var said = MailTools.Proposal("linkedin", "work", " in the whole mailbox", "",
            Preview(412, new SenderTally("LinkedIn", "news@linkedin.com", 412)));

        Assert.Contains("412 messages", said);
        Assert.Contains("news@linkedin.com", said);
        Assert.Contains("Nothing has been deleted", said);
        Assert.Contains("Deleted Items", said);
    }

    [Fact]
    public void Several_senders_are_all_named_because_that_is_the_misheard_name_caught()
    {
        // "Everything from Dave" matching three Daves has to be heard before several
        // hundred messages move. A bare count cannot say this.
        var said = MailTools.Proposal("dave", "work", " in the whole mailbox", "",
            Preview(30,
                new SenderTally("Dave Smith", "dave@acme.example", 12),
                new SenderTally("Dave Jones", "dave.jones@other.example", 9),
                new SenderTally("Davenport Ltd", "accounts@davenport.example", 9)));

        Assert.Contains("3 different senders", said);
        Assert.Contains("dave@acme.example", said);
        Assert.Contains("dave.jones@other.example", said);
        Assert.Contains("accounts@davenport.example", said);
    }

    [Fact]
    public void A_tally_from_a_sample_does_not_claim_to_be_the_whole_list()
    {
        var said = MailTools.Proposal("dave", "work", " in the whole mailbox", "",
            Preview(900,
                new SenderTally("Dave Smith", "dave@acme.example", 30),
                new SenderTally("Dave Jones", "dave.jones@other.example", 20)));

        Assert.Contains("sample", said);
    }

    [Fact]
    public void One_sender_needs_no_caveat_about_sampling()
    {
        var said = MailTools.Proposal("linkedin", "work", " in the whole mailbox", "",
            Preview(412, new SenderTally("LinkedIn", "news@linkedin.com", 50)));

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
