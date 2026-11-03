using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Sending from Gmail (OP2-281): new mail, replies, reply-all, forwards, and sends with
/// attachments from the job queue. Gmail takes the whole RFC 822 message, so these parse
/// what would be uploaded — with MimeKit, as a receiving client would — and check that,
/// not a description of it.
/// </summary>
public sealed class GmailSendTests
{
    private const string Me = "me@example.com";

    private static readonly Connection Personal = new()
    {
        UserId = Guid.Empty, Label = "personal", Provider = ProviderKind.Google, Instance = "google",
        AuthKind = AuthKind.OAuth2, SealedCredential = "sealed", State = ConnectionState.Active,
        AccountIdentifier = Me,
    };

    // ── a fake Gmail that keeps what it was sent ─────────────────────────────

    private sealed record Upload(string Url, string Metadata, MimeMessage Message);

    private sealed class Gmail(Func<HttpRequestMessage, HttpResponseMessage?>? custom = null) : HttpMessageHandler
    {
        public List<Upload> Uploads { get; } = [];
        public List<string> Gets { get; } = [];
        public Func<HttpResponseMessage>? OnSend { get; set; }
        public Action? BeforeSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (custom?.Invoke(r) is { } overridden) return overridden;

            var url = r.RequestUri!.AbsoluteUri;
            if (r.Method == HttpMethod.Post && url.Contains("/upload/gmail/v1/users/me/messages/send"))
            {
                BeforeSend?.Invoke();

                // multipart/related: JSON metadata, then the message.
                var multipart = (MultipartContent)r.Content!;
                var parts = multipart.ToList();
                var meta = await parts[0].ReadAsStringAsync(ct);
                Assert.Equal("message/rfc822", parts[1].Headers.ContentType?.MediaType);
                await using var raw = await parts[1].ReadAsStreamAsync(ct);
                var message = await MimeMessage.LoadAsync(raw, ct);
                lock (Uploads) Uploads.Add(new Upload(url, meta, message));

                return OnSend?.Invoke() ?? Json("""{"id":"sent-1","threadId":"thread-9","labelIds":["SENT"]}""");
            }

            lock (Gets) Gets.Add(url);
            return Respond(url);
        }

        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private sealed class OneClient(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private sealed class Tokens(string? token = "ya29.token") : IGoogleTokenAccessor
    {
        public Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default) => Task.FromResult(token);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string B64Url(string s) => B64Url(Encoding.UTF8.GetBytes(s));

    private static string H(string name, string value) =>
        $$"""{"name":{{JsonSerializer.Serialize(name)}},"value":{{JsonSerializer.Serialize(value)}}}""";

    private static (GmailService Mail, Gmail Wire) Build()
    {
        var wire = new Gmail();
        return (new GmailService(new Tokens(), new OneClient(wire), NullLogger<GmailService>.Instance), wire);
    }

    /// <summary>The original being replied to, in Gmail's full format.</summary>
    private static string Original(string from = "Ann Lee <ann@example.com>", string? replyTo = null,
        string to = "me@example.com, Jordan <jordan@example.com>", string cc = "Sam <sam@example.com>",
        string subject = "Thursday", string attachments = "") => $$"""
        {"id":"18c1","threadId":"thread-9","labelIds":["INBOX"],"internalDate":"1762162200000",
         "payload":{"partId":"","mimeType":"multipart/mixed","filename":"",
           "headers":[{{H("From", from)}},{{(replyTo is null ? "" : H("Reply-To", replyTo) + ",")}}
                      {{H("To", to)}},{{H("Cc", cc)}},{{H("Subject", subject)}},
                      {{H("Date", "Mon, 3 Nov 2026 09:00:00 +0000")}},
                      {{H("Message-ID", "<CAorig123@mail.gmail.com>")}},
                      {{H("References", "<CAfirst@mail.gmail.com>")}}],
           "body":{"size":0},
           "parts":[{"partId":"0","mimeType":"text/html","filename":"",
                     "headers":[{{H("Content-Type", "text/html; charset=UTF-8")}}],
                     "body":{"size":30,"data":"{{B64Url("<div>Can we move Thursday?</div>")}}"} }{{attachments}}]} }
        """;

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 the invoice");

    private const string WithAttachments = """
        ,{"partId":"1","mimeType":"application/pdf","filename":"Invoice.pdf","headers":[],
          "body":{"attachmentId":"ANGjdJ-pdf","size":20}},
         {"partId":"2","mimeType":"image/png","filename":"logo.png","headers":[],
          "body":{"size":3,"data":"AQID"}}
        """;

    private static Func<string, HttpResponseMessage> Mailbox(string original) => url =>
        url.Contains("/messages/18c1/attachments/ANGjdJ-pdf") ? Json($$"""{"size":20,"data":"{{B64Url(Pdf)}}"}""")
        : url.Contains("/messages/18c1?format=full") ? Json(original)
        : new HttpResponseMessage(HttpStatusCode.NotFound);

    private static string Html(MimeMessage m) => m.HtmlBody ?? "";

    // ── new mail ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_message_goes_up_whole_as_html_with_a_plain_alternative()
    {
        var (mail, wire) = Build();

        await mail.SendAsync(Personal, new OutgoingMail
        {
            To = ["Ann Lee <ann@example.com>", "bob@example.com"], Cc = ["sam@example.com"],
            Subject = "Agenda — Thursday", Body = "Please bring **the figures**.",
        });

        var up = Assert.Single(wire.Uploads);
        Assert.Contains("uploadType=multipart", up.Url);
        Assert.Equal("{}", up.Metadata);                          // a new conversation: no thread

        var m = up.Message;
        Assert.Equal(Me, m.From.Mailboxes.Single().Address);
        Assert.Equal(["ann@example.com", "bob@example.com"], m.To.Mailboxes.Select(x => x.Address));
        Assert.Equal("Ann Lee", m.To.Mailboxes.First().Name);
        Assert.Equal("sam@example.com", m.Cc.Mailboxes.Single().Address);
        Assert.Equal("Agenda — Thursday", m.Subject);            // non-ASCII survives encoding
        Assert.Contains("<strong>the figures</strong>", Html(m));
        Assert.Contains("the figures", m.TextBody);
        Assert.DoesNotContain("**", m.TextBody);
        Assert.IsType<MultipartAlternative>(m.Body);
        Assert.Equal(MessageImportance.Normal, m.Importance);
        Assert.False(m.Headers.Contains("X-Priority"));
    }

    [Fact]
    public async Task A_line_break_cannot_smuggle_a_header_in()
    {
        var (mail, wire) = Build();

        // In an address: refused, nothing sent.
        var ex = await Assert.ThrowsAsync<MailException>(() => mail.SendAsync(Personal,
            new OutgoingMail { To = ["a@example.com\r\nBcc: evil@example.com"], Subject = "s", Body = "b" }));
        Assert.Contains("Nothing was sent", ex.Message);
        Assert.Empty(wire.Uploads);

        // In a subject or a display name: carried as text, never as a header.
        await mail.SendAsync(Personal, new OutgoingMail
        {
            To = ["Ann\r\nBcc: evil@example.com <a@example.com>"], Subject = "Hi\r\nBcc: evil@example.com", Body = "b",
        });
        var m = wire.Uploads.Single().Message;
        Assert.Empty(m.Bcc);
        Assert.False(m.Headers.Contains(HeaderId.Bcc));
        Assert.Equal(["a@example.com"], m.To.Mailboxes.Select(x => x.Address));
    }

    [Theory]
    [InlineData(MailImportance.High, MessageImportance.High, "1 (Highest)")]
    [InlineData(MailImportance.Low, MessageImportance.Low, "5 (Lowest)")]
    public async Task Importance_goes_as_the_headers_mail_clients_read(MailImportance asked, MessageImportance header, string priority)
    {
        var (mail, wire) = Build();
        await mail.SendAsync(Personal, new OutgoingMail { To = ["a@example.com"], Subject = "s", Body = "b", Importance = asked });

        var m = wire.Uploads.Single().Message;
        Assert.Equal(header, m.Importance);
        Assert.Equal(priority, m.Headers["X-Priority"]);
    }

    [Fact]
    public void The_confirmation_does_not_promise_a_marker_gmail_will_not_show()
    {
        Assert.Contains("Gmail itself doesn't display it", MailTools.Marked(MailImportance.High, ProviderKind.Google));
        Assert.Equal(", marked high importance", MailTools.Marked(MailImportance.High, ProviderKind.Microsoft));
        Assert.Equal("", MailTools.Marked(MailImportance.Normal, ProviderKind.Google));
    }

    // ── replies ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_stays_in_the_thread_on_both_ends_and_quotes_the_original()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original());

        var sent = await mail.SendReplyAsync(Personal, "18c1", ReplyKind.Sender, "3pm works.");

        var up = Assert.Single(wire.Uploads);
        Assert.Equal("""{"threadId":"thread-9"}""", up.Metadata);   // the user's own thread

        var m = up.Message;
        Assert.Equal("CAorig123@mail.gmail.com", m.InReplyTo);       // and the recipient's
        Assert.Equal(["CAfirst@mail.gmail.com", "CAorig123@mail.gmail.com"], m.References);
        Assert.Equal("Re: Thursday", m.Subject);
        Assert.Equal(["ann@example.com"], m.To.Mailboxes.Select(x => x.Address));
        Assert.Empty(m.Cc);

        var html = Html(m);
        Assert.True(html.IndexOf("3pm works.", StringComparison.Ordinal) < html.IndexOf("gmail_quote", StringComparison.Ordinal));
        Assert.Contains("Can we move Thursday?", html);
        Assert.Contains("Ann Lee &lt;ann@example.com&gt; wrote:", html);

        Assert.Equal("Re: Thursday", sent.Subject);
        Assert.Equal("ann@example.com", sent.To.Single().Address);
    }

    [Fact]
    public async Task Re_is_not_doubled_on_a_thread_that_already_has_one()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original(subject: "RE: Thursday"));
        await mail.SendReplyAsync(Personal, "18c1", ReplyKind.Sender, "ok");
        Assert.Equal("RE: Thursday", wire.Uploads.Single().Message.Subject);
    }

    [Fact]
    public async Task A_reply_goes_to_reply_to_when_the_sender_set_one()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original(replyTo: "Bookings <bookings@example.com>"));
        await mail.SendReplyAsync(Personal, "18c1", ReplyKind.Sender, "ok");
        Assert.Equal(["bookings@example.com"], wire.Uploads.Single().Message.To.Mailboxes.Select(x => x.Address));
    }

    [Fact]
    public async Task Reply_all_reaches_everyone_but_the_user_and_nobody_twice()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original(to: "me@example.com, Jordan <jordan@example.com>, ann@example.com",
            cc: "Sam <sam@example.com>, ME@example.com"));

        var sent = await mail.SendReplyAsync(Personal, "18c1", ReplyKind.All, "ok");

        var m = wire.Uploads.Single().Message;
        Assert.Equal(["ann@example.com", "jordan@example.com"], m.To.Mailboxes.Select(x => x.Address));
        Assert.Equal(["sam@example.com"], m.Cc.Mailboxes.Select(x => x.Address));
        Assert.Equal(["sam@example.com"], sent.Cc.Select(r => r.Address));
    }

    [Fact]
    public async Task Replying_to_your_own_sent_message_goes_back_to_the_people_you_sent_it_to()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original(from: "Me <me@example.com>", to: "Ann <ann@example.com>"));
        await mail.SendReplyAsync(Personal, "18c1", ReplyKind.Sender, "following up");
        Assert.Equal(["ann@example.com"], wire.Uploads.Single().Message.To.Mailboxes.Select(x => x.Address));
    }

    [Fact]
    public async Task Recipient_edits_work_exactly_as_on_outlook()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original());

        var sent = await mail.SendReplyAsync(Personal, "18c1", ReplyKind.All, "ok",
            edit: new RecipientEdit { Drop = ["Sam <sam@example.com>"], AddCc = ["pat@example.com"] });

        var m = wire.Uploads.Single().Message;
        Assert.DoesNotContain("sam@example.com", m.To.Mailboxes.Concat(m.Cc.Mailboxes).Select(x => x.Address));
        Assert.Contains("pat@example.com", m.Cc.Mailboxes.Select(x => x.Address));
        Assert.Equal("sam@example.com", sent.Dropped.Single().Address);
    }

    [Fact]
    public async Task Dropping_someone_who_is_not_on_the_thread_sends_nothing_and_says_who_is()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original());

        var ex = await Assert.ThrowsAsync<MailException>(() => mail.SendReplyAsync(Personal, "18c1", ReplyKind.All, "ok",
            edit: new RecipientEdit { Drop = ["bobby@example.com"] }));

        Assert.StartsWith("Nothing was sent: nobody on that reply is \"bobby@example.com\"", ex.Message);
        Assert.Empty(wire.Uploads);
    }

    [Fact]
    public async Task Taking_everyone_off_sends_nothing()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original());

        var ex = await Assert.ThrowsAsync<MailException>(() => mail.SendReplyAsync(Personal, "18c1", ReplyKind.Sender, "ok",
            edit: new RecipientEdit { Drop = ["ann@example.com"] }));
        Assert.Contains("no recipients at all", ex.Message);
        Assert.Empty(wire.Uploads);
    }

    [Fact]
    public async Task Replying_to_a_message_that_has_gone_says_so()
    {
        var (mail, wire) = Build();
        var ex = await Assert.ThrowsAsync<MailException>(() => mail.SendReplyAsync(Personal, "18c1", ReplyKind.Sender, "ok"));
        Assert.Contains("no longer exists", ex.Message);
        Assert.Empty(wire.Uploads);
    }

    // ── forwards ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_forward_carries_the_original_and_its_attachments()
    {
        var (mail, wire) = Build();
        wire.Respond = Mailbox(Original(attachments: WithAttachments));

        var sent = await mail.SendReplyAsync(Personal, "18c1", ReplyKind.Forward, "FYI", ["bob@example.com"]);

        var m = wire.Uploads.Single().Message;
        Assert.Equal("Fwd: Thursday", m.Subject);
        Assert.Equal(["bob@example.com"], m.To.Mailboxes.Select(x => x.Address));
        Assert.Null(m.InReplyTo);                                     // a new conversation for Bob…
        Assert.Contains("thread-9", wire.Uploads.Single().Metadata);   // …in the user's own thread

        var html = Html(m);
        Assert.Contains("---------- Forwarded message ---------", html);
        Assert.Contains("Can we move Thursday?", html);

        var files = m.Attachments.OfType<MimePart>().ToList();
        Assert.Equal(["Invoice.pdf", "logo.png"], files.Select(f => f.FileName));
        using var pdf = new MemoryStream();
        await files[0].Content.DecodeToAsync(pdf);
        Assert.Equal(Pdf, pdf.ToArray());
        Assert.Equal("Fwd: Thursday", sent.Subject);
    }

    [Fact]
    public async Task A_forward_whose_attachment_cannot_be_fetched_does_not_go_without_it()
    {
        var (mail, wire) = Build();
        var original = Original(attachments: WithAttachments);
        wire.Respond = url => url.Contains("/attachments/")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(original);

        var ex = await Assert.ThrowsAsync<MailException>(
            () => mail.SendReplyAsync(Personal, "18c1", ReplyKind.Forward, "FYI", ["bob@example.com"]));
        Assert.Contains("Invoice.pdf", ex.Message);
        Assert.Empty(wire.Uploads);
    }

    // ── when the answer is not clear (OP2-262) ───────────────────────────────

    [Fact]
    public async Task No_answer_once_the_message_is_handed_over_is_may_have_sent()
    {
        var (mail, wire) = Build();
        wire.OnSend = () => throw new HttpRequestException("The response ended prematurely.");

        var ex = await Assert.ThrowsAsync<MailMaybeSentException>(() => mail.SendAsync(Personal,
            new OutgoingMail { To = ["a@example.com"], Subject = "s", Body = "b" }));
        Assert.Contains("Check Sent Items", ex.Message);
    }

    [Fact]
    public async Task A_server_error_after_the_upload_is_may_have_sent_too()
    {
        var (mail, wire) = Build();
        wire.OnSend = () => Json("""{"error":{"code":503,"message":"Backend Error","status":"UNAVAILABLE"}}""",
            HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<MailMaybeSentException>(() => mail.SendAsync(Personal,
            new OutgoingMail { To = ["a@example.com"], Subject = "s", Body = "b" }));
    }

    [Fact]
    public async Task A_clear_refusal_is_a_plain_failure_because_it_did_not_go()
    {
        var (mail, wire) = Build();
        wire.OnSend = () => Json("""{"error":{"code":400,"message":"Invalid To header","status":"INVALID_ARGUMENT"}}""",
            HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<MailException>(() => mail.SendAsync(Personal,
            new OutgoingMail { To = ["a@example.com"], Subject = "s", Body = "b" }));
        Assert.IsNotType<MailMaybeSentException>(ex);
    }

    // ── with attachments, from the job queue ─────────────────────────────────

    private sealed class Staged(Dictionary<Guid, byte[]> bytes) : IStagingService
    {
        public Task<Stream> OpenAsync(StagedFile file, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(bytes[file.Id]));
        public Task<Stream> OpenRangeAsync(StagedFile file, long from, long toInclusive, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StagedFile> UploadAsync(Guid u, string n, string f, string c, Stream s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StagedFile>> ListAsync(Guid u, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StagedFile?> FindByNameAsync(Guid u, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid u, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static StagedFile File(string name, long size) => new()
    {
        UserId = Guid.Empty, Name = name, FileName = name, ContentType = "application/pdf",
        SizeBytes = size, StorageKey = "k", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    private sealed class Recorder(List<string> log) : IProgress<(int Percent, string Detail)>
    {
        public void Report((int Percent, string Detail) value) { lock (log) log.Add($"progress {value.Percent}"); }
    }

    private static (GmailAttachmentSender Sender, Gmail Wire, List<string> Log, StagedFile File) Job(string? token = "ya29.token")
    {
        var log = new List<string>();
        var wire = new Gmail { BeforeSend = () => { lock (log) log.Add("POST"); } };
        var f = File("Q3 report.pdf", Pdf.Length);
        var sender = new GmailAttachmentSender(new Tokens(token), new Staged(new() { [f.Id] = Pdf }),
            new OneClient(wire), NullLogger<GmailAttachmentSender>.Instance);
        return (sender, wire, log, f);
    }

    private static OutgoingMail Mail() => new() { To = ["ann@example.com"], Subject = "Q3", Body = "Attached." };

    [Fact]
    public async Task A_queued_send_goes_up_with_its_files_and_says_sending_before_it_does()
    {
        var (sender, wire, log, f) = Job();

        var outcome = await sender.SendAsync(Personal, Mail(), [f], progress: new Recorder(log));

        Assert.True(outcome.Ok, outcome.Error);

        // JobWrites marks "may have sent" at 95, so 95 must come before the request.
        Assert.True(log.IndexOf("progress 95") < log.IndexOf("POST"), string.Join(", ", log));

        var m = wire.Uploads.Single().Message;
        var part = Assert.IsType<MimePart>(Assert.Single(m.Attachments));
        Assert.Equal("Q3 report.pdf", part.FileName);
        Assert.Equal(ContentEncoding.Base64, part.ContentTransferEncoding);
        using var bytes = new MemoryStream();
        await part.Content.DecodeToAsync(bytes);
        Assert.Equal(Pdf, bytes.ToArray());
        Assert.Contains("Attached.", Html(m));
    }

    [Fact]
    public async Task Over_gmails_25_mb_is_refused_before_anything_is_sent()
    {
        var (sender, wire, _, _) = Job();
        var outcome = await sender.SendAsync(Personal, Mail(), [File("huge.zip", 26L * 1024 * 1024)]);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Retryable);
        Assert.Contains("25 MB", outcome.Error);
        Assert.Contains("Nothing was sent", outcome.Error);
        Assert.Empty(wire.Uploads);
    }

    [Fact]
    public async Task A_queued_send_with_no_answer_is_may_have_sent_and_never_retryable()
    {
        var (sender, wire, _, f) = Job();
        wire.OnSend = () => throw new HttpRequestException("reset");

        var outcome = await sender.SendAsync(Personal, Mail(), [f]);

        Assert.True(outcome.MaybeSent);
        Assert.False(outcome.Retryable);
        Assert.Contains("Check Sent Items", outcome.Error);
    }

    [Fact]
    public async Task A_queued_send_gmail_throttled_did_not_go_and_can_try_again()
    {
        var (sender, wire, _, f) = Job();
        wire.OnSend = () => Json("""{"error":{"code":429,"errors":[{"reason":"rateLimitExceeded"}]}}""", HttpStatusCode.TooManyRequests);

        var outcome = await sender.SendAsync(Personal, Mail(), [f]);

        Assert.False(outcome.Ok);
        Assert.False(outcome.MaybeSent);
        Assert.True(outcome.Retryable);
    }

    [Fact]
    public async Task A_queued_send_on_a_connection_that_needs_reconnecting_sends_nothing()
    {
        var (sender, wire, _, f) = Job(token: null);
        var outcome = await sender.SendAsync(Personal, Mail(), [f]);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Retryable);
        Assert.Contains("re-authorising", outcome.Error);
        Assert.Empty(wire.Uploads);
    }

    [Fact]
    public async Task The_router_sends_a_google_connections_attachments_through_gmail()
    {
        var (gmail, wire, _, f) = Job();
        var router = new LargeAttachmentSenderRouter([gmail]);

        var outcome = await router.SendAsync(Personal, Mail(), [f]);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Single(wire.Uploads);
    }
}
