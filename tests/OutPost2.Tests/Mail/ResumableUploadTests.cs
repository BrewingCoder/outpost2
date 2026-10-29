using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Files;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Resuming an interrupted attachment upload (OP2-101).
/// <para>
/// OP2-10 set the rule and this is the suite that holds it: <i>"GET {uploadUrl} returns
/// nextExpectedRanges — use it, do not assume."</i> The code used to assume, starting at
/// zero and advancing by the length it had just sent. Both are right until something
/// fails, and wrong in the expensive direction afterwards — re-sending megabytes Graph
/// already holds, or leaving a hole in the file where it does not.
/// </para>
/// <para>
/// Driven through a stubbed transport that records every request, because what is being
/// asserted is the CONVERSATION with Graph: which ranges went up, in what order, and what
/// was asked before sending them.
/// </para>
/// </summary>
public class ResumableUploadTests
{
    private const string Session = "https://upload.example/session/abc";
    private const long FileSize = 12L * 1024 * 1024;   // 12 MiB

    /// <summary>
    /// The sender's chunk: 25 units of 320 KiB. 8,192,000 bytes, NOT 8 MiB — Graph
    /// requires a multiple of 320 KiB and 8 MiB is not one, so the ranges below are
    /// deliberately not round numbers.
    /// </summary>
    private const long Chunk = 320 * 1024 * 25;

    private sealed record Call(string Method, string Url, string? Range);

    private sealed class Transport(Func<HttpRequestMessage, List<Call>, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add(new Call(r.Method.Method, r.RequestUri!.AbsoluteUri,
                r.Content?.Headers.ContentRange?.ToString()));
            return Task.FromResult(respond(r, Calls));
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

    /// <summary>Serves bytes from nowhere: the content is irrelevant, the ranges are not.</summary>
    private sealed class FakeStaging : IStagingService
    {
        public Task<Stream> OpenAsync(StagedFile f, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(new byte[f.SizeBytes]));

        public Task<Stream> OpenRangeAsync(StagedFile f, long from, long to, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(new byte[to - from + 1]));

        public Task<StagedFile> UploadAsync(Guid u, string n, string fn, string ctype, Stream s,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StagedFile>> ListAsync(Guid u, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<StagedFile?> FindByNameAsync(Guid u, string n, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(Guid u, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static Connection Conn() => new()
    {
        UserId = Guid.Empty, Label = "work", Provider = ProviderKind.Microsoft,
        Instance = "tenant", AuthKind = AuthKind.OAuth2, SealedCredential = "sealed",
        State = ConnectionState.Active,
    };

    private static StagedFile Big(Guid? id = null) => new()
    {
        Id = id ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
        UserId = Guid.Empty, Name = "report", FileName = "report.pdf",
        ContentType = "application/pdf", SizeBytes = FileSize,
        StorageKey = "k", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    private static Core.Mail.OutgoingMail Mail() => new()
    {
        To = ["someone@example.com"], Cc = [], Subject = "here it is", Body = "see attached", IsHtml = false,
    };

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Ranges(params string[] ranges) =>
        Json(HttpStatusCode.Accepted,
            $"{{\"nextExpectedRanges\":[{string.Join(',', ranges.Select(r => $"\"{r}\""))}]}}");

    /// <summary>Graph's reply to the final chunk: the attachment, with no ranges left.</summary>
    private static HttpResponseMessage Finished() => Json(HttpStatusCode.Created, """{"id":"att-1"}""");

    private static (LargeAttachmentSender Sender, Transport Wire) Build(
        Func<HttpRequestMessage, List<Call>, HttpResponseMessage> respond)
    {
        var wire = new Transport(respond);
        return (new LargeAttachmentSender(new Tokens(), new FakeStaging(), new OneClient(wire),
            NullLogger<LargeAttachmentSender>.Instance), wire);
    }

    /// <summary>The default mailbox: a draft is created, a session opened, the send accepted.</summary>
    private static HttpResponseMessage Mailbox(HttpRequestMessage r)
    {
        var u = r.RequestUri!.AbsoluteUri;

        if (u.Contains("createUploadSession")) return Json(HttpStatusCode.OK, $$"""{"uploadUrl":"{{Session}}"}""");
        if (u.EndsWith("/send")) return new HttpResponseMessage(HttpStatusCode.Accepted);
        if (u.Contains("me/messages") && r.Method == HttpMethod.Post) return Json(HttpStatusCode.Created, """{"id":"draft-1"}""");
        if (u.Contains("me/messages") && r.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, """{"id":"draft-1"}""");
        if (u.Contains("me/messages") && r.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static List<string> SentRanges(Transport wire) =>
        wire.Calls.Where(c => c.Method == "PUT" && c.Range is not null).Select(c => c.Range!).ToList();

    // ── the rule: ask, do not assume ─────────────────────────────────────────

    [Fact]
    public async Task A_fresh_upload_asks_the_session_where_it_is_before_sending_anything()
    {
        var (sender, wire) = Build((r, calls) =>
        {
            if (!r.RequestUri!.AbsoluteUri.StartsWith(Session)) return Mailbox(r);
            if (r.Method == HttpMethod.Get) return Ranges($"0-{FileSize - 1}");
            return calls.Count(c => c.Method == "PUT") == 1 ? Ranges("8192000-12582911") : Finished();
        });

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.True(outcome.Ok, outcome.Error);

        // The GET comes first. Without it a resumed session starts at zero, which is the
        // bug this ticket is about.
        var firstToSession = wire.Calls.First(c => c.Url.StartsWith(Session));
        Assert.Equal("GET", firstToSession.Method);

        Assert.Equal(["bytes 0-8191999/12582912", "bytes 8192000-12582911/12582912"], SentRanges(wire));
    }

    [Fact]
    public async Task It_starts_from_the_offset_Graph_reports_not_from_zero()
    {
        // The session already holds the first 8 MiB from an attempt that died. Starting
        // at zero would push them a second time — the whole cost this change avoids.
        var (sender, wire) = Build((r, _) =>
        {
            if (!r.RequestUri!.AbsoluteUri.StartsWith(Session)) return Mailbox(r);
            return r.Method == HttpMethod.Get ? Ranges("8192000-12582911") : Finished();
        });

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(["bytes 8192000-12582911/12582912"], SentRanges(wire));
    }

    [Fact]
    public async Task It_advances_by_what_Graph_acknowledged_not_by_what_it_sent()
    {
        // Graph accepts the chunk but reports it kept less than was offered. Trusting our
        // own arithmetic here leaves a hole in the middle of the file, and the send fails
        // later with nothing to point at.
        var (sender, wire) = Build((r, calls) =>
        {
            if (!r.RequestUri!.AbsoluteUri.StartsWith(Session)) return Mailbox(r);
            if (r.Method == HttpMethod.Get) return Ranges($"0-{FileSize - 1}");

            return calls.Count(c => c.Method == "PUT") switch
            {
                1 => Ranges("4194304-12582911"),    // only took the first 4 MiB
                2 => Ranges("8192000-12582911"),
                _ => Finished(),
            };
        });

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(
        [
            "bytes 0-8191999/12582912",
            "bytes 4194304-12386303/12582912",
            "bytes 8192000-12582911/12582912",
        ], SentRanges(wire));
    }

    [Fact]
    public async Task A_session_with_nothing_outstanding_is_already_done()
    {
        // The previous attempt died between the last chunk and recording it. Re-sending
        // would attach the file twice.
        var (sender, wire) = Build((r, _) =>
            r.RequestUri!.AbsoluteUri.StartsWith(Session)
                ? Json(HttpStatusCode.OK, """{"nextExpectedRanges":[]}""")
                : Mailbox(r));

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Empty(SentRanges(wire));
    }

    // ── a lost session is one attachment's problem ───────────────────────────

    [Fact]
    public async Task An_expired_session_restarts_that_attachment_and_not_the_whole_job()
    {
        var sessions = 0;
        var second = Session + "-2";

        var (sender, wire) = Build((r, _) =>
        {
            var u = r.RequestUri!.AbsoluteUri;

            if (u.Contains("createUploadSession"))
            {
                sessions++;
                return Json(HttpStatusCode.OK, sessions == 1
                    ? $$"""{"uploadUrl":"{{Session}}"}"""
                    : $$"""{"uploadUrl":"{{second}}"}""");
            }

            // The first session has been cleaned up by Graph.
            if (u.StartsWith(second))
                return r.Method == HttpMethod.Get ? Ranges($"0-{FileSize - 1}") : Finished();
            if (u.StartsWith(Session)) return new HttpResponseMessage(HttpStatusCode.NotFound);

            return Mailbox(r);
        });

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(2, sessions);

        // One draft, created once. The job did not start over.
        Assert.Single(wire.Calls, c => c.Method == "POST" && c.Url.EndsWith("me/messages"));
        Assert.Contains(wire.Calls, c => c.Url.StartsWith(second) && c.Method == "PUT");
    }

    // ── what is carried between attempts ─────────────────────────────────────

    [Fact]
    public async Task The_draft_and_the_session_are_recorded_as_soon_as_they_exist()
    {
        var saved = new List<SendResumeState>();

        var (sender, _) = Build((r, calls) =>
        {
            if (!r.RequestUri!.AbsoluteUri.StartsWith(Session)) return Mailbox(r);
            if (r.Method == HttpMethod.Get) return Ranges($"0-{FileSize - 1}");
            return calls.Count(c => c.Method == "PUT") == 1 ? Ranges("8192000-12582911") : Finished();
        });

        await sender.SendAsync(Conn(), Mail(), [Big()],
            persist: (s, _) => { saved.Add(s with { Attachments = new(s.Attachments) }); return Task.CompletedTask; });

        // Draft first, then the session, then the attachment finishing: each is a thing
        // a resumed attempt would otherwise pay for again.
        Assert.Equal("draft-1", saved[0].DraftId);
        Assert.Empty(saved[0].Attachments);

        Assert.Equal(Session, saved[1].Attachments.Single().Value.UploadUrl);
        Assert.False(saved[1].Attachments.Single().Value.Done);

        Assert.True(saved[^1].Attachments.Single().Value.Done);
    }

    [Fact]
    public async Task A_resumed_send_reuses_the_draft_instead_of_making_another()
    {
        var (sender, wire) = Build((r, _) =>
            r.RequestUri!.AbsoluteUri.StartsWith(Session)
                ? (r.Method == HttpMethod.Get ? Ranges("8192000-12582911") : Finished())
                : Mailbox(r));

        var resume = new SendResumeState
        {
            DraftId = "draft-1",
            Attachments = { [Big().Id.ToString()] = new AttachmentUpload(Session, false) },
        };

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()], resume);

        Assert.True(outcome.Ok, outcome.Error);

        // No new draft and no new session: both were already paid for.
        Assert.DoesNotContain(wire.Calls, c => c.Method == "POST" && c.Url.EndsWith("me/messages"));
        Assert.DoesNotContain(wire.Calls, c => c.Url.Contains("createUploadSession"));
        Assert.Contains(wire.Calls, c => c.Method == "GET" && c.Url.Contains("me/messages/draft-1"));
    }

    [Fact]
    public async Task An_attachment_already_acknowledged_is_not_uploaded_again()
    {
        var (sender, wire) = Build((r, _) => Mailbox(r));

        var resume = new SendResumeState
        {
            DraftId = "draft-1",
            Attachments = { [Big().Id.ToString()] = new AttachmentUpload(null, true) },
        };

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()], resume);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Empty(SentRanges(wire));
        Assert.DoesNotContain(wire.Calls, c => c.Url.Contains("createUploadSession"));
    }

    [Fact]
    public async Task A_draft_the_user_deleted_starts_the_send_again()
    {
        var saved = new List<SendResumeState>();

        var (sender, wire) = Build((r, calls) =>
        {
            var u = r.RequestUri!.AbsoluteUri;

            // The resumed draft is gone; a newly created one is fine.
            if (r.Method == HttpMethod.Get && u.Contains("me/messages/gone"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (!u.StartsWith(Session)) return Mailbox(r);
            if (r.Method == HttpMethod.Get) return Ranges($"0-{FileSize - 1}");
            return calls.Count(c => c.Method == "PUT") == 1 ? Ranges("8192000-12582911") : Finished();
        });

        var resume = new SendResumeState
        {
            DraftId = "gone",
            Attachments = { [Big().Id.ToString()] = new AttachmentUpload(Session, false) },
        };

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()], resume,
            persist: (s, _) => { saved.Add(s with { Attachments = new(s.Attachments) }); return Task.CompletedTask; });

        Assert.True(outcome.Ok, outcome.Error);

        // A new draft, and the stale session discarded with it — the sessions belong to
        // the old draft's attachments and mean nothing without it.
        Assert.Contains(wire.Calls, c => c.Method == "POST" && c.Url.EndsWith("me/messages"));
        Assert.Contains(saved, s => s.DraftId is null && s.Attachments.Count == 0);
        Assert.Equal("draft-1", saved[^1].DraftId);
    }

    // ── keeping the work when it is worth keeping ────────────────────────────

    [Fact]
    public async Task A_retryable_failure_keeps_the_draft()
    {
        // The draft used to be deleted on every failure, which threw away every byte
        // already uploaded and made a resume impossible: the next attempt started from
        // an empty mailbox.
        var (sender, wire) = Build((r, _) =>
            r.RequestUri!.AbsoluteUri.StartsWith(Session)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Mailbox(r));

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Retryable);
        Assert.Equal("draft-1", outcome.DraftId);
        Assert.DoesNotContain(wire.Calls, c => c.Method == "DELETE");
    }

    [Fact]
    public async Task A_permanent_failure_cleans_the_draft_up()
    {
        // Nothing will come back for it, and an abandoned draft sits in the folder the
        // user actually looks at.
        var (sender, wire) = Build((r, _) =>
            r.RequestUri!.AbsoluteUri.StartsWith(Session)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : Mailbox(r));

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Retryable);
        Assert.Contains(wire.Calls, c => c.Method == "DELETE" && c.Url.Contains("me/messages/draft-1"));
    }

    [Fact]
    public async Task A_throttled_chunk_is_retried_from_wherever_Graph_says_it_got_to()
    {
        var puts = 0;

        var (sender, wire) = Build((r, _) =>
        {
            if (!r.RequestUri!.AbsoluteUri.StartsWith(Session)) return Mailbox(r);

            if (r.Method == HttpMethod.Get)
                // First ask: nothing yet. Second ask (after the 429): Graph took the
                // chunk it then failed to acknowledge, so resending it would be the one
                // thing that cannot work.
                return puts == 0 ? Ranges($"0-{FileSize - 1}") : Ranges("8192000-12582911");

            puts++;
            return puts == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Finished();
        });

        var outcome = await sender.SendAsync(Conn(), Mail(), [Big()]);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(["bytes 0-8191999/12582912", "bytes 8192000-12582911/12582912"], SentRanges(wire));
    }

    [Fact]
    public async Task Resume_state_survives_a_round_trip_through_JSON()
    {
        // It is stored as a string on the job, so a shape the serialiser cannot carry
        // would silently become "start again" on every retry.
        var original = new SendResumeState
        {
            DraftId = "draft-1",
            Attachments =
            {
                ["a"] = new AttachmentUpload(Session, false),
                ["b"] = new AttachmentUpload(null, true),
            },
        };

        var back = JsonSerializer.Deserialize<SendResumeState>(JsonSerializer.Serialize(original))!;

        Assert.Equal("draft-1", back.DraftId);
        Assert.Equal(Session, back.Attachments["a"].UploadUrl);
        Assert.True(back.Attachments["b"].Done);
    }
}
