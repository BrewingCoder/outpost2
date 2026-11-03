using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// Gmail behind <see cref="IMailService"/> (OP2-80), read side (OP2-280): listing, reading,
/// searching, labels as folders, and attachments.
/// <para>
/// Written against the Gmail API v1 as documented, not against a live account; the wire
/// shapes in the tests are Google's. Sending (OP2-281) and organising (OP2-282) are not
/// here yet, and say so in one sentence rather than failing strangely.
/// </para>
/// <para>
/// Four Gmail facts shape this file and are easy to get wrong:
/// </para>
/// <list type="bullet">
/// <item>Listing returns ids only. Each message is a second request, so they are fetched
/// in parallel but bounded, to stay inside the per-user quota.</item>
/// <item>An <c>attachmentId</c> is issued afresh on every fetch of a message. So an
/// attachment is identified here by its MIME part id, which is stable, and the current
/// attachment id is looked up when the bytes are wanted — otherwise a download link minted
/// ten minutes ago points at nothing.</item>
/// <item>A <c>pageToken</c> is only valid with the same query and labels it came from, so
/// a search continuation carries all three.</item>
/// <item>There are labels, not folders. Inbox, Sent, Drafts, Spam and Trash are system
/// labels and map to the well-known folders; user labels nest with <c>/</c>.</item>
/// </list>
/// </summary>
public sealed partial class GmailService(
    IGoogleTokenAccessor tokens,
    IHttpClientFactory httpFactory,
    ILogger<GmailService> log) : IMailProvider
{
    public ProviderKind Provider => ProviderKind.Google;

    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me/";

    /// <summary>Message fetches in flight at once, per call. Gmail allows 250 quota units a
    /// second per user and a get costs 5, so this leaves room for everything else.</summary>
    private const int Parallel = 8;

    static GmailService()
    {
        // Mail still arrives in windows-1252, iso-8859-x, shift_jis… — none of which .NET
        // knows without this.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <param name="sending">
    /// The long-timeout client, for anything that uploads a message. A forward carrying 20 MB
    /// of attachments can outlast the ordinary minute, and a timeout mid-upload would be
    /// reported — correctly but needlessly — as "may have been sent" (OP2-281).
    /// </param>
    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct, bool sending = false)
    {
        var token = await tokens.GetAccessTokenAsync(c, ct) ?? throw new MailAuthRequiredException(c.Label);
        var http = httpFactory.CreateClient(sending ? "gmail-upload" : "gmail");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>
    /// One GET, with Gmail's errors turned into the ones the tools already speak. Null for
    /// 404, because "that message is gone" is an answer the caller decides about.
    /// </summary>
    private async Task<T?> GetAsync<T>(HttpClient http, Connection c, string path, CancellationToken ct)
        where T : class
    {
        using var resp = await http.GetAsync(Api + path, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(resp, c, ct);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private Task EnsureAsync(HttpResponseMessage resp, Connection c, CancellationToken ct) =>
        EnsureAsync(resp, c, log, ct);

    internal static async Task EnsureAsync(HttpResponseMessage resp, Connection c, ILogger log, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync(ct);
        var reason = ReasonOf(body);

        // A token Google no longer accepts. Usually caught by the refresh before this, but
        // access can be withdrawn between the two.
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new MailAuthRequiredException(c.Label);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests
            || reason is "rateLimitExceeded" or "userRateLimitExceeded")
            throw new MailThrottledException(c.Label, RetryAfter(resp), provider: "Google");

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            log.LogWarning("Gmail refused {Label}: {Reason} {Body}", c.Label, reason, Trim(body));
            throw new MailException(reason is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT"
                ? $"Google hasn't given Outpost permission to do that on {c.Label}. Reconnecting it and "
                  + "leaving the Gmail permissions ticked fixes this."
                : $"Gmail refused that on {c.Label}.");
        }

        log.LogError("Gmail call for {Label} failed: HTTP {Status} {Body}", c.Label, (int)resp.StatusCode, Trim(body));
        throw new MailException((int)resp.StatusCode >= 500
            ? $"Gmail is having trouble right now ({(int)resp.StatusCode}). Try again in a moment."
            : $"Gmail couldn't do that on {c.Label} ({(int)resp.StatusCode}).");
    }

    /// <summary>The first <c>errors[].reason</c>, or failing that <c>status</c>, from Google's error body.</summary>
    public static string? ReasonOf(string body)
    {
        try
        {
            var e = JsonSerializer.Deserialize<GError>(body, Json)?.Error;
            return e?.Errors?.FirstOrDefault()?.Reason ?? e?.Details?.FirstOrDefault()?.Reason ?? e?.Status;
        }
        catch (JsonException) { return null; }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage resp) =>
        resp.Headers.RetryAfter?.Delta
        ?? (resp.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;

    private static Exception NotYet(Connection c, string what) =>
        new MailException($"{Cap(what)} isn't available for Gmail yet, so nothing was done on {c.Label}.");

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Fetches each id's message, in order, a bounded number at a time; gone ones are dropped.</summary>
    private async Task<List<GMessage>> FetchAllAsync(
        HttpClient http, Connection c, IEnumerable<string> ids, string format, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Parallel);
        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { return await GetAsync<GMessage>(http, c, $"messages/{Uri.EscapeDataString(id)}?{format}", ct); }
            finally { gate.Release(); }
        }).ToList();

        var all = await Task.WhenAll(tasks);
        return all.Where(m => m is not null).Select(m => m!).ToList();
    }

    private const string MetadataFormat =
        "format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date"
        + "&metadataHeaders=Importance&metadataHeaders=X-Priority";

    // ── listing and searching ────────────────────────────────────────────────

    public async Task<IReadOnlyList<MailSummary>> ListAsync(
        Connection connection, string folder = "inbox", int top = 25, bool unreadOnly = false,
        CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        var scope = Scope(folder);
        var q = string.Join(' ', new[] { scope.Query, unreadOnly ? "is:unread" : null }.Where(x => x is not null));

        var page = await ListIdsAsync(http, connection, q, scope.Labels, top, null, ct);
        var messages = await FetchAllAsync(http, connection, page.Ids, MetadataFormat, ct);
        return messages.Select(m => Summary(m, connection.Label)).ToList();
    }

    public async Task<MailSearchResult> SearchAsync(
        Connection connection, string query, int top = 25, string? folderId = null,
        string? continueFrom = null, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // Resuming: the continuation is the query and labels the token belongs to, and the
        // arguments are ignored, as on the Graph side — a page token sent with a different
        // query is rejected or, worse, silently means something else.
        var resume = Continuation.Read(continueFrom);
        string q;
        IReadOnlyList<string> labels;
        if (resume is not null)
        {
            (q, labels) = (resume.Query, resume.Labels);
        }
        else
        {
            var scope = folderId is null ? new Scoped(null, []) : Scope(folderId);
            q = string.Join(' ', new[] { query?.Trim(), scope.Query }.Where(x => !string.IsNullOrWhiteSpace(x)));
            labels = scope.Labels;
        }

        var page = await ListIdsAsync(http, connection, q, labels, top, resume?.PageToken, ct);
        var messages = await FetchAllAsync(http, connection, page.Ids, MetadataFormat, ct);

        return new MailSearchResult
        {
            Items = messages.Select(m => Summary(m, connection.Label)).ToList(),
            More = page.Next is not null,
            Continuation = page.Next is null ? null : new Continuation(q, labels, page.Next).Write(),
        };
    }

    private sealed record IdPage(IReadOnlyList<string> Ids, string? Next);

    private async Task<IdPage> ListIdsAsync(
        HttpClient http, Connection c, string q, IReadOnlyList<string> labels, int top, string? pageToken,
        CancellationToken ct)
    {
        var query = new List<string> { $"maxResults={Math.Clamp(top, 1, 500)}" };
        if (!string.IsNullOrWhiteSpace(q)) query.Add("q=" + Uri.EscapeDataString(q));
        query.AddRange(labels.Select(l => "labelIds=" + Uri.EscapeDataString(l)));
        if (pageToken is not null) query.Add("pageToken=" + Uri.EscapeDataString(pageToken));

        var list = await GetAsync<GList>(http, c, "messages?" + string.Join('&', query), ct);

        // No "messages" key at all is how Gmail says "none".
        return new IdPage((list?.Messages ?? []).Select(m => m.Id).ToList(), list?.NextPageToken);
    }

    /// <summary>A folder as Gmail can be asked for it: by label, or (for Archive) by query.</summary>
    public sealed record Scoped(string? Query, IReadOnlyList<string> Labels);

    /// <summary>
    /// The well-known folder names the tools use (Graph's), and label ids, as Gmail scopes.
    /// Archive has no label in Gmail — it is everything that has left the Inbox — so it is
    /// a query.
    /// </summary>
    public static Scoped Scope(string folder) => folder.Trim().ToLowerInvariant() switch
    {
        "inbox" => new(null, ["INBOX"]),
        "sentitems" => new(null, ["SENT"]),
        "drafts" => new(null, ["DRAFT"]),
        "junkemail" => new(null, ["SPAM"]),
        "deleteditems" => new(null, ["TRASH"]),
        "archive" => new("-in:inbox -in:sent -in:drafts -in:spam -in:trash", []),
        _ => new(null, [folder.Trim()]),
    };

    /// <summary>A search continuation: the token, and the query and labels it is only valid with.</summary>
    public sealed record Continuation(
        [property: JsonPropertyName("q")] string Query,
        [property: JsonPropertyName("l")] IReadOnlyList<string> Labels,
        [property: JsonPropertyName("t")] string PageToken)
    {
        public string Write() => JsonSerializer.Serialize(this);

        public static Continuation? Read(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { return JsonSerializer.Deserialize<Continuation>(s); }
            catch (JsonException) { return null; }
        }
    }

    private static MailSummary Summary(GMessage m, string label)
    {
        var h = Headers(m.Payload);
        return new MailSummary
        {
            Id = m.Id,
            ConnectionLabel = label,
            Subject = Decode(h.GetValueOrDefault("subject")) is { Length: > 0 } s ? s : "(no subject)",
            From = Decode(h.GetValueOrDefault("from")) is { Length: > 0 } f ? f : "(unknown)",
            ReceivedAt = Received(m),
            IsRead = !(m.LabelIds ?? []).Contains("UNREAD"),

            // Metadata does not carry the parts, so this is read from the top-level type:
            // a message with attachments is multipart/mixed. Close enough for "(has
            // attachments)" in a listing; list_attachments reads the real structure.
            HasAttachments = string.Equals(m.Payload?.MimeType, "multipart/mixed", StringComparison.OrdinalIgnoreCase),

            // Gmail's snippet arrives HTML-escaped (&#39; and friends).
            Preview = WebUtility.HtmlDecode(m.Snippet ?? "").Trim(),
            FolderId = PrimaryLabel(m.LabelIds),
            Importance = ImportanceOf(h),
        };
    }

    /// <summary>
    /// The one label to report as where a message "is", for search results. Inbox first,
    /// because "is this still in the Inbox" is the question that location answers; then a
    /// user label; then Sent, Drafts, Spam or Trash; then nowhere (archived).
    /// </summary>
    public static string? PrimaryLabel(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count == 0) return null;
        if (labels.Contains("INBOX")) return "INBOX";
        var user = labels.FirstOrDefault(l => l.StartsWith("Label_", StringComparison.Ordinal));
        if (user is not null) return user;
        foreach (var system in (string[])["SENT", "DRAFT", "SPAM", "TRASH"])
            if (labels.Contains(system)) return system;
        return null;
    }

    private static DateTimeOffset Received(GMessage m) =>
        long.TryParse(m.InternalDate, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.UnixEpoch;

    /// <summary>
    /// What the sender set: <c>Importance: high|low</c>, or <c>X-Priority: 1/2</c> (high) and
    /// <c>4/5</c> (low). Not Gmail's own IMPORTANT label, which is Google's guess about the
    /// reader, not the sender's flag.
    /// </summary>
    public static MailImportance ImportanceOf(IReadOnlyDictionary<string, string> h)
    {
        var imp = h.GetValueOrDefault("importance")?.Trim().ToLowerInvariant();
        if (imp == "high") return MailImportance.High;
        if (imp == "low") return MailImportance.Low;

        var prio = h.GetValueOrDefault("x-priority")?.Trim();
        if (prio is { Length: > 0 } && char.IsDigit(prio[0]))
            return prio[0] switch { '1' or '2' => MailImportance.High, '4' or '5' => MailImportance.Low, _ => MailImportance.Normal };

        return MailImportance.Normal;
    }

    // ── reading ──────────────────────────────────────────────────────────────

    public async Task<MailMessage?> ReadAsync(Connection connection, string messageId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        var m = await GetAsync<GMessage>(http, connection, $"messages/{Uri.EscapeDataString(messageId)}?format=full", ct);
        if (m is null) return null;

        var h = Headers(m.Payload);
        var (html, plain) = Bodies(m.Payload);

        string text;
        bool stripped;
        if (html is not null)
        {
            var (kept, cut) = StripHtmlQuote(html);
            (text, stripped) = (HtmlToText.Convert(kept), cut);
        }
        else
        {
            (text, stripped) = StripPlainQuote(plain ?? "");
        }

        return new MailMessage
        {
            Id = m.Id,
            ConnectionLabel = connection.Label,
            Subject = Decode(h.GetValueOrDefault("subject")) is { Length: > 0 } s ? s : "(no subject)",
            From = Decode(h.GetValueOrDefault("from")) is { Length: > 0 } f ? f : "(unknown)",
            To = Addresses(h.GetValueOrDefault("to")),
            Cc = Addresses(h.GetValueOrDefault("cc")),
            ReceivedAt = Received(m),
            SentAt = ParseDate(h.GetValueOrDefault("date")),
            IsRead = !(m.LabelIds ?? []).Contains("UNREAD"),
            HasAttachments = Attachments(m.Payload).Count > 0,
            Importance = ImportanceOf(h),
            Body = HtmlToText.TrimSignature(text),

            // Graph gives the new text alone (uniqueBody); Gmail gives the whole thing, so
            // this says whether the quoted history was actually found and removed. When it
            // was not, the body may carry the thread below it.
            QuotedHistoryStripped = stripped,
        };
    }

    /// <summary>
    /// The first text/html and the first text/plain body in the message, attachments
    /// excluded, decoded in their own charsets. Gmail has already undone the transfer
    /// encoding; what is left is base64url bytes.
    /// </summary>
    public static (string? Html, string? Plain) Bodies(GPart? root)
    {
        string? html = null, plain = null;
        foreach (var p in Walk(root))
        {
            if (IsAttachment(p) || p.Body?.Data is null) continue;
            var type = p.MimeType?.ToLowerInvariant();
            if (type == "text/html") html ??= Text(p);
            else if (type == "text/plain") plain ??= Text(p);
        }
        return (html, plain);
    }

    private static string Text(GPart p)
    {
        var bytes = FromBase64Url(p.Body!.Data!);
        return CharsetOf(Headers(p).GetValueOrDefault("content-type")).GetString(bytes);
    }

    public static Encoding CharsetOf(string? contentType)
    {
        var m = CharsetRx().Match(contentType ?? "");
        if (!m.Success) return Encoding.UTF8;
        try { return Encoding.GetEncoding(m.Groups[1].Value.Trim('"', '\'')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    [GeneratedRegex(@"charset\s*=\s*""?([^"";\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRx();

    /// <summary>
    /// Cuts the quoted history off an HTML reply at the first place a mail client marks it:
    /// Gmail's <c>gmail_quote</c>, a cited blockquote, Outlook's reply header. Replies put
    /// history at the bottom, so cutting there keeps what is new. If that would leave
    /// nothing — a bare forward, say — the whole message is kept, because then the "quote"
    /// is the content.
    /// </summary>
    public static (string Html, bool Stripped) StripHtmlQuote(string html)
    {
        var at = QuoteMarkers().Matches(html).Select(m => m.Index).DefaultIfEmpty(-1).Min();
        if (at <= 0) return (html, false);

        var kept = html[..at];
        return string.IsNullOrWhiteSpace(HtmlToText.Convert(kept)) ? (html, false) : (kept, true);
    }

    [GeneratedRegex(
        @"<div[^>]*class=""[^""]*gmail_quote|<blockquote[^>]*type=""cite""|<div[^>]*id=""(?:divRplyFwdMsg|appendonsend)""|<hr[^>]*id=""stopSpelling""",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuoteMarkers();

    /// <summary>
    /// The same for plain text: cut at "On … wrote:" (which clients wrap over two lines),
    /// "-----Original Message-----", or the first line of a trailing block quoted with
    /// "&gt;". Kept whole when cutting would leave nothing.
    /// </summary>
    public static (string Text, bool Stripped) StripPlainQuote(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var cut = -1;

        for (var i = 0; i < lines.Length && cut < 0; i++)
        {
            var line = lines[i].Trim();
            var joined = i + 1 < lines.Length ? line + " " + lines[i + 1].Trim() : line;

            if (line.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase)) cut = i;
            else if (WroteRx().IsMatch(line) || (line.StartsWith("On ", StringComparison.Ordinal) && WroteRx().IsMatch(joined))) cut = i;
        }

        if (cut < 0)
        {
            // A quoted block that runs to the end, with nothing new after it.
            var last = lines.Length - 1;
            while (last >= 0 && lines[last].Trim().Length == 0) last--;
            var first = last;
            while (first >= 0 && lines[first].TrimStart().StartsWith('>')) first--;
            if (first < last) cut = first + 1;
        }

        if (cut <= 0) return (text, false);

        var kept = string.Join('\n', lines[..cut]).TrimEnd();
        return kept.Length == 0 ? (text, false) : (kept, true);
    }

    [GeneratedRegex(@"^On .{4,200} wrote:\s*$")]
    private static partial Regex WroteRx();

    // ── attachments ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MailAttachment>> ListAttachmentsAsync(
        Connection connection, string messageId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        var m = await GetAsync<GMessage>(http, connection, $"messages/{Uri.EscapeDataString(messageId)}?format=full", ct);
        return m is null ? [] : Attachments(m.Payload);
    }

    /// <summary>
    /// Every part that is an attachment, identified by its MIME part id (stable) rather than
    /// Gmail's attachment id (not). An attached message (<c>message/rfc822</c>) is reported
    /// as an embedded item, as on the Graph side.
    /// </summary>
    public static IReadOnlyList<MailAttachment> Attachments(GPart? root) =>
        Walk(root)
            .Where(IsAttachment)
            .Select(p => new MailAttachment
            {
                Id = p.PartId ?? "",
                Name = Decode(p.Filename) is { Length: > 0 } n ? n : "attachment",
                ContentType = p.MimeType ?? "application/octet-stream",
                SizeBytes = p.Body?.Size ?? 0,
                IsEmbeddedItem = string.Equals(p.MimeType, "message/rfc822", StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

    /// <summary>
    /// A part is an attachment if it has a filename, or Gmail stored its body separately and
    /// it is not one of the message's own text bodies. An inline image in an HTML message
    /// has a filename and counts — Outlook reports those too.
    /// </summary>
    private static bool IsAttachment(GPart p)
    {
        if (!string.IsNullOrEmpty(p.Filename)) return true;
        if (string.Equals(p.MimeType, "message/rfc822", StringComparison.OrdinalIgnoreCase)) return true;
        var disposition = Headers(p).GetValueOrDefault("content-disposition") ?? "";
        return disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<Stream?> OpenAttachmentAsync(
        Connection connection, string messageId, string attachmentId, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);

        // attachmentId here is the part id handed out by ListAttachmentsAsync. Gmail's own
        // attachment id is looked up now, from a fresh fetch, because it changes per fetch.
        var m = await GetAsync<GMessage>(http, connection, $"messages/{Uri.EscapeDataString(messageId)}?format=full", ct);
        var part = Walk(m?.Payload).FirstOrDefault(p => p.PartId == attachmentId);
        if (part?.Body is null) return null;

        // Small parts come inline; large ones are fetched by their attachment id.
        if (part.Body.Data is { } inline) return new MemoryStream(FromBase64Url(inline));
        if (part.Body.AttachmentId is not { } gid) return null;

        var body = await GetAsync<GBody>(http, connection,
            $"messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(gid)}", ct);
        return body?.Data is null ? null : new MemoryStream(FromBase64Url(body.Data));
    }

    // ── labels as folders ────────────────────────────────────────────────────

    /// <summary>
    /// The system labels that are folders in all but name, as the well-known folders the
    /// tools already understand. Starred, Important, Unread, Chat and the inbox category
    /// tabs are labels but not places a message is "in", and are left out.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string WellKnown)> SystemFolders = new()
    {
        ["INBOX"] = ("Inbox", "inbox"),
        ["SENT"] = ("Sent", "sentitems"),
        ["DRAFT"] = ("Drafts", "drafts"),
        ["SPAM"] = ("Spam", "junkemail"),
        ["TRASH"] = ("Trash", "deleteditems"),
    };

    public async Task<IReadOnlyList<MailFolder>> ListFoldersAsync(Connection connection, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        var list = await GetAsync<GLabels>(http, connection, "labels", ct);

        var wanted = (list?.Labels ?? [])
            .Where(l => l.Type == "user" || SystemFolders.ContainsKey(l.Id))
            .ToList();

        // The list does not carry counts; each label's own record does. Counts matter: the
        // folder listing reports them, and deleting a folder is gated on them (OP2-188).
        using var gate = new SemaphoreSlim(Parallel);
        var detailed = await Task.WhenAll(wanted.Select(async l =>
        {
            await gate.WaitAsync(ct);
            try { return await GetAsync<GLabel>(http, connection, $"labels/{Uri.EscapeDataString(l.Id)}", ct) ?? l; }
            finally { gate.Release(); }
        }));

        return Folders(detailed);
    }

    /// <summary>Gmail labels as folders, nested by the <c>/</c> in their names.</summary>
    public static IReadOnlyList<MailFolder> Folders(IReadOnlyList<GLabel> labels)
    {
        var userPaths = labels.Where(l => l.Type == "user").Select(l => l.Name).ToList();

        return labels.Select(l =>
        {
            if (SystemFolders.TryGetValue(l.Id, out var sys))
                return new MailFolder
                {
                    Id = l.Id, Name = sys.Name, Path = sys.Name, WellKnownName = sys.WellKnown,
                    MessageCount = l.MessagesTotal,
                };

            var name = l.Name;
            var slash = name.LastIndexOf('/');
            return new MailFolder
            {
                Id = l.Id,
                Name = slash >= 0 ? name[(slash + 1)..] : name,
                Path = name,
                MessageCount = l.MessagesTotal,
                ChildCount = userPaths.Count(p =>
                    p.StartsWith(name + "/", StringComparison.Ordinal) && !p[(name.Length + 1)..].Contains('/')),
            };
        })
        .OrderBy(f => f.IsWellKnown ? 0 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    // ── not yet: sending (OP2-281) and organising (OP2-282) ──────────────────

    // ── sending (OP2-281) ────────────────────────────────────────────────────

    private const string UploadSend = "https://gmail.googleapis.com/upload/gmail/v1/users/me/messages/send?uploadType=multipart";

    /// <summary>Gmail's limit on the attachments of one message; the whole message may be 35 MB.</summary>
    public const long AttachmentLimitBytes = 25L * 1024 * 1024;

    public async Task SendAsync(Connection connection, OutgoingMail mail, CancellationToken ct = default)
    {
        var html = mail.IsHtml ? MarkdownToHtml.Render(mail.Body) : "<div style=\"white-space:pre-wrap\">"
            + WebUtility.HtmlEncode(mail.Body) + "</div>";

        var message = GmailMime.Compose(connection.AccountIdentifier,
            mail.To.Select(MailService.ParseRecipient), mail.Cc.Select(MailService.ParseRecipient),
            mail.Subject, html, mail.Importance);

        using var http = await ClientAsync(connection, ct, sending: true);
        await SendRawAsync(http, connection, message, threadId: null, log, ct);

        log.LogInformation("Sent Gmail from {Label} to {Count} recipient(s).", connection.Label, mail.To.Count);
    }

    public async Task<ReplySent> SendReplyAsync(Connection connection, string messageId, ReplyKind kind, string markdownBody,
        IReadOnlyList<string>? forwardTo = null, RecipientEdit? edit = null,
        MailImportance importance = MailImportance.Normal, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct, sending: true);

        var m = await GetAsync<GMessage>(http, connection, $"messages/{Uri.EscapeDataString(messageId)}?format=full", ct)
            ?? throw new MailException("That message no longer exists, so there is nothing to reply to.");

        var original = OriginalOf(m);
        var describe = kind == ReplyKind.Forward ? "forward" : "reply";

        // The same recipient rules, and the same edit semantics, as the Graph path —
        // PlanRecipients is the one function that decides who stays on a thread (OP2-187).
        RecipientPlan plan;
        if (kind == ReplyKind.Forward)
        {
            plan = MailService.PlanRecipients(
                (forwardTo ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(MailService.ParseRecipient).ToList(),
                [], edit);
        }
        else
        {
            var (to, cc) = GmailMime.ReplyRecipients(original, kind, connection.AccountIdentifier);
            plan = MailService.PlanRecipients(to, cc, edit);
        }

        if (plan.Unmatched.Count > 0)
            throw new MailException(
                $"Nothing was sent: nobody on that {describe} is "
                + $"\"{string.Join("\" or \"", plan.Unmatched)}\". It would go to "
                + $"{Join(plan.To)}{(plan.Cc.Count > 0 ? $", copying {Join(plan.Cc)}" : "")}. "
                + "Name one of those exactly, or leave it off to keep everyone.");

        if (plan.To.Count == 0)
            throw new MailException(
                $"Nothing was sent: that would leave the {describe} with no recipients at all."
                + (kind == ReplyKind.Forward ? "" : " Add someone, or take fewer people off."));

        // A forward carries the original's attachments, as Outlook's does. Collected before
        // anything is sent, so one that cannot be fetched stops the forward rather than
        // letting it go out silently without it.
        var carried = kind == ReplyKind.Forward
            ? await CarriedAttachmentsAsync(http, connection, m, ct)
            : [];

        var subject = GmailMime.Subject(kind, original.Subject);
        var html = GmailMime.WithOriginal(MarkdownToHtml.Render(markdownBody), kind, original);

        var message = GmailMime.Compose(connection.AccountIdentifier, plan.To, plan.Cc, subject, html, importance,
            carried,
            // Threading: a forward starts a new conversation on the other end, but stays in
            // the user's own thread, which is what Gmail itself does.
            inReplyTo: kind == ReplyKind.Forward ? null : original.MessageId,
            references: kind == ReplyKind.Forward ? null : original.References);

        await SendRawAsync(http, connection, message, m.ThreadId, log, ct);

        log.LogInformation("Sent a Gmail {Kind} from {Label} to {Count} recipient(s).",
            describe, connection.Label, plan.To.Count + plan.Cc.Count);

        return new ReplySent { Subject = subject, To = plan.To, Cc = plan.Cc, Dropped = plan.Dropped };

        static string Join(IEnumerable<MailRecipient> rs) => string.Join(", ", rs.Select(r => r.Display));
    }

    /// <summary>The parts of the original a reply or forward needs, from Gmail's full format.</summary>
    public static GmailMime.Original OriginalOf(GMessage m)
    {
        var h = Headers(m.Payload);
        var (html, plain) = Bodies(m.Payload);
        return new GmailMime.Original(
            Subject: Decode(h.GetValueOrDefault("subject")),
            MessageId: h.GetValueOrDefault("message-id")?.Trim(),
            References: h.GetValueOrDefault("references"),
            From: GmailMime.Recipients(h.GetValueOrDefault("from")),
            ReplyTo: GmailMime.Recipients(h.GetValueOrDefault("reply-to")),
            To: GmailMime.Recipients(h.GetValueOrDefault("to")),
            Cc: GmailMime.Recipients(h.GetValueOrDefault("cc")),
            Date: h.GetValueOrDefault("date"),
            Html: html,
            Plain: plain);
    }

    private async Task<List<MimeKit.MimeEntity>> CarriedAttachmentsAsync(
        HttpClient http, Connection c, GMessage m, CancellationToken ct)
    {
        var parts = Walk(m.Payload).Where(IsAttachment).ToList();
        if (parts.Sum(p => p.Body?.Size ?? 0) > AttachmentLimitBytes)
            throw new MailException(
                "Nothing was sent: the original's attachments are over Gmail's 25 MB limit, so it can't be forwarded with them.");

        var carried = new List<MimeKit.MimeEntity>();
        foreach (var p in parts)
        {
            var bytes = p.Body?.Data is { } inline
                ? FromBase64Url(inline)
                : p.Body?.AttachmentId is { } gid
                    ? (await GetAsync<GBody>(http, c,
                          $"messages/{Uri.EscapeDataString(m.Id)}/attachments/{Uri.EscapeDataString(gid)}", ct))?.Data is { } data
                        ? FromBase64Url(data)
                        : null
                    : null;

            if (bytes is null)
                throw new MailException(
                    $"Nothing was sent: the original's attachment \"{Decode(p.Filename)}\" couldn't be fetched to forward with it.");

            // An attached message stays a message: RFC 2046 does not allow message/rfc822 to
            // be base64-encoded, so it is carried as a message part rather than a file.
            if (string.Equals(p.MimeType, "message/rfc822", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream(bytes);
                carried.Add(new MimeKit.MessagePart { Message = await MimeKit.MimeMessage.LoadAsync(ms, ct) });
            }
            else
            {
                carried.Add(GmailMime.Attachment(
                    Decode(p.Filename) is { Length: > 0 } n ? n : "attachment",
                    p.MimeType ?? "application/octet-stream", new MemoryStream(bytes)));
            }
        }

        return carried;
    }

    /// <summary>
    /// Sends a whole message through Gmail's upload endpoint: JSON metadata (the thread)
    /// and the raw RFC 822 message in one multipart/related request, which is the only
    /// form that carries both and takes up to Gmail's 35 MB.
    /// <para>
    /// The OP2-262 rule applies from the moment the request starts: an answer that is a
    /// clear refusal (4xx) means it did not go; no answer, or a 5xx after the message was
    /// handed over, means it MAY have gone, and that is a <see cref="MailMaybeSentException"/>
    /// — never a failure anyone should retry.
    /// </para>
    /// </summary>
    internal static async Task<string?> SendRawAsync(
        HttpClient http, Connection c, MimeKit.MimeMessage message, string? threadId, ILogger log, CancellationToken ct)
    {
        await using var raw = new MemoryStream();
        await GmailMime.WriteAsync(message, raw, ct);
        raw.Position = 0;
        return await SendRawAsync(http, c, raw, threadId, log, ct);
    }

    internal static async Task<string?> SendRawAsync(
        HttpClient http, Connection c, Stream raw, string? threadId, ILogger log, CancellationToken ct)
    {
        var metadata = new StringContent(
            threadId is null ? "{}" : JsonSerializer.Serialize(new { threadId }), Encoding.UTF8, "application/json");
        var mime = new StreamContent(raw);
        mime.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");

        using var body = new MultipartContent("related") { metadata, mime };

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync(UploadSend, body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            log.LogError(ex, "Gmail send for {Label} may or may not have gone: no answer.", c.Label);
            throw new MailMaybeSentException(c.Label);
        }

        using (resp)
        {
            if ((int)resp.StatusCode >= 500)
            {
                log.LogError("Gmail send for {Label} answered HTTP {Status} after the message was handed over.",
                    c.Label, (int)resp.StatusCode);
                throw new MailMaybeSentException(c.Label);
            }

            await EnsureAsync(resp, c, log, ct);
            var sent = await resp.Content.ReadFromJsonAsync<GRef>(Json, ct);
            return sent?.Id;
        }
    }

    public Task<ClearPreview> PreviewClearAsync(Connection connection, ClearTarget target, CancellationToken ct = default) =>
        throw NotYet(connection, "clearing mail");
    public Task<ClearOutcome> ClearAsync(Connection connection, ClearTarget target, int ceiling = 1000, CancellationToken ct = default) =>
        throw NotYet(connection, "clearing mail");
    public Task<ClearOutcome> RecoverAsync(Connection connection, string from, int ceiling = 1000, CancellationToken ct = default) =>
        throw NotYet(connection, "recovering mail");
    public Task<int> RecoverableCountAsync(Connection connection, string from, CancellationToken ct = default) =>
        throw NotYet(connection, "recovering mail");
    public Task MarkReadAsync(Connection connection, string messageId, bool read, CancellationToken ct = default) =>
        throw NotYet(connection, "marking mail read or unread");
    public Task<string> MoveAsync(Connection connection, string messageId, string destinationFolderId, CancellationToken ct = default) =>
        throw NotYet(connection, "moving mail");
    public Task<MailFolder> CreateFolderAsync(Connection connection, string name, string? parentFolderId = null, CancellationToken ct = default) =>
        throw NotYet(connection, "creating labels");
    public Task<MailFolder> RenameFolderAsync(Connection connection, string folderId, string name, CancellationToken ct = default) =>
        throw NotYet(connection, "renaming labels");
    public Task DeleteFolderAsync(Connection connection, string folderId, CancellationToken ct = default) =>
        throw NotYet(connection, "deleting labels");
    public Task<string?> MarkAsJunkAsync(Connection connection, string messageId, CancellationToken ct = default) =>
        throw NotYet(connection, "reporting spam");
    public Task FlagAsync(Connection connection, string messageId, bool flagged, DateTimeOffset? dueAt, CancellationToken ct = default) =>
        throw NotYet(connection, "starring mail");
    public Task<IReadOnlyList<string>> GetCategoriesAsync(Connection connection, string messageId, CancellationToken ct = default) =>
        throw NotYet(connection, "labelling mail");
    public Task SetCategoriesAsync(Connection connection, string messageId, IReadOnlyList<string> categories, CancellationToken ct = default) =>
        throw NotYet(connection, "labelling mail");
    public Task<IReadOnlyList<MailCategory>?> ListCategoriesAsync(Connection connection, CancellationToken ct = default) =>
        throw NotYet(connection, "listing categories");
    public Task<MailCategory?> CreateCategoryAsync(Connection connection, string name, string preset, CancellationToken ct = default) =>
        throw NotYet(connection, "creating categories");

    // ── headers, addresses, dates ────────────────────────────────────────────

    public static IReadOnlyDictionary<string, string> Headers(GPart? p)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in p?.Headers ?? [])
            if (h.Name is { } n && !d.ContainsKey(n)) d[n.ToLowerInvariant()] = h.Value ?? "";
        return d;
    }

    /// <summary>
    /// Every part of the message, depth first — but not the inside of an attached message.
    /// A forwarded email is one opaque attachment of this one: its text is not this
    /// message's body, and its attachments are not this message's attachments.
    /// </summary>
    private static IEnumerable<GPart> Walk(GPart? p)
    {
        if (p is null) yield break;
        yield return p;
        if (string.Equals(p.MimeType, "message/rfc822", StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var child in p.Parts ?? [])
            foreach (var d in Walk(child))
                yield return d;
    }

    /// <summary>
    /// RFC 2047 encoded-words (<c>=?utf-8?B?…?=</c>, <c>=?iso-8859-1?Q?…?=</c>) decoded.
    /// Gmail usually hands headers over decoded already, so on plain text this does
    /// nothing; adjacent encoded words joined by whitespace become one, as the RFC says.
    /// </summary>
    public static string Decode(string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.Contains("=?", StringComparison.Ordinal)) return header ?? "";

        var joined = AdjacentRx().Replace(header, "$1");
        return EncodedWordRx().Replace(joined, m =>
        {
            try
            {
                var enc = Encoding.GetEncoding(m.Groups["cs"].Value);
                var text = m.Groups["t"].Value;
                var bytes = m.Groups["e"].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(text)
                    : QDecode(text);
                return enc.GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                return m.Value;
            }
        });
    }

    private static byte[] QDecode(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '_') bytes.Add(0x20);
            else if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else bytes.Add((byte)s[i]);
        }
        return [.. bytes];
    }

    [GeneratedRegex(@"=\?(?<cs>[^?]+)\?(?<e>[BbQq])\?(?<t>[^?]*)\?=")]
    private static partial Regex EncodedWordRx();

    [GeneratedRegex(@"(=\?[^?]+\?[BbQq]\?[^?]*\?=)\s+(?==\?)")]
    private static partial Regex AdjacentRx();

    /// <summary>
    /// An address list split into addresses, on commas that are not inside a quoted
    /// display name or angle brackets — <c>"Lee, Jordan" &lt;j@x&gt;</c> is one person.
    /// </summary>
    public static IReadOnlyList<string> Addresses(string? header)
    {
        var s = Decode(header);
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, angled = false;

        foreach (var ch in s)
        {
            if (ch == '"' ) quoted = !quoted;
            else if (ch == '<' && !quoted) angled = true;
            else if (ch == '>' && !quoted) angled = false;

            if (ch == ',' && !quoted && !angled)
            {
                if (sb.ToString().Trim() is { Length: > 0 } one) result.Add(one);
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }

        if (sb.ToString().Trim() is { Length: > 0 } last) result.Add(last);
        return result;
    }

    /// <summary>
    /// The Date header (RFC 5322): <c>Mon, 3 Nov 2026 09:30:00 +0000 (UTC)</c>. Null when it
    /// cannot be read — a missing Sent time is better than a wrong one.
    /// </summary>
    public static DateTimeOffset? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;

        var s = CommentRx().Replace(date, "").Trim();

        // The day of the week is optional and informational (RFC 5322 §3.3), and senders get
        // it wrong; .NET refuses a date whose weekday disagrees with it, so it is dropped.
        s = WeekdayRx().Replace(s, "");
        s = ZoneRx().Replace(s, "$1$2:$3");           // +0000 → +00:00, which .NET reads
        s = s.Replace(" GMT", " +00:00").Replace(" UT", " +00:00");

        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex CommentRx();

    [GeneratedRegex(@"^[A-Za-z]{3},\s*")]
    private static partial Regex WeekdayRx();

    [GeneratedRegex(@"([+-])(\d{2})(\d{2})$")]
    private static partial Regex ZoneRx();

    public static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/').Replace("\n", "").Replace("\r", "");
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }

    // ── wire shapes (Gmail API v1) ───────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public sealed record GList(List<GRef>? Messages, string? NextPageToken, long ResultSizeEstimate);
    public sealed record GRef(string Id, string? ThreadId);

    public sealed record GMessage(
        string Id, string? ThreadId, List<string>? LabelIds, string? Snippet, string? InternalDate, GPart? Payload);

    public sealed record GPart(
        string? PartId, string? MimeType, string? Filename, List<GHeader>? Headers, GBody? Body, List<GPart>? Parts);

    public sealed record GHeader(string? Name, string? Value);
    public sealed record GBody(string? AttachmentId, long Size, string? Data);

    public sealed record GLabels(List<GLabel>? Labels);
    public sealed record GLabel(string Id, string Name, string? Type, int MessagesTotal, int MessagesUnread);

    private sealed record GError(GErrorBody? Error);
    private sealed record GErrorBody(int Code, string? Message, string? Status, List<GErrorItem>? Errors, List<GErrorItem>? Details);
    private sealed record GErrorItem(string? Reason, string? Domain);
}
