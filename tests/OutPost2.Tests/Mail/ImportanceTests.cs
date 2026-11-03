using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Jobs;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Importance on mail going out, and on mail coming in (OP2-276). The user asked for a
/// message to go out as high importance and had to be told it could not, though Graph has
/// had an <c>importance</c> property on a message all along.
/// </summary>
public sealed class ImportanceTests
{
    private static readonly Connection Work = new()
    {
        UserId = Guid.Empty, Label = "work", Provider = ProviderKind.Microsoft,
        Instance = "tenant", AuthKind = AuthKind.OAuth2, SealedCredential = "sealed",
        State = ConnectionState.Active,
    };

    private sealed record Call(string Method, string Url, string? Body);

    private sealed class Transport(Func<Call, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var call = new Call(r.Method.Method, r.RequestUri!.AbsoluteUri,
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

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static (MailService, Transport) Service(Func<Call, HttpResponseMessage> respond)
    {
        var wire = new Transport(respond);
        return (new MailService(new Tokens(), new OneClient(wire), NullLogger<MailService>.Instance), wire);
    }

    private static string? Importance(string? json) =>
        json is null ? null
        : JsonDocument.Parse(json).RootElement is var root
          && (root.TryGetProperty("message", out var m) ? m : root).TryGetProperty("importance", out var i)
            ? i.GetString() : null;

    // ── what people say ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, MailImportance.Normal)]
    [InlineData("", MailImportance.Normal)]
    [InlineData("normal", MailImportance.Normal)]
    [InlineData("High", MailImportance.High)]
    [InlineData("urgent", MailImportance.High)]
    [InlineData("important", MailImportance.High)]
    [InlineData(" low ", MailImportance.Low)]
    public void What_people_say_is_understood(string? said, MailImportance expected)
    {
        Assert.Null(MailTools.ImportanceProblem(said, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("hihg")]
    [InlineData("critical-ish")]
    public void Anything_else_is_refused_and_says_nothing_was_sent(string said)
    {
        // Sending at normal when "high" was asked for and misheard would let the user
        // believe it went out flagged.
        var problem = MailTools.ImportanceProblem(said, out _);
        Assert.NotNull(problem);
        Assert.Contains("Nothing was sent", problem);
    }

    [Fact]
    public void The_confirmation_says_the_importance_only_when_there_is_one()
    {
        Assert.Equal("", MailTools.Marked(MailImportance.Normal));
        Assert.Equal(", marked high importance", MailTools.Marked(MailImportance.High));
        Assert.Equal("", MailTools.Marker(MailImportance.Normal));
        Assert.Equal(" (high importance)", MailTools.Marker(MailImportance.High));
    }

    // ── on the wire ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MailImportance.High, "high")]
    [InlineData(MailImportance.Low, "low")]
    [InlineData(MailImportance.Normal, "normal")]
    public async Task Send_mail_carries_importance_to_graph(MailImportance level, string wire)
    {
        var (mail, calls) = Service(_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        await mail.SendAsync(Work, new OutgoingMail
        {
            To = ["a@example.com"], Subject = "Act by Thursday", Body = "Please.", Importance = level,
        });

        var send = Assert.Single(calls.Calls);
        Assert.EndsWith("me/sendMail", send.Url);
        Assert.Equal(wire, Importance(send.Body));
    }

    private const string Draft = """
        {
          "id": "draft-1", "subject": "RE: Contract",
          "from": { "emailAddress": { "name": "Me", "address": "me@example.com" } },
          "toRecipients": [ { "emailAddress": { "name": "Ann", "address": "ann@example.com" } } ],
          "ccRecipients": [],
          "receivedDateTime": "2026-11-03T01:00:00Z", "isRead": true, "hasAttachments": false,
          "body": { "contentType": "html", "content": "<p>original</p>" }
        }
        """;

    private static HttpResponseMessage DraftThenOk(Call c) =>
        c.Url.Contains("/createReply") || c.Url.Contains("/createForward")
            ? Json(Draft, HttpStatusCode.Created)
            : new HttpResponseMessage(HttpStatusCode.Accepted);

    [Fact]
    public async Task A_high_importance_reply_patches_importance_onto_the_draft_before_it_goes()
    {
        var (mail, calls) = Service(DraftThenOk);

        await mail.SendReplyAsync(Work, "m1", ReplyKind.Sender, "Yes.", importance: MailImportance.High);

        var patch = Assert.Single(calls.Calls, c => c.Method == "PATCH");
        Assert.Equal("high", Importance(patch.Body));

        // Patched, then sent — never the other way round.
        Assert.True(calls.Calls.IndexOf(patch) < calls.Calls.FindIndex(c => c.Url.EndsWith("/send")));
    }

    [Fact]
    public async Task An_ordinary_reply_leaves_importance_as_graph_composed_it()
    {
        var (mail, calls) = Service(DraftThenOk);

        await mail.SendReplyAsync(Work, "m1", ReplyKind.Sender, "Yes.");

        var patch = Assert.Single(calls.Calls, c => c.Method == "PATCH");
        Assert.Null(Importance(patch.Body));
    }

    [Fact]
    public async Task A_forward_can_be_high_importance_too()
    {
        var (mail, calls) = Service(DraftThenOk);

        await mail.SendReplyAsync(Work, "m1", ReplyKind.Forward, "FYI", ["bob@example.com"],
            importance: MailImportance.High);

        Assert.Equal("high", Importance(Assert.Single(calls.Calls, c => c.Method == "PATCH").Body));
    }

    [Fact]
    public async Task Read_mail_sees_the_importance_of_what_came_in()
    {
        var (mail, calls) = Service(_ => Json("""
            {
              "id": "m1", "subject": "Deadline",
              "from": { "emailAddress": { "name": "Ann", "address": "ann@example.com" } },
              "toRecipients": [], "ccRecipients": [],
              "receivedDateTime": "2026-11-03T01:00:00Z", "isRead": false, "hasAttachments": false,
              "importance": "high",
              "body": { "contentType": "text", "content": "Now." }
            }
            """));

        var m = await mail.ReadAsync(Work, "m1");

        Assert.Equal(MailImportance.High, m!.Importance);
        Assert.Contains("importance", Assert.Single(calls.Calls).Url);
    }

    // ── queued sends ─────────────────────────────────────────────────────────

    [Fact]
    public void A_send_queued_before_this_change_still_reads_as_normal_importance()
    {
        // Jobs outlive deploys. A payload written by the old code has no Importance field
        // and must still deserialise rather than fail the send.
        var old = """
            {"ConnectionId":"00000000-0000-0000-0000-000000000001","To":["a@example.com"],
             "Cc":[],"Subject":"s","Body":"b","AttachmentIds":[]}
            """;

        var payload = JsonSerializer.Deserialize<SendMailPayload>(old)!;
        Assert.Equal(MailImportance.Normal, payload.Importance);
    }

    [Fact]
    public void A_queued_high_importance_send_keeps_its_importance()
    {
        var payload = new SendMailPayload(Guid.NewGuid(), ["a@example.com"], [], "s", "b", [], MailImportance.High);
        var back = JsonSerializer.Deserialize<SendMailPayload>(JsonSerializer.Serialize(payload))!;
        Assert.Equal(MailImportance.High, back.Importance);
    }

    [Fact]
    public void Joining_a_queued_send_says_when_the_importance_asked_for_is_not_the_one_it_goes_with()
    {
        // Asked again with only the importance changed: it joins the queued job (no second
        // copy), and says that the importance did not change rather than letting the user
        // believe it went flagged.
        var queued = JsonSerializer.Serialize(
            new SendMailPayload(Guid.NewGuid(), ["a@example.com"], [], "s", "b", [], MailImportance.Normal));

        var said = FileAndJobTools.ImportanceUnchanged(queued, MailImportance.High);
        Assert.Contains("queued at normal importance", said);
        Assert.Contains("not going as high importance", said);

        Assert.Equal("", FileAndJobTools.ImportanceUnchanged(queued, MailImportance.Normal));
        Assert.Equal("", FileAndJobTools.ImportanceUnchanged("not json", MailImportance.High));
    }
}
