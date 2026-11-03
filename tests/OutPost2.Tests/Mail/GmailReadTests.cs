using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Reading Gmail (OP2-280): listing, searching with paging, reading MIME, attachments and
/// labels as folders — against fixtures shaped like the Gmail API v1's documented
/// responses. A live run against a real account is OP2-284.
/// </summary>
public sealed class GmailReadTests
{
    private static readonly Connection Personal = new()
    {
        UserId = Guid.Empty, Label = "personal", Provider = ProviderKind.Google, Instance = "google",
        AuthKind = AuthKind.OAuth2, SealedCredential = "sealed", State = ConnectionState.Active,
    };

    // ── a fake Gmail ─────────────────────────────────────────────────────────

    private sealed class Gmail(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            var url = Uri.UnescapeDataString(r.RequestUri!.PathAndQuery.Replace("/gmail/v1/users/me/", ""));
            lock (Urls) Urls.Add(url);
            return Task.FromResult(respond(url));
        }
    }

    private sealed class OneClient(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private sealed class Tokens(string? token = "ya29.token") : IGoogleTokenAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default) => Task.FromResult(token);
    }

    private static (GmailService Mail, Gmail Wire) Build(Func<string, HttpResponseMessage> respond, string? token = "ya29.token")
    {
        var wire = new Gmail(respond);
        return (new GmailService(new Tokens(token), new OneClient(wire), NullLogger<GmailService>.Instance), wire);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code, string json = "{}") =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string B64Url(string text, Encoding? enc = null) => B64Url((enc ?? Encoding.UTF8).GetBytes(text));
    private static string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string H(string name, string value) =>
        $$"""{"name":{{JsonSerializer.Serialize(name)}},"value":{{JsonSerializer.Serialize(value)}}}""";

    // ── fixtures, in the Gmail API's documented shape ────────────────────────

    private const string TwoIds = """
        {"messages":[{"id":"18c1","threadId":"18c1"},{"id":"18c2","threadId":"18c0"}],"resultSizeEstimate":2}
        """;

    private static string Metadata(string id, string subject, string from, long internalDate, string labels,
        string mimeType = "multipart/alternative", string snippet = "", string extraHeaders = "") => $$"""
        {"id":"{{id}}","threadId":"{{id}}","labelIds":[{{labels}}],"snippet":{{JsonSerializer.Serialize(snippet)}},
         "sizeEstimate":4821,"historyId":"12","internalDate":"{{internalDate}}",
         "payload":{"partId":"","mimeType":"{{mimeType}}","filename":"",
           "headers":[{{H("From", from)}},{{H("Subject", subject)}},{{H("Date", "Mon, 3 Nov 2026 09:30:00 +0000")}}{{extraHeaders}}]} }
        """;

    private static HttpResponseMessage Inbox(string url) => url switch
    {
        _ when url.StartsWith("messages?") => Ok(TwoIds),
        _ when url.StartsWith("messages/18c1?") => Ok(Metadata("18c1", "Thursday", "Ann Lee <ann@example.com>",
            1762162200000, "\"UNREAD\",\"IMPORTANT\",\"CATEGORY_PERSONAL\",\"INBOX\"",
            snippet: "Can we move Thursday&#39;s call?", extraHeaders: "," + H("X-Priority", "1 (Highest)"))),
        _ when url.StartsWith("messages/18c2?") => Ok(Metadata("18c2", "Invoice 4471", "billing@vendor.example",
            1762075800000, "\"INBOX\",\"Label_12\"", mimeType: "multipart/mixed")),
        _ => Status(HttpStatusCode.NotFound),
    };

    // ── listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_inbox_lists_with_what_each_message_is()
    {
        var (mail, wire) = Build(Inbox);

        var items = await mail.ListAsync(Personal, "inbox", top: 10);

        Assert.Contains("messages?maxResults=10&labelIds=INBOX", wire.Urls);
        Assert.Equal(["18c1", "18c2"], items.Select(i => i.Id));

        var first = items[0];
        Assert.Equal("Thursday", first.Subject);
        Assert.Equal("Ann Lee <ann@example.com>", first.From);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762162200000), first.ReceivedAt);
        Assert.False(first.IsRead);
        Assert.Equal("Can we move Thursday's call?", first.Preview);   // snippet arrives HTML-escaped
        Assert.Equal("INBOX", first.FolderId);
        Assert.Equal(MailImportance.High, first.Importance);
        Assert.False(first.HasAttachments);

        Assert.True(items[1].IsRead);
        Assert.True(items[1].HasAttachments);                          // multipart/mixed
        Assert.Equal("personal", items[1].ConnectionLabel);
    }

    [Fact]
    public async Task Unread_only_asks_gmail_for_unread()
    {
        var (mail, wire) = Build(Inbox);
        await mail.ListAsync(Personal, "inbox", top: 5, unreadOnly: true);
        Assert.Contains(wire.Urls, u => u.StartsWith("messages?") && u.Contains("q=is:unread") && u.Contains("labelIds=INBOX"));
    }

    [Fact]
    public async Task An_empty_inbox_has_no_messages_key_and_lists_nothing()
    {
        var (mail, _) = Build(_ => Ok("""{"resultSizeEstimate":0}"""));
        Assert.Empty(await mail.ListAsync(Personal));
    }

    [Fact]
    public async Task A_message_deleted_between_the_list_and_the_fetch_is_dropped_not_fatal()
    {
        var (mail, _) = Build(url => url.StartsWith("messages/18c1?") ? Status(HttpStatusCode.NotFound) : Inbox(url));
        Assert.Equal(["18c2"], (await mail.ListAsync(Personal)).Select(i => i.Id));
    }

    // ── searching ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_search_pages_with_its_own_query_and_labels_carried_in_the_continuation()
    {
        var (mail, wire) = Build(url =>
        {
            if (url.StartsWith("messages?") && !url.Contains("pageToken"))
                return Ok("""{"messages":[{"id":"18c1","threadId":"18c1"}],"nextPageToken":"09391","resultSizeEstimate":2}""");
            if (url.StartsWith("messages?") && url.Contains("pageToken=09391"))
                return Ok("""{"messages":[{"id":"18c2","threadId":"18c0"}],"resultSizeEstimate":2}""");
            return Inbox(url);
        });

        var first = await mail.SearchAsync(Personal, "from:billing invoice", top: 1, folderId: "Label_12");
        Assert.True(first.More);
        Assert.NotNull(first.Continuation);
        Assert.Equal(["18c1"], first.Items.Select(i => i.Id));

        // A different query on the resume is ignored: the token is only valid with its own.
        var second = await mail.SearchAsync(Personal, "something else", top: 1, continueFrom: first.Continuation);
        Assert.False(second.More);
        Assert.Null(second.Continuation);
        Assert.Equal(["18c2"], second.Items.Select(i => i.Id));

        var resumed = wire.Urls.Single(u => u.Contains("pageToken"));
        Assert.Contains("q=from:billing invoice", resumed);
        Assert.Contains("labelIds=Label_12", resumed);
        Assert.DoesNotContain("something else", resumed);
    }

    [Theory]
    [InlineData("inbox", "labelIds=INBOX")]
    [InlineData("deleteditems", "labelIds=TRASH")]
    [InlineData("junkemail", "labelIds=SPAM")]
    [InlineData("sentitems", "labelIds=SENT")]
    [InlineData("Label_12", "labelIds=Label_12")]
    [InlineData("archive", "-in:inbox")]
    public async Task A_scoped_search_asks_gmail_for_that_label_or_for_archive_by_query(string folder, string expected)
    {
        var (mail, wire) = Build(_ => Ok("""{"resultSizeEstimate":0}"""));
        await mail.SearchAsync(Personal, "invoice", folderId: folder);
        Assert.Contains(wire.Urls, u => u.StartsWith("messages?") && u.Contains(expected));
    }

    [Fact]
    public void A_search_hit_is_placed_in_the_inbox_first_then_a_user_label_then_a_system_one()
    {
        Assert.Equal("INBOX", GmailService.PrimaryLabel(["IMPORTANT", "Label_3", "INBOX"]));
        Assert.Equal("Label_3", GmailService.PrimaryLabel(["IMPORTANT", "Label_3", "SENT"]));
        Assert.Equal("SENT", GmailService.PrimaryLabel(["SENT", "IMPORTANT"]));
        Assert.Null(GmailService.PrimaryLabel(["IMPORTANT", "CATEGORY_UPDATES"]));   // archived
        Assert.Null(GmailService.PrimaryLabel(null));
    }

    // ── reading ──────────────────────────────────────────────────────────────

    private static HttpResponseMessage Full(string payload, string labels = "\"INBOX\"") => Ok($$"""
        {"id":"18c1","threadId":"18c1","labelIds":[{{labels}}],"snippet":"","internalDate":"1762162200000","payload":{{payload}}}
        """);

    private static string Part(string partId, string mimeType, string data, string charset = "UTF-8",
        string filename = "", string extraHeaders = "") => $$"""
        {"partId":"{{partId}}","mimeType":"{{mimeType}}","filename":"{{filename}}",
         "headers":[{{H("Content-Type", $"{mimeType}; charset=\"{charset}\"")}}{{extraHeaders}}],
         "body":{"size":{{data.Length}},"data":"{{data}}"} }
        """;

    private static string Envelope(string mimeType, string parts, string extraHeaders = "") => $$"""
        {"partId":"","mimeType":"{{mimeType}}","filename":"",
         "headers":[{{H("From", "Ann Lee <ann@example.com>")}},{{H("To", "\"Lee, Jordan\" <jordan@example.com>, me@example.com")}},
                    {{H("Cc", "Sam <sam@example.com>")}},{{H("Subject", "Thursday")}},
                    {{H("Date", "Mon, 3 Nov 2026 09:29:41 +0000 (UTC)")}}{{extraHeaders}}],
         "body":{"size":0},"parts":[{{parts}}]}
        """;

    [Fact]
    public async Task A_message_reads_with_its_envelope_and_its_html_body_preferred()
    {
        var html = "<div dir=\"ltr\">Can we move <b>Thursday</b>?</div>";
        var (mail, wire) = Build(_ => Full(Envelope("multipart/alternative",
            Part("0", "text/plain", B64Url("Can we move *Thursday*?")) + "," + Part("1", "text/html", B64Url(html)),
            "," + H("Importance", "high"))));

        var m = await mail.ReadAsync(Personal, "18c1");

        Assert.Contains("messages/18c1?format=full", wire.Urls);
        Assert.NotNull(m);
        Assert.Equal("Thursday", m.Subject);
        Assert.Equal("Ann Lee <ann@example.com>", m.From);
        Assert.Equal(["\"Lee, Jordan\" <jordan@example.com>", "me@example.com"], m.To);   // the comma in the name holds
        Assert.Equal(["Sam <sam@example.com>"], m.Cc);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 29, 41, TimeSpan.Zero), m.SentAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1762162200000), m.ReceivedAt);
        Assert.Equal(MailImportance.High, m.Importance);
        Assert.Contains("Can we move", m.Body);
        Assert.Contains("Thursday", m.Body);
        Assert.DoesNotContain("<b>", m.Body);
        Assert.DoesNotContain("*Thursday*", m.Body);   // the HTML alternative won
    }

    [Fact]
    public async Task A_message_that_has_gone_reads_as_null()
    {
        var (mail, _) = Build(_ => Status(HttpStatusCode.NotFound,
            """{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND"}}"""));
        Assert.Null(await mail.ReadAsync(Personal, "gone"));
    }

    [Fact]
    public async Task Gmails_quoted_history_is_cut_from_an_html_reply_and_says_so()
    {
        var html = "<div dir=\"ltr\">Yes, 3pm works.</div><br><div class=\"gmail_quote gmail_quote_container\">"
                 + "<div dir=\"ltr\" class=\"gmail_attr\">On Mon, 3 Nov 2026 at 09:00, Ann wrote:<br></div>"
                 + "<blockquote class=\"gmail_quote\">Can we move Thursday?</blockquote></div>";
        var (mail, _) = Build(_ => Full(Envelope("multipart/alternative", Part("1", "text/html", B64Url(html)))));

        var m = (await mail.ReadAsync(Personal, "18c1"))!;

        Assert.Contains("Yes, 3pm works.", m.Body);
        Assert.DoesNotContain("Can we move Thursday", m.Body);
        Assert.True(m.QuotedHistoryStripped);
    }

    [Fact]
    public void A_bare_forward_keeps_its_content_because_the_quote_is_the_message()
    {
        var html = "<div class=\"gmail_quote\">---------- Forwarded message ---------<br>The contract is attached.</div>";
        var (kept, stripped) = GmailService.StripHtmlQuote(html);
        Assert.Equal(html, kept);
        Assert.False(stripped);
    }

    [Fact]
    public void Plain_text_replies_lose_their_history_however_the_client_wrapped_it()
    {
        var wrapped = "Sounds good.\n\nOn Mon, 3 Nov 2026 at 09:00, Ann Lee <ann@example.com>\nwrote:\n> Can we move Thursday?\n";
        Assert.Equal(("Sounds good.", true), GmailService.StripPlainQuote(wrapped));

        var outlook = "Fine by me.\r\n-----Original Message-----\r\nFrom: Ann\r\nCan we move Thursday?";
        Assert.Equal(("Fine by me.", true), GmailService.StripPlainQuote(outlook));

        var trailing = "Agreed.\n> earlier\n> more earlier\n";
        Assert.Equal(("Agreed.", true), GmailService.StripPlainQuote(trailing));

        Assert.Equal(("Just a note.", false), GmailService.StripPlainQuote("Just a note."));
        Assert.Equal(("> all quote", false), GmailService.StripPlainQuote("> all quote"));
    }

    [Theory]
    [InlineData("windows-1252")]
    [InlineData("iso-8859-1")]
    public async Task A_body_in_an_old_charset_reads_correctly(string charset)
    {
        var enc = Encoding.GetEncoding(charset);
        var (mail, _) = Build(_ => Full(Envelope("multipart/alternative",
            Part("0", "text/plain", B64Url("Café at 3 — naïve résumé", enc), charset))));

        var m = (await mail.ReadAsync(Personal, "18c1"))!;
        Assert.Contains("Café", m.Body);
        Assert.Contains("résumé", m.Body);
    }

    [Theory]
    [InlineData("=?UTF-8?B?Q2Fmw6kgYXQgMw==?=", "Café at 3")]
    [InlineData("=?iso-8859-1?Q?Caf=E9_at_3?=", "Café at 3")]
    [InlineData("=?UTF-8?Q?Re:_?= =?UTF-8?Q?Caf=C3=A9?=", "Re: Café")]   // adjacent words join
    [InlineData("Plain subject", "Plain subject")]
    public void Encoded_header_words_are_decoded(string raw, string expected)
    {
        Assert.Equal(expected, GmailService.Decode(raw));
    }

    [Theory]
    [InlineData("Mon, 3 Nov 2026 09:29:41 +0000", 9, 0)]
    [InlineData("Mon, 3 Nov 2026 04:29:41 -0500 (EST)", 4, -5)]
    [InlineData("3 Nov 2026 09:29:41 GMT", 9, 0)]
    public void The_date_header_is_read_with_its_offset(string raw, int hour, int offsetHours)
    {
        var d = GmailService.ParseDate(raw);
        Assert.NotNull(d);
        Assert.Equal(hour, d.Value.Hour);
        Assert.Equal(TimeSpan.FromHours(offsetHours), d.Value.Offset);
    }

    [Fact]
    public void An_unreadable_date_is_no_sent_time_rather_than_a_wrong_one()
    {
        Assert.Null(GmailService.ParseDate("yesterday-ish"));
        Assert.Null(GmailService.ParseDate(null));
    }

    // ── attachments ──────────────────────────────────────────────────────────

    /// <summary>
    /// multipart/mixed: the text (as an alternative), a PDF stored separately, an inline
    /// image, and a forwarded message that has an attachment of its own.
    /// </summary>
    private static string Mixed(string pdfAttachmentId) => Envelope("multipart/mixed",
        $$"""
        {"partId":"0","mimeType":"multipart/alternative","filename":"","headers":[],"body":{"size":0},
         "parts":[{{Part("0.0", "text/plain", B64Url("See attached."))}},{{Part("0.1", "text/html", B64Url("<p>See attached.</p>"))}}]},
        {"partId":"1","mimeType":"application/pdf","filename":"Invoice 4471.pdf",
         "headers":[{{H("Content-Disposition", "attachment; filename=\"Invoice 4471.pdf\"")}}],
         "body":{"attachmentId":"{{pdfAttachmentId}}","size":48213} },
        {"partId":"2","mimeType":"image/png","filename":"logo.png",
         "headers":[{{H("Content-Disposition", "inline; filename=\"logo.png\"")}}],
         "body":{"size":5,"data":"{{B64Url([1, 2, 3, 4, 5])}}"} },
        {"partId":"3","mimeType":"message/rfc822","filename":"Earlier thread.eml","headers":[],"body":{"size":9000},
         "parts":[{"partId":"3.0","mimeType":"multipart/mixed","filename":"","headers":[],"body":{"size":0},
           "parts":[{{Part("3.0.0", "text/plain", B64Url("THE FORWARDED BODY"))}},
                    {"partId":"3.0.1","mimeType":"application/pdf","filename":"inner.pdf","headers":[],
                     "body":{"attachmentId":"ANGjdJ-inner","size":100} }]}]}
        """);

    [Fact]
    public async Task Attachments_are_the_messages_own_parts_by_stable_part_id()
    {
        var (mail, _) = Build(_ => Full(Mixed("ANGjdJ-first")));

        var list = await mail.ListAttachmentsAsync(Personal, "18c1");

        Assert.Equal(["1", "2", "3"], list.Select(a => a.Id));
        Assert.Equal(["Invoice 4471.pdf", "logo.png", "Earlier thread.eml"], list.Select(a => a.Name));
        Assert.Equal(48213, list[0].SizeBytes);
        Assert.Equal("application/pdf", list[0].ContentType);
        Assert.True(list[2].IsEmbeddedItem);
        Assert.DoesNotContain(list, a => a.Name == "inner.pdf");   // the forwarded message's own attachment
    }

    [Fact]
    public async Task A_mixed_message_reads_its_own_text_not_the_forwarded_ones()
    {
        var (mail, _) = Build(_ => Full(Mixed("ANGjdJ-first")));

        var m = (await mail.ReadAsync(Personal, "18c1"))!;

        Assert.Contains("See attached.", m.Body);
        Assert.DoesNotContain("THE FORWARDED BODY", m.Body);
        Assert.True(m.HasAttachments);
    }

    [Fact]
    public async Task Opening_an_attachment_uses_the_attachment_id_gmail_issues_now()
    {
        // Gmail hands out a fresh attachmentId on every fetch of the message, so the one
        // seen when the list was made is not the one to ask for later.
        var fetches = 0;
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7 bytes");
        var (mail, wire) = Build(url =>
        {
            if (url.StartsWith("messages/18c1?format=full")) return Full(Mixed($"ANGjdJ-fetch{++fetches}"));
            if (url.StartsWith("messages/18c1/attachments/ANGjdJ-fetch"))
                return Ok($$"""{"size":{{pdf.Length}},"data":"{{B64Url(pdf)}}"}""");
            return Status(HttpStatusCode.NotFound);
        });

        var listed = await mail.ListAttachmentsAsync(Personal, "18c1");
        await using var bytes = await mail.OpenAttachmentAsync(Personal, "18c1", listed[0].Id);

        Assert.NotNull(bytes);
        using var ms = new MemoryStream();
        await bytes.CopyToAsync(ms);
        Assert.Equal(pdf, ms.ToArray());
        Assert.Contains("messages/18c1/attachments/ANGjdJ-fetch2", wire.Urls);   // not fetch1
    }

    [Fact]
    public async Task A_small_attachment_comes_inline_with_no_second_fetch()
    {
        var (mail, wire) = Build(_ => Full(Mixed("unused")));

        await using var bytes = await mail.OpenAttachmentAsync(Personal, "18c1", "2");

        using var ms = new MemoryStream();
        await bytes!.CopyToAsync(ms);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, ms.ToArray());
        Assert.DoesNotContain(wire.Urls, u => u.Contains("/attachments/"));
    }

    [Fact]
    public async Task An_attachment_part_that_is_not_there_opens_as_null()
    {
        var (mail, _) = Build(_ => Full(Mixed("x")));
        Assert.Null(await mail.OpenAttachmentAsync(Personal, "18c1", "9"));
    }

    // ── labels as folders ────────────────────────────────────────────────────

    [Fact]
    public async Task Labels_are_folders_with_real_counts_and_nesting()
    {
        var (mail, wire) = Build(url => url switch
        {
            "labels" => Ok("""
                {"labels":[
                  {"id":"INBOX","name":"INBOX","type":"system"},
                  {"id":"SENT","name":"SENT","type":"system"},
                  {"id":"TRASH","name":"TRASH","type":"system"},
                  {"id":"SPAM","name":"SPAM","type":"system"},
                  {"id":"STARRED","name":"STARRED","type":"system"},
                  {"id":"UNREAD","name":"UNREAD","type":"system"},
                  {"id":"CATEGORY_PROMOTIONS","name":"CATEGORY_PROMOTIONS","type":"system"},
                  {"id":"Label_1","name":"Receipts","type":"user"},
                  {"id":"Label_2","name":"Receipts/2026","type":"user"},
                  {"id":"Label_3","name":"Receipts/2026/Travel","type":"user"}]}
                """),
            _ when url.StartsWith("labels/") => Ok($$"""
                {"id":"{{url["labels/".Length..]}}","name":"{{url["labels/".Length..] switch
                    { "Label_1" => "Receipts", "Label_2" => "Receipts/2026", "Label_3" => "Receipts/2026/Travel", var x => x }}}",
                 "type":"{{(url.Contains("Label_") ? "user" : "system")}}","messagesTotal":{{url.Length}},"messagesUnread":0}
                """),
            _ => Status(HttpStatusCode.NotFound),
        });

        var folders = await mail.ListFoldersAsync(Personal);

        Assert.DoesNotContain(folders, f => f.Id is "STARRED" or "UNREAD" or "CATEGORY_PROMOTIONS");
        Assert.DoesNotContain(wire.Urls, u => u is "labels/STARRED" or "labels/UNREAD");

        var inbox = folders.Single(f => f.Id == "INBOX");
        Assert.Equal(("Inbox", "inbox"), (inbox.Name, inbox.WellKnownName));
        Assert.Equal("labels/INBOX".Length, inbox.MessageCount);           // from the label's own record
        Assert.Equal("deleteditems", folders.Single(f => f.Id == "TRASH").WellKnownName);
        Assert.Equal("junkemail", folders.Single(f => f.Id == "SPAM").WellKnownName);

        var year = folders.Single(f => f.Id == "Label_2");
        Assert.Equal(("2026", "Receipts/2026"), (year.Name, year.Path));
        Assert.Equal(1, year.ChildCount);                                  // Travel, one level down
        Assert.Equal(1, folders.Single(f => f.Id == "Label_1").ChildCount); // 2026 only, not Travel
    }

    // ── failures, in the tools' own terms ────────────────────────────────────

    [Fact]
    public async Task Too_many_requests_is_a_throttle_that_names_google_and_how_long()
    {
        var (mail, _) = Build(_ =>
        {
            var r = Status(HttpStatusCode.TooManyRequests, """
                {"error":{"code":429,"message":"Too many concurrent requests for user",
                  "errors":[{"message":"Too many concurrent requests for user","domain":"global","reason":"rateLimitExceeded"}],
                  "status":"RESOURCE_EXHAUSTED"}}
                """);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return r;
        });

        var ex = await Assert.ThrowsAsync<MailThrottledException>(() => mail.ListAsync(Personal));
        Assert.Contains("Google is rate-limiting personal", ex.Message);
        Assert.Equal(TimeSpan.FromSeconds(20), ex.RetryAfter);
    }

    [Fact]
    public async Task A_403_rate_limit_is_a_throttle_too()
    {
        var (mail, _) = Build(_ => Status(HttpStatusCode.Forbidden, """
            {"error":{"code":403,"message":"User Rate Limit Exceeded",
              "errors":[{"domain":"usageLimits","reason":"userRateLimitExceeded"}]}}
            """));
        await Assert.ThrowsAsync<MailThrottledException>(() => mail.ListAsync(Personal));
    }

    [Fact]
    public async Task Missing_permission_says_how_to_fix_it()
    {
        var (mail, _) = Build(_ => Status(HttpStatusCode.Forbidden, """
            {"error":{"code":403,"message":"Request had insufficient authentication scopes.",
              "errors":[{"message":"Insufficient Permission","domain":"global","reason":"insufficientPermissions"}],
              "status":"PERMISSION_DENIED",
              "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"ACCESS_TOKEN_SCOPE_INSUFFICIENT"}]}}
            """));

        var ex = await Assert.ThrowsAsync<MailException>(() => mail.ListAsync(Personal));
        Assert.Contains("hasn't given Outpost permission", ex.Message);
    }

    [Fact]
    public async Task A_token_google_no_longer_accepts_means_reconnect()
    {
        var (mail, _) = Build(_ => Status(HttpStatusCode.Unauthorized,
            """{"error":{"code":401,"message":"Request had invalid authentication credentials.","status":"UNAUTHENTICATED"}}"""));
        await Assert.ThrowsAsync<MailAuthRequiredException>(() => mail.ListAsync(Personal));
    }

    [Fact]
    public async Task No_token_at_all_means_reconnect_without_calling_gmail()
    {
        var (mail, wire) = Build(_ => throw new InvalidOperationException("must not be called"), token: null);
        await Assert.ThrowsAsync<MailAuthRequiredException>(() => mail.ListAsync(Personal));
        Assert.Empty(wire.Urls);
    }

    [Fact]
    public async Task Gmail_having_a_bad_minute_says_so()
    {
        var (mail, _) = Build(_ => Status(HttpStatusCode.ServiceUnavailable));
        var ex = await Assert.ThrowsAsync<MailException>(() => mail.ReadAsync(Personal, "18c1"));
        Assert.Contains("having trouble", ex.Message);
    }

    [Fact]
    public void Gmail_is_registered_as_the_google_provider()
    {
        var (mail, _) = Build(_ => Status(HttpStatusCode.NotFound));
        Assert.Equal(ProviderKind.Google, mail.Provider);
        Assert.True(new MailServiceRouter([mail]).Supports(ProviderKind.Google));
    }
}
