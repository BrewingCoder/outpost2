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
    ILogger<MailService> log) : IMailService
{
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
                "?$select=id,subject,from,toRecipients,ccRecipients,receivedDateTime,isRead," +
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

    public async Task<IReadOnlyList<MailFolder>> ListFoldersAsync(
        Connection connection, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // One level of children as well as the top level: people keep their filing folders
        // under Inbox, and a destination list that stops at the root would miss all of them.
        //
        // $expand with no nested options, and no $select beside it. Graph rejects
        // $select + $expand on mailFolders outright with a 400, and rejects nested
        // $select/$top inside the expand as well — so the shape here is the one it accepts,
        // not the narrowest one that looks reasonable.
        using var resp = await http.GetAsync("me/mailFolders?$top=100&$expand=childFolders", ct);
        await EnsureAsync(resp, connection, "could not list the folders", ct);

        var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphFolder>>(ct);

        var folders = new List<MailFolder>();
        foreach (var f in page?.Value ?? [])
        {
            folders.Add(f.ToFolder(null));
            foreach (var child in f.ChildFolders ?? [])
                folders.Add(child.ToFolder(f.DisplayName));
        }
        return folders;
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
    private async Task EnsureAsync(
        HttpResponseMessage resp, Connection connection, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync(ct);
        log.LogError("{What} for {Label}: HTTP {Status} {Body}",
            what, connection.Label, (int)resp.StatusCode, body);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new MailAuthRequiredException(connection.Label);

        throw new MailException($"{connection.Label}: {what} — {Describe(resp.StatusCode, body)}");
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
        [property: JsonPropertyName("parentFolderId")] string? ParentFolderId = null)
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

public sealed class MailException(string message) : Exception(message);

/// <summary>
/// The connection needs an interactive sign-in. Distinct from a general failure because
/// it cannot be retried and cannot be fixed inside a voice turn — it needs a browser.
/// </summary>
public sealed class MailAuthRequiredException(string label)
    : Exception($"{label} needs re-authorising before it can be used.")
{
    public string Label { get; } = label;
}
