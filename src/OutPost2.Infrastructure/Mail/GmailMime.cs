using System.Net;
using MimeKit;
using OutPost2.Core.Mail;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// Composing the RFC 5322 messages Gmail sends (OP2-281). Gmail takes a whole message,
/// not a JSON description of one, so everything Graph does for Outlook — the headers, the
/// alternative parts, the quoted original, the threading — is done here, with MimeKit for
/// the parts that are easy to get subtly wrong (header encoding, folding, addresses).
/// </summary>
public static class GmailMime
{
    public sealed record Original(
        string Subject, string? MessageId, string? References,
        IReadOnlyList<MailRecipient> From, IReadOnlyList<MailRecipient> ReplyTo,
        IReadOnlyList<MailRecipient> To, IReadOnlyList<MailRecipient> Cc,
        string? Date, string? Html, string? Plain);

    /// <summary>
    /// A message: HTML with a plain-text alternative (clients that cannot show HTML, and
    /// spam filters, both want one), then any attachments.
    /// </summary>
    public static MimeMessage Compose(
        string? fromAddress,
        IEnumerable<MailRecipient> to, IEnumerable<MailRecipient> cc,
        string subject, string html, MailImportance importance,
        IEnumerable<MimeEntity>? attachments = null,
        string? inReplyTo = null, string? references = null)
    {
        var message = new MimeMessage();

        // Left out when unknown: Gmail fills From with the signed-in account itself, which
        // is the right answer anyway. When known it is set so the message is complete.
        if (!string.IsNullOrWhiteSpace(fromAddress)) message.From.Add(Mailbox(new MailRecipient(null, fromAddress)));

        foreach (var r in to) message.To.Add(Mailbox(r));
        foreach (var r in cc) message.Cc.Add(Mailbox(r));
        message.Subject = subject;

        if (inReplyTo is not null) message.InReplyTo = inReplyTo;
        foreach (var id in MessageIds(references)) message.References.Add(id);
        if (inReplyTo is not null && !message.References.Contains(inReplyTo)) message.References.Add(inReplyTo);

        // Gmail has no importance flag of its own; these are the headers Outlook and most
        // other clients read (OP2-276).
        switch (importance)
        {
            case MailImportance.High:
                message.Importance = MessageImportance.High;
                message.Headers.Replace("X-Priority", "1 (Highest)");
                break;
            case MailImportance.Low:
                message.Importance = MessageImportance.Low;
                message.Headers.Replace("X-Priority", "5 (Lowest)");
                break;
        }

        var body = new MultipartAlternative
        {
            new TextPart("plain") { Text = HtmlToText.Convert(html) },
            new TextPart("html") { Text = html },
        };

        var files = attachments?.ToList() ?? [];
        if (files.Count == 0)
        {
            message.Body = body;
        }
        else
        {
            var mixed = new Multipart("mixed") { body };
            foreach (var f in files) mixed.Add(f);
            message.Body = mixed;
        }

        return message;
    }

    public static MimePart Attachment(string fileName, string contentType, Stream content)
    {
        var type = ContentType.TryParse(contentType, out var parsed) ? parsed : new ContentType("application", "octet-stream");
        return new MimePart(type)
        {
            Content = new MimeContent(content),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName,
        };
    }

    /// <summary>
    /// A recipient as a MIME address. MimeKit refuses an address it cannot make valid —
    /// which includes one carrying a line break, the shape of a header-injection attempt —
    /// and that refusal is said in the user's terms, before anything is sent.
    /// </summary>
    private static MailboxAddress Mailbox(MailRecipient r)
    {
        try
        {
            return new MailboxAddress(string.IsNullOrWhiteSpace(r.Name) ? "" : r.Name, r.Address);
        }
        catch (ParseException)
        {
            throw new MailException(
                $"\"{r.Address.ReplaceLineEndings(" ")}\" isn't an email address Outpost can send to. Nothing was sent.");
        }
    }

    private static IEnumerable<string> MessageIds(string? references) =>
        string.IsNullOrWhiteSpace(references)
            ? []
            : MimeKit.Utils.MimeUtils.EnumerateReferences(references);

    /// <summary>Addresses from a header, as recipients. Groups are flattened; anything unparseable is dropped.</summary>
    public static IReadOnlyList<MailRecipient> Recipients(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !InternetAddressList.TryParse(header, out var list)) return [];
        return list.Mailboxes
            .Where(m => !string.IsNullOrWhiteSpace(m.Address))
            .Select(m => new MailRecipient(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address))
            .ToList();
    }

    /// <summary>
    /// Who a reply goes to, before the caller's edits — Outlook's rules, which are also
    /// Gmail's:
    /// <list type="bullet">
    /// <item>Reply: Reply-To if the sender set one, else From.</item>
    /// <item>Reply all: that, plus everyone else on To and Cc, without the user themself.</item>
    /// <item>Replying to your own sent message: back to the people you sent it to, not to
    /// yourself.</item>
    /// </list>
    /// </summary>
    public static (List<MailRecipient> To, List<MailRecipient> Cc) ReplyRecipients(
        Original o, ReplyKind kind, string? self)
    {
        bool IsSelf(MailRecipient r) => self is not null && Same(r.Address, self);

        var fromSelf = o.From.Count > 0 && o.From.All(IsSelf);
        var primary = fromSelf ? o.To.ToList() : (o.ReplyTo.Count > 0 ? o.ReplyTo : o.From).ToList();

        var to = new List<MailRecipient>();
        var cc = new List<MailRecipient>();

        void Add(List<MailRecipient> into, MailRecipient r)
        {
            if (IsSelf(r)) return;
            if (to.Concat(cc).Any(x => Same(x.Address, r.Address))) return;
            into.Add(r);
        }

        foreach (var r in primary) Add(to, r);

        if (kind == ReplyKind.All)
        {
            foreach (var r in o.To) Add(to, r);
            foreach (var r in o.Cc) Add(cc, r);
        }

        return (to, cc);
    }

    public static string Subject(ReplyKind kind, string original)
    {
        var s = original.Trim();
        if (kind == ReplyKind.Forward)
            return s.StartsWith("fwd:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("fw:", StringComparison.OrdinalIgnoreCase)
                ? s : "Fwd: " + s;

        return s.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? s : "Re: " + s;
    }

    /// <summary>
    /// The new text, then the original — quoted the way Gmail quotes it for a reply, or
    /// under Gmail's own "Forwarded message" header for a forward — so the message reads
    /// on the other end like one a person wrote in Gmail.
    /// </summary>
    public static string WithOriginal(string newHtml, ReplyKind kind, Original o)
    {
        var originalHtml = o.Html ?? (o.Plain is null
            ? ""
            : "<div style=\"white-space:pre-wrap\">" + WebUtility.HtmlEncode(o.Plain) + "</div>");

        var from = WebUtility.HtmlEncode(string.Join(", ", o.From.Select(r => r.Display)));
        var date = WebUtility.HtmlEncode(o.Date ?? "");

        if (kind == ReplyKind.Forward)
        {
            var to = WebUtility.HtmlEncode(string.Join(", ", o.To.Select(r => r.Display)));
            var cc = o.Cc.Count == 0 ? "" : $"Cc: {WebUtility.HtmlEncode(string.Join(", ", o.Cc.Select(r => r.Display)))}<br>";
            return newHtml
                + "<br><div class=\"gmail_quote\"><div dir=\"ltr\" class=\"gmail_attr\">"
                + "---------- Forwarded message ---------<br>"
                + $"From: {from}<br>Date: {date}<br>Subject: {WebUtility.HtmlEncode(o.Subject)}<br>To: {to}<br>{cc}"
                + "</div><br><br>" + originalHtml + "</div>";
        }

        return newHtml
            + "<br><div class=\"gmail_quote\"><div dir=\"ltr\" class=\"gmail_attr\">"
            + $"On {date}, {from} wrote:<br></div>"
            + "<blockquote class=\"gmail_quote\" style=\"margin:0px 0px 0px 0.8ex;border-left:1px solid rgb(204,204,204);padding-left:1ex\">"
            + originalHtml + "</blockquote></div>";
    }

    /// <summary>The whole message, as Gmail's upload endpoint takes it.</summary>
    public static async Task WriteAsync(MimeMessage message, Stream into, CancellationToken ct) =>
        await message.WriteToAsync(FormatOptions.Default, into, ct);

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
