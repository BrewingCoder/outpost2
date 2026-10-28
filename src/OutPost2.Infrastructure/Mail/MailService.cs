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

    Task<IReadOnlyList<MailSummary>> SearchAsync(
        Connection connection, string query, int top = 25, CancellationToken ct = default);

    Task SendAsync(Connection connection, OutgoingMail mail, CancellationToken ct = default);

    /// <summary>
    /// Replies, replies to all, or forwards, keeping the thread and the quoted original.
    /// Returns the subject actually sent, so the caller can read back what it did.
    /// </summary>
    Task<string> SendReplyAsync(
        Connection connection, string messageId, ReplyKind kind, string markdownBody,
        IReadOnlyList<string>? forwardTo = null, CancellationToken ct = default);

    Task MarkReadAsync(Connection connection, string messageId, bool read, CancellationToken ct = default);

    /// <summary>Moves a message. Returns its NEW id — Graph reissues one on every move.</summary>
    Task<string> MoveAsync(
        Connection connection, string messageId, string destinationFolderId, CancellationToken ct = default);

    Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Connection connection, CancellationToken ct = default);

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
            From = m.From?.EmailAddress?.Describe() ?? "(unknown)",
            To = (m.ToRecipients ?? []).Select(r => r.EmailAddress?.Describe() ?? "").Where(x => x != "").ToList(),
            Cc = (m.CcRecipients ?? []).Select(r => r.EmailAddress?.Describe() ?? "").Where(x => x != "").ToList(),
            ReceivedAt = m.ReceivedDateTime,
            IsRead = m.IsRead,
            HasAttachments = m.HasAttachments,
            Body = text,
            QuotedHistoryStripped = m.UniqueBody is not null,
        };
    }

    public async Task<IReadOnlyList<MailSummary>> SearchAsync(
        Connection connection, string query, int top = 25, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // $search cannot be combined with $orderby in Graph; attempting it returns a
        // confusing 400 rather than ignoring the sort.
        var q = $"me/messages?$search={Uri.EscapeDataString($"\"{query}\"")}" +
                $"&$top={Math.Clamp(top, 1, 100)}" +
                "&$select=id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";

        using var req = new HttpRequestMessage(HttpMethod.Get, q);
        req.Headers.Add("ConsistencyLevel", "eventual");

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphMessage>>(ct);
        return (page?.Value ?? []).Select(m => m.ToSummary(connection.Label)).ToList();
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

    public async Task<string> SendReplyAsync(
        Connection connection, string messageId, ReplyKind kind, string markdownBody,
        IReadOnlyList<string>? forwardTo = null, CancellationToken ct = default)
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

        var patch = new Dictionary<string, object?>
        {
            ["body"] = new
            {
                contentType = "HTML",
                content = Prepend(MarkdownToHtml.Render(markdownBody), draft.Body?.Content),
            },
        };

        if (kind == ReplyKind.Forward)
        {
            // A forward draft has no recipients at all, so this is not an override but the
            // only thing that makes it sendable.
            var to = (forwardTo ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (to.Count == 0) throw new MailException("A forward needs at least one recipient.");
            patch["toRecipients"] = to.Select(a => new { emailAddress = new { address = a } }).ToArray();
        }

        using var patchResp = await http.PatchAsJsonAsync(
            $"me/messages/{Uri.EscapeDataString(draft.Id)}", patch, ct);
        await EnsureAsync(patchResp, connection, "could not write the reply body", ct);

        using var sendResp = await http.PostAsync(
            $"me/messages/{Uri.EscapeDataString(draft.Id)}/send", content: null, ct);
        await EnsureAsync(sendResp, connection, $"could not send that {Describe(kind)}", ct);

        log.LogInformation("Sent a {Kind} from {Label}.", verb, connection.Label);
        return draft.Subject ?? "(no subject)";

        static string Describe(ReplyKind k) => k == ReplyKind.Forward ? "forward" : "reply";
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

    private sealed record GraphList<T>([property: JsonPropertyName("value")] List<T>? Value);

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
        [property: JsonPropertyName("body")] GraphBody? Body)
    {
        public MailSummary ToSummary(string label) => new()
        {
            Id = Id,
            ConnectionLabel = label,
            Subject = Subject ?? "(no subject)",
            From = From?.EmailAddress?.Describe() ?? "(unknown)",
            ReceivedAt = ReceivedDateTime,
            IsRead = IsRead,
            HasAttachments = HasAttachments,
            Preview = (BodyPreview ?? "").Trim(),
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
        [property: JsonPropertyName("childFolders")] List<GraphFolder>? ChildFolders)
    {
        public MailFolder ToFolder(string? parent) => new()
        {
            Id = Id,
            Name = DisplayName ?? WellKnownName ?? "(unnamed)",
            WellKnownName = WellKnownName,
            Path = parent is null ? DisplayName ?? "(unnamed)" : $"{parent}/{DisplayName}",
        };
    }

    private sealed record GraphRecipient(
        [property: JsonPropertyName("emailAddress")] GraphAddress? EmailAddress);

    private sealed record GraphAddress(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("address")] string? Address)
    {
        /// <summary>A name alone is ambiguous and an address alone is unfriendly; prefer "Name &lt;addr&gt;".</summary>
        public string Describe() =>
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
