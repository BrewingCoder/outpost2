using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;

namespace OutPost2.Infrastructure.Mail;

public interface IMailService
{
    Task<IReadOnlyList<MailSummary>> ListAsync(
        Connection connection, string folder = "inbox", int top = 25, bool unreadOnly = false,
        CancellationToken ct = default);

    Task<MailMessage?> ReadAsync(Connection connection, string messageId, CancellationToken ct = default);

    /// <summary>
    /// Searches a mailbox, following Graph's paging until <paramref name="top"/> results
    /// are in hand, and says whether there were more it did not fetch.
    /// </summary>
    /// <param name="folderId">
    /// Restricts the search to one folder (a well-known name such as <c>inbox</c> works).
    /// Null searches everywhere, including everything already filed.
    /// </param>
    /// <param name="continueFrom">
    /// A <see cref="MailSearchResult.Continuation"/> from an earlier search, to carry on
    /// from where it stopped. Graph refuses <c>$skip</c> and <c>$filter</c> alongside
    /// <c>$search</c>, so this opaque link is the only way to reach a later page at all.
    /// When set, the query and folder are already baked into it and are ignored.
    /// </param>
    Task<MailSearchResult> SearchAsync(
        Connection connection, string query, int top = 25, string? folderId = null,
        string? continueFrom = null, CancellationToken ct = default);

    Task SendAsync(Connection connection, OutgoingMail mail, CancellationToken ct = default);

    /// <summary>
    /// Replies, replies to all, or forwards, keeping the thread and the quoted original.
    /// Returns what actually went out — subject and recipients — so the caller can read
    /// back what it did rather than what it asked for.
    /// </summary>
    Task<ReplySent> SendReplyAsync(
        Connection connection, string messageId, ReplyKind kind, string markdownBody,
        IReadOnlyList<string>? forwardTo = null, RecipientEdit? edit = null,
        CancellationToken ct = default);

    /// <summary>
    /// What clearing out a sender would remove, without removing any of it (OP2-224).
    /// The count is exact; the sender tally may come from a sample.
    /// </summary>
    Task<ClearPreview> PreviewClearAsync(
        Connection connection, ClearTarget target, CancellationToken ct = default);

    /// <summary>
    /// Moves everything matching into Deleted Items, in batches, up to a ceiling per call.
    /// <para>
    /// A move, not a <c>DELETE</c>: Graph's <c>DELETE</c> on a message puts it in
    /// Recoverable Items instead, which is not where anybody looks (OP2-228).
    /// </para>
    /// </summary>
    Task<ClearOutcome> ClearAsync(
        Connection connection, ClearTarget target, int ceiling = 1000, CancellationToken ct = default);

    /// <summary>
    /// Brings a sender's mail back out of Recoverable Items into the Inbox (OP2-228).
    /// </summary>
    Task<ClearOutcome> RecoverAsync(
        Connection connection, string from, int ceiling = 1000, CancellationToken ct = default);

    /// <summary>How many of a sender's messages are sitting in Recoverable Items.</summary>
    Task<int> RecoverableCountAsync(
        Connection connection, string from, CancellationToken ct = default);

    Task MarkReadAsync(Connection connection, string messageId, bool read, CancellationToken ct = default);

    /// <summary>Moves a message. Returns its NEW id — Graph reissues one on every move.</summary>
    Task<string> MoveAsync(
        Connection connection, string messageId, string destinationFolderId, CancellationToken ct = default);

    Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Connection connection, CancellationToken ct = default);

    /// <summary>
    /// Creates a folder, at the top level or under <paramref name="parentFolderId"/>
    /// (which may be a well-known name such as <c>inbox</c>).
    /// </summary>
    Task<MailFolder> CreateFolderAsync(
        Connection connection, string name, string? parentFolderId = null,
        CancellationToken ct = default);

    Task<MailFolder> RenameFolderAsync(
        Connection connection, string folderId, string name, CancellationToken ct = default);

    /// <summary>
    /// Deletes a folder and everything in it. Graph puts it in Deleted Items rather than
    /// destroying it, which is the only reason this is offered at all.
    /// </summary>
    Task DeleteFolderAsync(Connection connection, string folderId, CancellationToken ct = default);

    /// <summary>Blocks the sender and files the message as junk. Returns its new id.</summary>
    Task<string?> MarkAsJunkAsync(Connection connection, string messageId, CancellationToken ct = default);

    Task FlagAsync(
        Connection connection, string messageId, bool flagged, DateTimeOffset? dueAt,
        CancellationToken ct = default);

    /// <summary>The categories currently on a message. Needed to add or remove one without clobbering the rest.</summary>
    Task<IReadOnlyList<string>> GetCategoriesAsync(
        Connection connection, string messageId, CancellationToken ct = default);

    Task SetCategoriesAsync(
        Connection connection, string messageId, IReadOnlyList<string> categories, CancellationToken ct = default);

    /// <summary>
    /// The categories defined on the mailbox. NULL when the connection cannot see them —
    /// the palette lives in mailbox settings, not in mail, so a connection consented
    /// before MailboxSettings was requested is refused. Distinct from an empty list, which
    /// means the mailbox genuinely has none.
    /// </summary>
    Task<IReadOnlyList<MailCategory>?> ListCategoriesAsync(Connection connection, CancellationToken ct = default);

    /// <summary>Null when the mailbox's palette is not readable; see <see cref="ListCategoriesAsync"/>.</summary>
    Task<MailCategory?> CreateCategoryAsync(
        Connection connection, string name, string preset, CancellationToken ct = default);

    Task<IReadOnlyList<MailAttachment>> ListAttachmentsAsync(
        Connection connection, string messageId, CancellationToken ct = default);

    /// <summary>Downloads one attachment's bytes. Null when it has none to give.</summary>
    Task<Stream?> OpenAttachmentAsync(
        Connection connection, string messageId, string attachmentId, CancellationToken ct = default);
}

public sealed class MailService(
    IMicrosoftConnectionServiceAccessor tokens,
    IHttpClientFactory httpFactory,
    ILogger<MailService> log,
    Func<TimeSpan, CancellationToken, Task>? sleep = null) : IMailService
{
    /// <summary>
    /// How a throttle is waited out. Injectable only so the tests for it do not actually
    /// sleep — a unit suite that waits six real seconds to prove a backoff is a suite
    /// people start skipping.
    /// </summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _sleep =
        sleep ?? ((wait, ct) => Task.Delay(wait, ct));

    private const string Graph = "https://graph.microsoft.com/v1.0";

    /// <summary>
    /// The most a single search will fetch, however many are asked for. Bulk filing is a
    /// real use (OP2-189), so it is well above a page; it is still bounded, because every
    /// result becomes a handle that has to be held for the session and read back to
    /// somebody who is driving.
    /// </summary>
    public const int MaxSearchResults = 200;

    /// <summary>
    /// How many pages a single search will walk. Only a guard: the result count is what
    /// normally ends the walk.
    /// </summary>
    private const int MaxSearchPages = 25;

    /// <summary>
    /// The most folders a mailbox will be reported as having. A real mailbox here holds 112,
    /// so this is a guard against a pathological tree rather than a limit anyone meets.
    /// </summary>
    private const int MaxFolders = 1000;

    /// <summary>
    /// How many requests the folder walk will spend. One per folder that has children the
    /// expand did not reach, so a deep mailbox costs more than a flat one — and a folder
    /// nobody can name is worse than a walk that stops, so this is generous.
    /// </summary>
    private const int MaxFolderRequests = 60;

    private async Task<HttpClient> ClientAsync(Connection connection, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(connection, ct)
            ?? throw new MailAuthRequiredException(connection.Label);

        var http = httpFactory.CreateClient("graph");
        http.BaseAddress = new Uri(Graph + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<IReadOnlyList<MailSummary>> ListAsync(
        Connection connection, string folder = "inbox", int top = 25, bool unreadOnly = false,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var q = $"me/mailFolders/{Uri.EscapeDataString(folder)}/messages" +
                $"?$top={Math.Clamp(top, 1, 100)}" +
                "&$orderby=receivedDateTime desc" +
                "&$select=id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";

        if (unreadOnly) q += "&$filter=isRead eq false";

        var page = await http.GetFromJsonAsync<GraphList<GraphMessage>>(q, ct);
        return (page?.Value ?? []).Select(m => m.ToSummary(connection.Label)).ToList();
    }

    public async Task<MailMessage?> ReadAsync(Connection connection, string messageId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // uniqueBody, not body. It returns ONLY the new content, stripping the quoted reply
        // history — the single highest-value change on the read path. Without it a
        // ten-reply thread dumps ten copies of itself into context.
        var q = $"me/messages/{Uri.EscapeDataString(messageId)}" +
                "?$select=id,subject,from,toRecipients,ccRecipients,receivedDateTime,sentDateTime,isRead," +
                "hasAttachments,uniqueBody,body,conversationId";

        using var resp = await http.GetAsync(q, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var m = await resp.Content.ReadFromJsonAsync<GraphMessage>(ct);
        if (m is null) return null;

        // Prefer uniqueBody; fall back to body when Graph omits it (it is absent on some
        // single-message conversations).
        var source = m.UniqueBody ?? m.Body;
        var text = string.Equals(source?.ContentType, "html", StringComparison.OrdinalIgnoreCase)
            ? HtmlToText.Convert(source?.Content)
            : (source?.Content ?? "");

        text = HtmlToText.TrimSignature(text);

        return new MailMessage
        {
            Id = m.Id,
            ConnectionLabel = connection.Label,
            Subject = m.Subject ?? "(no subject)",
            From = m.From?.EmailAddress?.Format() ?? "(unknown)",
            To = (m.ToRecipients ?? []).Select(r => r.EmailAddress?.Format() ?? "").Where(x => x != "").ToList(),
            Cc = (m.CcRecipients ?? []).Select(r => r.EmailAddress?.Format() ?? "").Where(x => x != "").ToList(),
            ReceivedAt = m.ReceivedDateTime,
            SentAt = m.SentDateTime,
            IsRead = m.IsRead,
            HasAttachments = m.HasAttachments,
            Body = text,
            QuotedHistoryStripped = m.UniqueBody is not null,
        };
    }

    public async Task<MailSearchResult> SearchAsync(
        Connection connection, string query, int top = 25, string? folderId = null,
        string? continueFrom = null, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var wanted = Math.Clamp(top, 1, MaxSearchResults);

        // A continuation is Graph's own link, handed back to us. It already carries the
        // query, the folder and the page size, so none of those are rebuilt here — and it
        // is only ever a link Graph gave us, never one a caller composed.
        if (continueFrom is not null)
        {
            if (!continueFrom.StartsWith(Graph + "/", StringComparison.OrdinalIgnoreCase))
                throw new MailException("That continuation is not one of ours.");

            return await WalkAsync(http, connection, continueFrom, wanted, ct);
        }

        // Scoped to one folder when asked. $search over /me/messages covers the whole
        // mailbox including everything already filed, so a clean-up pass could not tell
        // what was still in the Inbox from what it had just moved out (OP2-196).
        var where = folderId is null
            ? "me/messages"
            : $"me/mailFolders/{Uri.EscapeDataString(folderId)}/messages";

        // $search cannot be combined with $orderby in Graph; attempting it returns a
        // confusing 400 rather than ignoring the sort.
        //
        // $top is a PAGE size here, not a total: Graph caps a search page well below 100
        // and hands back @odata.nextLink for the rest, which is why asking for 200 used to
        // yield 50 and no way to see the others (OP2-189).
        var url = $"{where}?$search={Uri.EscapeDataString($"\"{query}\"")}" +
                  $"&$top={Math.Min(wanted, 100)}" +
                  "&$select=id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview,parentFolderId";

        return await WalkAsync(http, connection, url, wanted, ct);
    }

    /// <summary>
    /// Walks pages from <paramref name="url"/> until it has <paramref name="wanted"/>
    /// results, and reports where it stopped.
    /// <para>
    /// Bounded on both ends: by what was asked for, and by a page count. A page that comes
    /// back empty but still carries a nextLink would otherwise loop for ever on a remote
    /// service's say-so, and the symptom is a tool call that never returns while someone is
    /// driving. 200 results arrive in two or three pages, so reaching the cap means Graph
    /// is behaving strangely rather than the caller asking for a lot.
    /// </para>
    /// </summary>
    private async Task<MailSearchResult> WalkAsync(
        HttpClient http, Connection connection, string url, int wanted, CancellationToken ct)
    {
        var all = new List<MailSummary>();
        var more = false;

        string? next = url;

        for (var page = 0; page < MaxSearchPages && next is not null && all.Count < wanted; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);

            // Required by $search on messages, and on every page — a nextLink carries the
            // query but not the headers.
            req.Headers.Add("ConsistencyLevel", "eventual");

            using var resp = await http.SendAsync(req, ct);

            // Was EnsureSuccessStatusCode, which threw a bare HttpRequestException and so
            // skipped the "needs re-authorising" path that every other call here takes.
            await EnsureAsync(resp, connection, "could not search that mailbox", ct);

            var body = await resp.Content.ReadFromJsonAsync<GraphList<GraphMessage>>(ct);

            foreach (var m in body?.Value ?? [])
            {
                if (all.Count == wanted)
                {
                    // One more came back than was wanted, so there is definitely another —
                    // whether or not a nextLink came with it.
                    more = true;
                    break;
                }
                all.Add(m.ToSummary(connection.Label));
            }

            next = body?.NextLink;
        }

        // Anything left unfollowed — a nextLink we stopped short of, or the page cap — is
        // "there is more". Saying nothing here is the failure OP2-189 reported, and keeping
        // the link is what makes the next page reachable rather than only countable
        // (OP2-208).
        return new MailSearchResult
        {
            Items = all,
            More = more || next is not null,
            Continuation = next,
        };
    }

    public async Task SendAsync(Connection connection, OutgoingMail mail, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // HTML by default. The model writes Markdown, which we convert and sanitise
        // server-side rather than asking it to emit HTML — more reliable, and it means we
        // control exactly which tags go out under the user's name.
        var (contentType, content) = mail.IsHtml
            ? ("HTML", MarkdownToHtml.Render(mail.Body))
            : ("Text", mail.Body);

        var payload = new
        {
            message = new
            {
                subject = mail.Subject,
                body = new { contentType, content },
                toRecipients = mail.To.Select(Recipient).ToArray(),
                ccRecipients = mail.Cc.Select(Recipient).ToArray(),
            },
            saveToSentItems = true,
        };

        using var resp = await http.PostAsJsonAsync("me/sendMail", payload, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            log.LogError("sendMail failed for {Label}: HTTP {Status} {Body}",
                connection.Label, (int)resp.StatusCode, body);
            throw new MailException($"Could not send from {connection.Label}: {Describe(resp.StatusCode, body)}");
        }

        log.LogInformation("Sent mail from {Label} to {Count} recipient(s).", connection.Label, mail.To.Count);

        static object Recipient(string address) => new { emailAddress = new { address } };
    }

    /// <summary>
    /// Works out who a reply goes to, given the recipients Graph composed and the caller's
    /// changes (OP2-187). Pure, and tested as such: this is the function that decides
    /// whether a person stays on a thread.
    /// </summary>
    public static RecipientPlan PlanRecipients(
        IReadOnlyList<MailRecipient> to, IReadOnlyList<MailRecipient> cc, RecipientEdit? edit)
    {
        if (edit is null || !edit.Any)
            return new RecipientPlan { To = to, Cc = cc };

        var dropped = new List<MailRecipient>();
        var unmatched = new List<string>();

        foreach (var term in edit.Drop.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var hits = to.Concat(cc).Where(r => Names(r, term)).ToList();
            if (hits.Count == 0) unmatched.Add(term.Trim());
            else dropped.AddRange(hits);
        }

        var nextTo = to.Where(r => !dropped.Contains(r)).ToList();
        var nextCc = cc.Where(r => !dropped.Contains(r)).ToList();

        // An add that is already on the message is a no-op rather than a duplicate. The
        // ordinary way to correct an address is drop-then-add, and the corrected one is
        // frequently a near-miss of the wrong one.
        foreach (var address in edit.AddTo.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!Present(nextTo, nextCc, address)) nextTo.Add(ParseRecipient(address));

        foreach (var address in edit.AddCc.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!Present(nextTo, nextCc, address)) nextCc.Add(ParseRecipient(address));

        return new RecipientPlan
        {
            To = nextTo,
            Cc = nextCc,
            Dropped = dropped.Distinct().ToList(),
            Unmatched = unmatched,
        };

        // Exact on the address, the display name, or the "Name <addr>" form a listing shows
        // — and nothing else. A substring match would let "bob" take bobby@ off the thread.
        static bool Names(MailRecipient r, string term) =>
            Eq(r.Address, term) || Eq(r.Name, term) || Eq(r.Display, term);

        static bool Present(List<MailRecipient> to, List<MailRecipient> cc, string address) =>
            to.Concat(cc).Any(r => Eq(r.Address, address) || Eq(r.Display, address));

        static bool Eq(string? a, string? b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ReplySent> SendReplyAsync(
        Connection connection, string messageId, ReplyKind kind, string markdownBody,
        IReadOnlyList<string>? forwardTo = null, RecipientEdit? edit = null,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var verb = kind switch
        {
            ReplyKind.All => "createReplyAll",
            ReplyKind.Forward => "createForward",
            _ => "createReply",
        };

        // Three calls rather than one, and deliberately so. The one-call form takes a
        // `comment` string which Graph splices into the HTML body as raw text — every line
        // break collapses and anything that looks like markup is at the mercy of the
        // renderer. Creating the draft gives us Graph's own correctly-quoted thread, which
        // we then prepend to, so the reply reads like one a person wrote.
        using var draftResp = await http.PostAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}/{verb}", content: null, ct);

        await EnsureAsync(draftResp, connection, $"could not start that {Describe(kind)}", ct);

        var draft = await draftResp.Content.ReadFromJsonAsync<GraphMessage>(ct)
            ?? throw new MailException($"{connection.Label} returned an empty draft.");

        // Everything from here is inside the try, so that a refusal of ours does not leave
        // a half-written draft sitting in the user's Drafts folder. A voice user who gets
        // the name wrong twice would otherwise be cleaning up by hand later.
        try
        {
            // A forward draft arrives with no recipients at all, so there its list is the
            // only thing that makes it sendable; a reply arrives with Graph's own, which is
            // the set the caller is editing.
            var plan = kind == ReplyKind.Forward
                ? PlanRecipients(
                    (forwardTo ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(ParseRecipient).ToList(),
                    [], edit)
                : PlanRecipients(Recipients(draft.ToRecipients), Recipients(draft.CcRecipients), edit);

            if (plan.Unmatched.Count > 0)
                throw new MailException(
                    $"Nothing was sent: nobody on that {Describe(kind)} is "
                    + $"\"{string.Join("\" or \"", plan.Unmatched)}\". It would go to "
                    + $"{Join(plan.To)}{(plan.Cc.Count > 0 ? $", copying {Join(plan.Cc)}" : "")}. "
                    + "Name one of those exactly, or leave it off to keep everyone.");

            if (plan.To.Count == 0)
                throw new MailException(
                    $"Nothing was sent: that would leave the {Describe(kind)} with no "
                    + "recipients at all."
                    + (kind == ReplyKind.Forward ? "" : " Add someone, or take fewer people off."));

            var patch = new Dictionary<string, object?>
            {
                ["body"] = new
                {
                    contentType = "HTML",
                    content = Prepend(MarkdownToHtml.Render(markdownBody), draft.Body?.Content),
                },
            };

            // Written back only when they changed, so an ordinary reply still sends exactly
            // the draft Graph composed.
            if (kind == ReplyKind.Forward || (edit is not null && edit.Any))
            {
                patch["toRecipients"] = plan.To.Select(Wire).ToArray();
                patch["ccRecipients"] = plan.Cc.Select(Wire).ToArray();
            }

            using var patchResp = await http.PatchAsJsonAsync(
                $"me/messages/{Uri.EscapeDataString(draft.Id)}", patch, ct);
            await EnsureAsync(patchResp, connection, "could not write the reply body", ct);

            using var sendResp = await http.PostAsync(
                $"me/messages/{Uri.EscapeDataString(draft.Id)}/send", content: null, ct);
            await EnsureAsync(sendResp, connection, $"could not send that {Describe(kind)}", ct);

            log.LogInformation("Sent a {Kind} from {Label} to {Count} recipient(s).",
                verb, connection.Label, plan.To.Count + plan.Cc.Count);

            return new ReplySent
            {
                Subject = draft.Subject ?? "(no subject)",
                To = plan.To,
                Cc = plan.Cc,
                Dropped = plan.Dropped,
            };
        }
        catch
        {
            await TryDiscardAsync(http, draft.Id);
            throw;
        }

        static string Describe(ReplyKind k) => k == ReplyKind.Forward ? "forward" : "reply";

        static object Wire(MailRecipient r) =>
            new { emailAddress = new { address = r.Address, name = r.Name } };

        static string Join(IEnumerable<MailRecipient> rs) => string.Join(", ", rs.Select(r => r.Display));
    }

    private static List<MailRecipient> Recipients(List<GraphRecipient>? from) =>
        (from ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.EmailAddress?.Address))
            .Select(r => new MailRecipient(r.EmailAddress!.Name, r.EmailAddress.Address!))
            .ToList();

    /// <summary>
    /// Accepts a bare address or the <c>Name &lt;addr&gt;</c> form a listing printed, because
    /// a caller correcting a recipient naturally pastes back what it was shown.
    /// </summary>
    public static MailRecipient ParseRecipient(string said)
    {
        var s = said.Trim();
        var open = s.LastIndexOf('<');
        var close = s.LastIndexOf('>');

        return open >= 0 && close > open
            ? new MailRecipient(s[..open].Trim().Trim('"'), s[(open + 1)..close].Trim())
            : new MailRecipient(null, s);
    }

    /// <summary>
    /// Best-effort cleanup of a draft we created and then refused to send. Never throws:
    /// it runs on the failure path, and a failed tidy-up must not replace the real reason.
    /// </summary>
    private async Task TryDiscardAsync(HttpClient http, string draftId)
    {
        try
        {
            using var resp = await http.DeleteAsync($"me/messages/{Uri.EscapeDataString(draftId)}");
            if (!resp.IsSuccessStatusCode)
                log.LogWarning("Could not discard draft {Id}: HTTP {Status}", draftId, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not discard draft {Id}.", draftId);
        }
    }

    /// <summary>
    /// Puts our text above Graph's quoted thread. Inserting after &lt;body&gt; rather than
    /// concatenating matters: a reply whose new text lands after the quoted history, or
    /// outside the document entirely, reads as an empty message with a thread attached.
    /// </summary>
    public static string Prepend(string html, string? draftBody)
    {
        if (string.IsNullOrWhiteSpace(draftBody)) return html;

        var open = draftBody.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            var close = draftBody.IndexOf('>', open);
            if (close > 0)
                return draftBody[..(close + 1)] + html + draftBody[(close + 1)..];
        }

        return html + draftBody;
    }

    public async Task MarkReadAsync(
        Connection connection, string messageId, bool read, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        using var resp = await http.PatchAsJsonAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}", new { isRead = read }, ct);

        await EnsureAsync(resp, connection, read ? "could not mark that read" : "could not mark that unread", ct);
    }

    public async Task<string> MoveAsync(
        Connection connection, string messageId, string destinationFolderId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        using var resp = await http.PostAsJsonAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}/move",
            new { destinationId = destinationFolderId }, ct);

        await EnsureAsync(resp, connection, "could not move that message", ct);

        // Graph reissues the id on a move. Returning the old one would leave the caller
        // holding a handle to a message that no longer exists at that address.
        var moved = await resp.Content.ReadFromJsonAsync<GraphMessage>(ct);
        return moved?.Id ?? messageId;
    }

    /// <summary>
    /// Every folder in the mailbox, parents before their children.
    /// <para>
    /// This used to be one request with <c>$expand=childFolders</c>. OP2-213 is what that
    /// cost, and measuring it found two separate causes where the ticket described one
    /// symptom — a folder visible in Outlook, already filed into earlier in the same
    /// session, refused as nonexistent:
    /// </para>
    /// <list type="number">
    /// <item><b>The expanded collection stops at ten.</b> The mailbox that reported it has
    /// an Inbox with eleven children, and the eleventh was the folder in question. Graph
    /// offers no <c>childFolders@odata.nextLink</c> to carry on with, and it answers
    /// <c>200</c> to a nested <c>$expand=childFolders($top=200)</c> while returning the same
    /// ten — accepted and ignored, which is worse than a refusal.</item>
    /// <item><b>The expand reaches one level down.</b> Another mailbox here has 84 folders
    /// under a single one of them; 87 of its 112 were below the horizon entirely.</item>
    /// </list>
    /// <para>
    /// So the expand is kept, because it still earns its place by bringing the second level
    /// back on the first request, and then the children of any folder that says it has more
    /// than arrived are asked for directly. <c>childFolderCount</c> comes from the folder
    /// itself, so the walk knows what it is still missing rather than guessing — and the
    /// direct collection does page, so <c>@odata.nextLink</c> is followed at both levels.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MailFolder>> ListFoldersAsync(
        Connection connection, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var folders = new List<MailFolder>();

        // Folders known to have children that have not been listed yet. Appended to while
        // it is walked, which makes the walk breadth-first: the shallow folders people
        // actually name are listed even if a deep corner of the mailbox runs the budget out.
        var pending = new List<MailFolder>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        var requests = 0;

        // $expand with no nested options, and no $select beside it. Graph rejects
        // $select + $expand on mailFolders outright with a 400 — so the shape here is the
        // one it accepts, not the narrowest one that looks reasonable.
        var next = "me/mailFolders?$top=100&$expand=childFolders";

        while (next is not null && requests < MaxFolderRequests && folders.Count < MaxFolders)
        {
            var page = await FolderPageAsync(http, connection, next, ct);
            requests++;

            foreach (var f in page?.Value ?? []) Add(f, null);
            next = page?.NextLink;
        }

        for (var i = 0; i < pending.Count && folders.Count < MaxFolders; i++)
        {
            var parent = pending[i];
            var url = $"me/mailFolders/{Uri.EscapeDataString(parent.Id)}/childFolders?$top=100";
            var got = 0;

            while (url is not null && requests < MaxFolderRequests && folders.Count < MaxFolders)
            {
                var page = await FolderPageAsync(http, connection, url, ct);
                requests++;

                foreach (var f in page?.Value ?? []) { Add(f, parent.Path); got++; }
                url = page?.NextLink;
            }

            // Only when the asking finished. Graph's childFolderCount can simply be wrong —
            // this mailbox has a Conversation History that claims one child and returns none,
            // even with includeHiddenFolders=true — and a count nobody can act on makes the
            // surface warn, for ever, that it cannot see everything. So record what was
            // actually there. A folder cut off by the budget keeps Graph's number instead,
            // which is what lets the surface tell the two cases apart.
            if (url is null && got != parent.ChildCount && at.TryGetValue(parent.Id, out var slot))
                folders[slot] = folders[slot] with { ChildCount = got };
        }

        return folders;

        void Add(GraphFolder f, string? parentPath)
        {
            // A folder reached twice would be listed twice and walked twice. Nothing in the
            // tree should repeat, but the budget is spent on the assumption that it cannot.
            if (folders.Count >= MaxFolders || !seen.Add(f.Id)) return;

            var mapped = f.ToFolder(parentPath);
            at[mapped.Id] = folders.Count;
            folders.Add(mapped);

            var inline = f.ChildFolders ?? [];
            foreach (var child in inline) Add(child, mapped.Path);

            if (f.ChildFolderCount > inline.Count) pending.Add(mapped);
        }
    }

    // ── clearing out a sender (OP2-224) ──────────────────────────────────────

    /// <summary>
    /// The most a single <c>$batch</c> may carry. Graph's own limit, measured rather than
    /// recalled: 20 is accepted and 21 comes back
    /// <c>400 "Number of requests inside batch exceed the limit."</c>
    /// </summary>
    public const int BatchLimit = 20;

    /// <summary>
    /// The <c>$filter</c> for a sender, as a spoken term rather than an exact address.
    /// <para>
    /// Which operators are available is the service's decision and was asked live:
    /// <c>eq</c>, <c>startsWith</c> and <c>contains</c> on the address all work;
    /// <c>endsWith</c> is refused as <c>ErrorInvalidUrlQueryFilter</c>; and any
    /// <c>$orderby</c> next to a filter is refused as <c>InefficientFilter</c>, so these
    /// results cannot be sorted at all.
    /// </para>
    /// <para>
    /// An '@' means the user named an address, and an address is matched exactly — nobody
    /// says a full address by accident, and <c>contains</c> on one would make
    /// "a@b.example" also match "xa@b.example.net". Anything else is a fragment and is
    /// matched against the address, because a display name is chosen by the sender and
    /// "LinkedIn" in the name proves nothing about who sent it.
    /// </para>
    /// </summary>
    public static string SenderFilter(ClearTarget target)
    {
        var term = target.From.Trim();

        // Doubling is OData's own escape. Without it an address containing a quote closes
        // the literal early and the rest of it is read as filter syntax.
        var safe = term.Replace("'", "''");

        var clause = term.Contains('@')
            ? $"from/emailAddress/address eq '{safe}'"
            : $"contains(from/emailAddress/address,'{safe}')";

        if (target.Before is { } before)
            clause += $" and receivedDateTime lt {before.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";

        return clause;
    }

    /// <summary>
    /// Outlook's own Deleted Items, by its well-known name. Graph takes one of these
    /// wherever it takes a folder id.
    /// </summary>
    private const string DeletedItems = "deleteditems";

    private static string ScopePath(string? folderId) =>
        folderId is null
            ? "me/messages"
            : $"me/mailFolders/{Uri.EscapeDataString(folderId)}/messages";

    /// <summary>The scopes to walk: each folder in turn, or the whole mailbox.</summary>
    private static IReadOnlyList<string?> Scopes(ClearTarget target) =>
        target.FolderIds is null or { Count: 0 }
            ? [null]
            : [.. target.FolderIds];

    private async Task<int> CountAsync(
        HttpClient http, Connection connection, string? folderId, string filter,
        CancellationToken ct)
    {
        using var resp = await SendAsync(http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"{ScopePath(folderId)}?$filter={filter}&$count=true&$top=1&$select=id");

            req.Headers.Add("ConsistencyLevel", "eventual");
            return req;
        }, connection, "counting mail", ct);

        await EnsureAsync(resp, connection, "could not count that mail", ct);

        return (await resp.Content.ReadFromJsonAsync<GraphCountedList<GraphMessage>>(ct))?.Count ?? 0;
    }

    public async Task<ClearPreview> PreviewClearAsync(
        Connection connection, ClearTarget target, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var filter = Uri.EscapeDataString(SenderFilter(target));

        // Exact counts, one request each: the scope, and then the whole mailbox. The
        // difference between them is mail the user has filed somewhere, and OP2-227 is what
        // happens when nobody mentions it — a clear-out that also emptied the folder he had
        // just sorted that sender's receipts into.
        var matched = 0;
        foreach (var scope in Scopes(target))
            matched += await CountAsync(http, connection, scope, filter, ct);

        // Everywhere else, minus the places mail has already been disposed of. Counting
        // Deleted Items here reported "another 1,033 in folders you've filed into — Sync
        // Issues/Conflicts, Deleted Items", which is both a wrong description and a number
        // that would have "everywhere" re-moving a thousand already-deleted messages.
        var everywhere = matched;

        if (target.FolderIds is not (null or { Count: 0 }))
        {
            everywhere = await CountAsync(http, connection, null, filter, ct);

            foreach (var bin in Disposed)
                everywhere -= await CountAsync(http, connection, bin, filter, ct);

            // Each count is taken a moment apart on a live mailbox, so the arithmetic can
            // cross zero. Never report fewer elsewhere than are in scope.
            everywhere = Math.Max(everywhere, matched);
        }

        // A sample from the WHOLE mailbox, so the folders being skipped can be named and the
        // senders tallied. $orderby is refused beside a $filter, so this is whatever order
        // the service chose rather than the newest.
        using var sampleReq = new HttpRequestMessage(HttpMethod.Get,
            "me/messages?$filter=" + filter
            + "&$top=50&$select=id,subject,from,receivedDateTime,parentFolderId");

        sampleReq.Headers.Add("ConsistencyLevel", "eventual");

        using var sampleResp = await http.SendAsync(sampleReq, ct);
        await EnsureAsync(sampleResp, connection, "could not look at that mail", ct);

        var items = (await sampleResp.Content
            .ReadFromJsonAsync<GraphList<GraphMessage>>(ct))?.Value ?? [];

        var senders = items
            .GroupBy(m => m.From?.EmailAddress?.Address ?? "(unknown)", StringComparer.OrdinalIgnoreCase)
            .Select(g => new SenderTally(
                g.First().From?.EmailAddress?.Name ?? g.Key, g.Key, g.Count()))
            .OrderByDescending(s => s.Count)
            .ToList();

        var inScope = target.FolderIds is null or { Count: 0 }
            ? null
            : new HashSet<string>(target.FolderIds, StringComparer.Ordinal);

        var skipped = inScope is null
            ? (IReadOnlyList<string>)[]
            : items
                .Select(m => m.ParentFolderId)
                .Where(f => f is not null && !inScope.Contains(f))
                .Select(f => f!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        return new ClearPreview
        {
            Matched = matched,
            Everywhere = everywhere,
            SkippedFolderIds = skipped,
            Senders = senders,
            Sampled = everywhere > items.Count,
        };
    }

    public async Task<ClearOutcome> ClearAsync(
        Connection connection, ClearTarget target, int ceiling = 1000, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var filter = Uri.EscapeDataString(SenderFilter(target));
        var wanted = Math.Clamp(ceiling, 1, 5000);

        var cleared = 0;
        var failed = 0;

        foreach (var scope in Scopes(target))
        {
            var path = ScopePath(scope);

            // Fetched and moved in turn rather than all ids first: a move changes the
            // collection being paged AND reissues every id, so a nextLink taken before
            // moving walks a list shifting underneath it. Asking for the first page again
            // each time is the only stable way round that, and it is cheap because the page
            // just cleared is no longer in the results.
            while (cleared + failed < wanted)
            {
                var take = Math.Min(BatchLimit, wanted - cleared - failed);

                using var listed = await SendAsync(http, () =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{path}?$filter={filter}&$select=id&$top={take}");

                    req.Headers.Add("ConsistencyLevel", "eventual");
                    return req;
                }, connection, "finding mail to clear", ct, done: cleared, of: wanted);

                await EnsureAsync(listed, connection, "could not find that mail", ct,
                    done: cleared, of: wanted);

                var page = await listed.Content.ReadFromJsonAsync<GraphList<GraphMessage>>(ct);
                var ids = (page?.Value ?? []).Select(m => m.Id).ToList();

                if (ids.Count == 0) break;

                int ok, bad;
                try
                {
                    (ok, bad) = await MoveBatchAsync(http, connection, ids, DeletedItems, ct);
                }
                catch (MailThrottledException throttle)
                {
                    // Stop, but keep what was done. Throwing here would lose the count of
                    // several hundred messages that really did move, and the caller would
                    // start again from the beginning — doing the work twice against the very
                    // limit that just refused it.
                    log.LogWarning(throttle,
                        "Throttled part-way through clearing {Label}: {Cleared} moved.",
                        connection.Label, cleared);

                    return new ClearOutcome
                    {
                        Deleted = cleared,
                        Failed = failed,
                        Remaining = 0,
                        Throttled = true,
                        RetryAfter = throttle.RetryAfter,
                    };
                }

                cleared += ok;
                failed += bad;

                // Every id in the page was refused, so asking again returns the same page
                // and this would spin for ever inside a tool call nobody can interrupt.
                if (ok == 0) break;
            }
        }

        // What is left, asked for separately: the caller has to be able to say "and there
        // are still 600 to go" rather than implying the job is finished.
        var remaining = 0;
        if (cleared + failed >= wanted)
            foreach (var scope in Scopes(target))
                remaining += await CountAsync(http, connection, scope, filter, ct);

        log.LogInformation(
            "Cleared {Cleared} message(s) from \"{From}\" to Deleted Items in {Label}; "
            + "{Failed} refused, {Remaining} left.",
            cleared, target.From, connection.Label, failed, remaining);

        return new ClearOutcome { Deleted = cleared, Failed = failed, Remaining = remaining };
    }

    /// <summary>
    /// Everything of this sender's sitting in Recoverable Items, moved back to the Inbox
    /// (OP2-228).
    /// <para>
    /// This exists because the first version of <c>clear_mail</c> used <c>DELETE</c>, which
    /// does not do what its name suggests, and 188 of the user's messages went somewhere he
    /// was told they had not gone. They are still there — Recoverable Items keeps them for a
    /// retention window — and this is how they come back.
    /// </para>
    /// </summary>
    public async Task<ClearOutcome> RecoverAsync(
        Connection connection, string from, int ceiling = 1000, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var filter = Uri.EscapeDataString(SenderFilter(new ClearTarget { From = from }));
        var wanted = Math.Clamp(ceiling, 1, 5000);

        var back = 0;
        var failed = 0;

        while (back + failed < wanted)
        {
            var take = Math.Min(BatchLimit, wanted - back - failed);

            using var listed = await SendAsync(http, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"me/mailFolders/{Recoverable}/messages?$filter={filter}&$select=id&$top={take}");

                req.Headers.Add("ConsistencyLevel", "eventual");
                return req;
            }, connection, "finding mail to recover", ct, done: back, of: wanted);

            await EnsureAsync(listed, connection, "could not look in Recoverable Items", ct,
                done: back, of: wanted);

            var page = await listed.Content.ReadFromJsonAsync<GraphList<GraphMessage>>(ct);
            var ids = (page?.Value ?? []).Select(m => m.Id).ToList();

            if (ids.Count == 0) break;

            int ok, bad;
            try
            {
                (ok, bad) = await MoveBatchAsync(http, connection, ids, "inbox", ct);
            }
            catch (MailThrottledException throttle)
            {
                log.LogWarning(throttle,
                    "Throttled part-way through recovering {Label}: {Back} restored.",
                    connection.Label, back);

                return new ClearOutcome
                {
                    Deleted = back,
                    Failed = failed,
                    Throttled = true,
                    RetryAfter = throttle.RetryAfter,
                };
            }

            back += ok;
            failed += bad;

            if (ok == 0) break;
        }

        var remaining = 0;
        if (back + failed >= wanted)
            remaining = await CountAsync(http, connection, Recoverable, filter, ct);

        log.LogInformation(
            "Recovered {Back} message(s) from \"{From}\" in {Label}; {Failed} refused.",
            back, from, connection.Label, failed);

        return new ClearOutcome { Deleted = back, Failed = failed, Remaining = remaining };
    }

    /// <summary>How many of this sender's messages are sitting in Recoverable Items.</summary>
    public async Task<int> RecoverableCountAsync(
        Connection connection, string from, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        return await CountAsync(http, connection, Recoverable,
            Uri.EscapeDataString(SenderFilter(new ClearTarget { From = from })), ct);
    }

    /// <summary>
    /// Where a <c>DELETE</c> on a message really puts it, which is not Deleted Items. Graph
    /// exposes it as a well-known folder, which is the only reason OP2-228 is recoverable
    /// rather than simply a loss.
    /// </summary>
    /// <summary>
    /// Folders whose contents the user has already finished with. Excluded from "and there
    /// are more elsewhere", because mail in the bin is not mail that has been filed.
    /// </summary>
    private static readonly string[] Disposed = ["deleteditems", "junkemail"];

    private const string Recoverable = "recoverableitemsdeletions";

    /// <summary>
    /// One <c>$batch</c> of moves. Returns how many moved and how many were refused.
    /// <para>
    /// A move and not a <c>DELETE</c>, and that is the whole of OP2-228. <c>DELETE
    /// /me/messages/{id}</c> answers <c>204</c> and puts the message in <b>Recoverable
    /// Items</b>, not Deleted Items — measured on the mailbox that reported it, where 188
    /// messages this tool had called recoverable "in Deleted Items" were not there at all.
    /// <c>POST /move</c> to <c>deleteditems</c> puts it where the user looks, and leaves it
    /// there until they empty the folder themselves.
    /// </para>
    /// <para>
    /// Success is read from each sub-request's own status, never from a follow-up search. A
    /// <c>$filter</c> run straight after a move found the message on one run and not on the
    /// next, so checking that way would invent failures; the move's own 201 is authoritative
    /// and carries the message's new id.
    /// </para>
    /// </summary>
    private async Task<(int Ok, int Failed)> MoveBatchAsync(
        HttpClient http, Connection connection, IReadOnlyList<string> ids, string destination,
        CancellationToken ct)
    {
        var payload = new
        {
            requests = ids.Select((id, i) => new
            {
                id = (i + 1).ToString(),
                method = "POST",
                url = $"/me/messages/{Uri.EscapeDataString(id)}/move",
                body = new { destinationId = destination },
                headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            }).ToArray(),
        };

        const int attempts = 5;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var resp = await http.PostAsJsonAsync("$batch", payload, ct);

            // Bulk work is exactly what Graph throttles. Retrying the whole batch is safe: a
            // move that already happened reissued the id, so the old one answers 404 and is
            // counted as refused — the right answer for a message no longer where it was.
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(resp) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

                // Wait only while waiting is still cheap. Past that the caller is better
                // served by being told to come back: somebody is listening to this, and a
                // tool call that sits silent for two minutes has already failed them even
                // if it eventually succeeds.
                if (attempt >= attempts - 1 || wait > TimeSpan.FromSeconds(20))
                {
                    log.LogWarning("Giving the throttle back to the caller for {Label}.",
                        connection.Label);

                    throw new MailThrottledException(connection.Label, wait);
                }

                log.LogWarning("Throttled moving mail in {Label}; waiting {Seconds}s.",
                    connection.Label, wait.TotalSeconds);

                await _sleep(wait, ct);
                continue;
            }

            await EnsureAsync(resp, connection, "could not move that mail", ct);

            var answer = await resp.Content.ReadFromJsonAsync<GraphBatchReplies>(ct);
            var ok = 0;
            var failed = 0;

            foreach (var r in answer?.Responses ?? [])
                if (r.Status is >= 200 and < 300) ok++;
                else failed++;

            return (ok, failed);
        }

        // Unreachable: the loop either returns or throws. Here for the compiler.
        throw new MailThrottledException(connection.Label, null);
    }

    private async Task<GraphList<GraphFolder>?> FolderPageAsync(
        HttpClient http, Connection connection, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        await EnsureAsync(resp, connection, "could not list the folders", ct);

        return await resp.Content.ReadFromJsonAsync<GraphList<GraphFolder>>(ct);
    }

    public async Task<MailFolder> CreateFolderAsync(
        Connection connection, string name, string? parentFolderId = null, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // Graph takes a well-known name wherever it takes a folder id, so "under the inbox"
        // needs no lookup — and under the inbox is where people keep filing folders.
        var path = parentFolderId is null
            ? "me/mailFolders"
            : $"me/mailFolders/{Uri.EscapeDataString(parentFolderId)}/childFolders";

        using var resp = await http.PostAsJsonAsync(path, new { displayName = name }, ct);
        await EnsureAsync(resp, connection, $"could not create a folder called \"{name}\"", ct);

        var f = await resp.Content.ReadFromJsonAsync<GraphFolder>(ct);
        return f?.ToFolder(null) ?? new MailFolder { Id = "", Name = name, Path = name };
    }

    public async Task<MailFolder> RenameFolderAsync(
        Connection connection, string folderId, string name, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        using var resp = await http.PatchAsJsonAsync(
            $"me/mailFolders/{Uri.EscapeDataString(folderId)}", new { displayName = name }, ct);
        await EnsureAsync(resp, connection, $"could not rename that folder to \"{name}\"", ct);

        var f = await resp.Content.ReadFromJsonAsync<GraphFolder>(ct);
        return f?.ToFolder(null) ?? new MailFolder { Id = folderId, Name = name, Path = name };
    }

    public async Task DeleteFolderAsync(
        Connection connection, string folderId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        using var resp = await http.DeleteAsync(
            $"me/mailFolders/{Uri.EscapeDataString(folderId)}", ct);
        await EnsureAsync(resp, connection, "could not delete that folder", ct);

        log.LogInformation("Deleted a mail folder in {Label}.", connection.Label);
    }

    public async Task<string?> MarkAsJunkAsync(
        Connection connection, string messageId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // markAsJunk does two things a plain move cannot: it adds the sender to the blocked
        // list and it tells Exchange's filter it got one wrong. That is the whole point of
        // reporting rather than deleting.
        using var resp = await http.PostAsJsonAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}/markAsJunk",
            new { moveToJunk = true }, ct);

        await EnsureAsync(resp, connection, "could not report that as junk", ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;

        var moved = await resp.Content.ReadFromJsonAsync<GraphMessage>(ct);
        return moved?.Id;
    }

    public async Task FlagAsync(
        Connection connection, string messageId, bool flagged, DateTimeOffset? dueAt,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        object flag = flagged
            ? dueAt is null
                ? new { flagStatus = "flagged" }
                : new
                {
                    flagStatus = "flagged",
                    // Graph rejects a due date with no start, and a reminder that was due
                    // before it began is nonsense anyway.
                    startDateTime = Stamp(DateTimeOffset.UtcNow),
                    dueDateTime = Stamp(dueAt.Value),
                }
            : new { flagStatus = "notFlagged" };

        using var resp = await http.PatchAsJsonAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}", new { flag }, ct);

        await EnsureAsync(resp, connection, flagged ? "could not flag that" : "could not clear that flag", ct);

        static object Stamp(DateTimeOffset t) => new
        {
            dateTime = t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
            timeZone = "UTC",
        };
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(
        Connection connection, string messageId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        var m = await http.GetFromJsonAsync<GraphCategorised>(
            $"me/messages/{Uri.EscapeDataString(messageId)}?$select=categories", ct);
        return m?.Categories ?? [];
    }

    public async Task SetCategoriesAsync(
        Connection connection, string messageId, IReadOnlyList<string> categories,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        using var resp = await http.PatchAsJsonAsync(
            $"me/messages/{Uri.EscapeDataString(messageId)}", new { categories }, ct);

        await EnsureAsync(resp, connection, "could not change the categories", ct);
    }

    public async Task<IReadOnlyList<MailCategory>?> ListCategoriesAsync(
        Connection connection, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        using var resp = await http.GetAsync("me/outlook/masterCategories?$top=200", ct);

        // Reported as "cannot see the palette" rather than as a failure. Applying a
        // category to a message works under Mail.ReadWrite alone, so a 403 here must not
        // take the whole of categorising down with it.
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            log.LogInformation(
                "{Label} cannot read its category palette — consented before MailboxSettings was requested.",
                connection.Label);
            return null;
        }

        await EnsureAsync(resp, connection, "could not list the categories", ct);
        var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphCategory>>(ct);

        return (page?.Value ?? [])
            .Select(c => new MailCategory
            {
                Id = c.Id,
                Name = c.DisplayName ?? "(unnamed)",
                Preset = c.Colour ?? "none",
            })
            .ToList();
    }

    public async Task<MailCategory?> CreateCategoryAsync(
        Connection connection, string name, string preset, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        using var resp = await http.PostAsJsonAsync("me/outlook/masterCategories",
            new { displayName = name, color = preset }, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) return null;

        await EnsureAsync(resp, connection, $"could not create the category \"{name}\"", ct);

        var c = await resp.Content.ReadFromJsonAsync<GraphCategory>(ct);
        return new MailCategory
        {
            Id = c?.Id ?? "",
            Name = c?.DisplayName ?? name,
            Preset = c?.Colour ?? preset,
        };
    }

    /// <summary>
    /// Turns a failed Graph call into something sayable. Auth failures are raised as
    /// <see cref="MailAuthRequiredException"/> because they need a browser, not a retry —
    /// the two are indistinguishable to a caller that only sees a status code.
    /// </summary>
    /// <param name="done">
    /// How much of a bulk job had already succeeded, so a throttle can say so. Work already
    /// done and not reported is work the caller will do again.
    /// </param>
    /// <param name="of">The size of that job, when it is known.</param>
    private async Task EnsureAsync(
        HttpResponseMessage resp, Connection connection, string what, CancellationToken ct,
        int done = 0, int of = 0)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync(ct);
        log.LogError("{What} for {Label}: HTTP {Status} {Body}",
            what, connection.Label, (int)resp.StatusCode, body);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new MailAuthRequiredException(connection.Label);

        // 429 ahead of the general case, and as its own type (OP2-238). Described as "could
        // not do that" it reads like a fault, and the agent retries immediately — which is
        // the one response that makes a throttle worse. This mailbox refuses with
        // ApplicationThrottled / MailboxConcurrency, and the only useful answer is a number
        // of seconds and an instruction to do less.
        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new MailThrottledException(connection.Label, RetryAfter(resp), done, of);

        throw new MailException($"{connection.Label}: {what} — {Describe(resp.StatusCode, body)}");
    }

    /// <summary>
    /// How long Graph asked us to wait. It sends <c>Retry-After</c> as delta seconds, but
    /// not always, so the fallback matters as much as the header.
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta) return delta;

        if (resp.Headers.RetryAfter?.Date is { } when)
        {
            var wait = when - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }

        return null;
    }

    /// <summary>
    /// A request that waits out a throttle rather than failing on it, for the ordinary
    /// single-shot calls. Bounded tightly: somebody is waiting for an answer out loud, so a
    /// couple of short waits is the most that helps and anything longer belongs to the
    /// caller, who can be told to come back.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Func<HttpRequestMessage> build, Connection connection,
        string what, CancellationToken ct, int done = 0, int of = 0)
    {
        const int attempts = 3;

        for (var attempt = 0; attempt < attempts - 1; attempt++)
        {
            using var req = build();
            var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests) return resp;

            var wait = RetryAfter(resp) ?? TimeSpan.FromSeconds(2 * (attempt + 1));
            resp.Dispose();

            // Past a few seconds the caller is better served by being told to come back:
            // somebody is listening to this, and a tool call that sits silent for a minute
            // has already failed them even if it eventually succeeds.
            if (wait > TimeSpan.FromSeconds(10))
                throw new MailThrottledException(connection.Label, wait, done, of);

            log.LogWarning("Throttled on {What} for {Label}; waiting {Seconds}s.",
                what, connection.Label, wait.TotalSeconds);

            await _sleep(wait, ct);
        }

        // Last go: whatever comes back is the answer, and a 429 here becomes a throttle
        // report through EnsureAsync like any other.
        using var last = build();
        return await http.SendAsync(last, ct);
    }

    public async Task<IReadOnlyList<MailAttachment>> ListAttachmentsAsync(
        Connection connection, string messageId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var q = $"me/messages/{Uri.EscapeDataString(messageId)}/attachments" +
                "?$select=id,name,contentType,size,isInline";

        var page = await http.GetFromJsonAsync<GraphList<GraphAttachment>>(q, ct);

        return (page?.Value ?? [])
            // Inline images are attachments as far as Graph is concerned. Listing them
            // would make every HTML newsletter look like it has fourteen attachments.
            .Where(a => !a.IsInline)
            .Select(a => new MailAttachment
            {
                Id = a.Id,
                Name = a.Name ?? "(unnamed)",
                ContentType = a.ContentType ?? "application/octet-stream",
                SizeBytes = a.Size,

                // A reference attachment is a OneDrive/SharePoint LINK with no bytes behind
                // it. Downloading it the normal way returns nothing useful, so it has to be
                // distinguished rather than failing mysteriously.
                IsLink = a.ODataType?.Contains("referenceAttachment", StringComparison.OrdinalIgnoreCase) == true,
                IsEmbeddedItem = a.ODataType?.Contains("itemAttachment", StringComparison.OrdinalIgnoreCase) == true,
            })
            .ToList();
    }

    public async Task<Stream?> OpenAttachmentAsync(
        Connection connection, string messageId, string attachmentId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var q = $"me/messages/{Uri.EscapeDataString(messageId)}/attachments/" +
                $"{Uri.EscapeDataString(attachmentId)}/$value";

        var resp = await http.GetAsync(q, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;

        // Copy out before the HttpClient is disposed by the using above.
        var ms = new MemoryStream();
        await resp.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    private sealed record GraphAttachment(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("isInline")] bool IsInline,
        [property: JsonPropertyName("@odata.type")] string? ODataType);

    private static string Describe(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) &&
                e.TryGetProperty("message", out var m))
                return m.GetString() ?? status.ToString();
        }
        catch { /* not JSON */ }

        return status.ToString();
    }

    // ── Graph wire types ─────────────────────────────────────────────────────

    private sealed record GraphList<T>(
        [property: JsonPropertyName("value")] List<T>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink = null);

    private sealed record GraphCountedList<T>(
        [property: JsonPropertyName("value")] List<T>? Value,
        [property: JsonPropertyName("@odata.count")] int? Count = null);

    private sealed record GraphBatchReplies(
        [property: JsonPropertyName("responses")] List<GraphBatchReply>? Responses);

    private sealed record GraphBatchReply(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] int Status);

    private sealed record GraphMessage(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("from")] GraphRecipient? From,
        [property: JsonPropertyName("toRecipients")] List<GraphRecipient>? ToRecipients,
        [property: JsonPropertyName("ccRecipients")] List<GraphRecipient>? CcRecipients,
        [property: JsonPropertyName("receivedDateTime")] DateTimeOffset ReceivedDateTime,
        [property: JsonPropertyName("isRead")] bool IsRead,
        [property: JsonPropertyName("hasAttachments")] bool HasAttachments,
        [property: JsonPropertyName("bodyPreview")] string? BodyPreview,
        [property: JsonPropertyName("uniqueBody")] GraphBody? UniqueBody,
        [property: JsonPropertyName("body")] GraphBody? Body,
        [property: JsonPropertyName("parentFolderId")] string? ParentFolderId = null,
        [property: JsonPropertyName("sentDateTime")] DateTimeOffset? SentDateTime = null)
    {
        public MailSummary ToSummary(string label) => new()
        {
            Id = Id,
            ConnectionLabel = label,
            Subject = Subject ?? "(no subject)",
            From = From?.EmailAddress?.Format() ?? "(unknown)",
            ReceivedAt = ReceivedDateTime,
            IsRead = IsRead,
            HasAttachments = HasAttachments,
            Preview = (BodyPreview ?? "").Trim(),
            FolderId = ParentFolderId,
        };
    }

    private sealed record GraphCategorised(
        [property: JsonPropertyName("categories")] List<string>? Categories);

    private sealed record GraphCategory(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        // Graph spells it "color"; the rest of this codebase does not.
        [property: JsonPropertyName("color")] string? Colour);

    private sealed record GraphFolder(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("wellKnownName")] string? WellKnownName,
        [property: JsonPropertyName("totalItemCount")] int TotalItemCount,
        [property: JsonPropertyName("childFolderCount")] int ChildFolderCount,
        [property: JsonPropertyName("childFolders")] List<GraphFolder>? ChildFolders)
    {
        public MailFolder ToFolder(string? parent) => new()
        {
            Id = Id,
            Name = DisplayName ?? WellKnownName ?? "(unnamed)",
            WellKnownName = WellKnownName,
            Path = parent is null ? DisplayName ?? "(unnamed)" : $"{parent}/{DisplayName}",
            MessageCount = TotalItemCount,
            ChildCount = ChildFolderCount,
        };
    }

    private sealed record GraphRecipient(
        [property: JsonPropertyName("emailAddress")] GraphAddress? EmailAddress);

    private sealed record GraphAddress(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("address")] string? Address)
    {
        /// <summary>
        /// A name alone is ambiguous and an address alone is unfriendly; prefer "Name &lt;addr&gt;".
        /// Format rather than Describe, which would shadow the enclosing class's two
        /// Describe overloads — both of which render HTTP failures, not people (OP2-146).
        /// </summary>
        public string Format() =>
            string.IsNullOrWhiteSpace(Name) ? Address ?? ""
            : string.IsNullOrWhiteSpace(Address) ? Name
            : $"{Name} <{Address}>";
    }

    private sealed record GraphBody(
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("content")] string? Content);
}

/// <summary>Indirection so MailService does not depend on the whole connection service.</summary>
public interface IMicrosoftConnectionServiceAccessor
{
    /// <summary>
    /// A Graph access token for this connection, refreshed silently. Returns null when the
    /// connection needs interactive re-authorisation.
    /// <para>
    /// Declared here and nowhere else. IMicrosoftConnectionService extends this interface
    /// and used to redeclare the same member, which hid this one (OP2-147).
    /// </para>
    /// </summary>
    Task<string?> GetAccessTokenAsync(Connection connection, CancellationToken ct = default);
}

public class MailException(string message) : Exception(message);

/// <summary>
/// Microsoft is rate-limiting this mailbox (OP2-238).
/// <para>
/// Its own type because it is the one failure where the right response is to <b>wait and do
/// less</b>, and nothing else in this surface can say that. A generic "could not do that"
/// invites an immediate retry, which is the behaviour the throttle exists to stop — and the
/// agent on the other end of this will cheerfully retry in a loop unless it is told
/// otherwise, in words, with a number of seconds in them.
/// </para>
/// <para>
/// It derives from <see cref="MailException"/> so that every tool already catching one keeps
/// working and keeps speaking the message, rather than this needing a new catch in each of
/// twenty places to avoid becoming an unhandled 500.
/// </para>
/// </summary>
public sealed class MailThrottledException : MailException
{
    public MailThrottledException(string label, TimeSpan? retryAfter, int done = 0, int of = 0)
        : base(Say(label, retryAfter, done, of))
    {
        Label = label;
        RetryAfter = retryAfter;
        Done = done;
    }

    public string Label { get; }
    public TimeSpan? RetryAfter { get; }
    public int Done { get; }

    /// <summary>The seconds to wait, rounded for saying out loud and floored at five.</summary>
    public int Seconds => Math.Max(5, (int)Math.Ceiling((RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds));

    private static string Say(string label, TimeSpan? retryAfter, int done, int of)
    {
        var wait = Math.Max(5, (int)Math.Ceiling((retryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds));

        // Progress first, because a throttle part-way through a bulk job is the case where
        // the agent most needs to know that the work was not wasted and must not be redone
        // from the start.
        var progress = done > 0
            ? of > 0
                ? $"I got through {done} of {of} before "
                : $"I got through {done} before "
            : "";

        return $"{progress}Microsoft is rate-limiting {label} right now — it is refusing "
             + $"requests because too many have gone through too quickly. Wait about {wait} "
             + "seconds before asking again, and do it in smaller pieces. Retrying straight "
             + "away will only extend the limit.";
    }
}

/// <summary>
/// The connection needs an interactive sign-in. Distinct from a general failure because
/// it cannot be retried and cannot be fixed inside a voice turn — it needs a browser.
/// </summary>
public sealed class MailAuthRequiredException(string label)
    : Exception($"{label} needs re-authorising before it can be used.")
{
    public string Label { get; } = label;
}
