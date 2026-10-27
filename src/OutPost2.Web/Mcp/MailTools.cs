using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// The MCP tool surface. Per D1 this IS the product — the web app is configuration only
/// and never renders a mailbox, so every tool must be able to answer fully in speech.
/// <para>
/// Three rules shape every signature here:
/// <list type="bullet">
/// <item>No opaque identifiers in the conversational path. Graph message ids are long
/// base64 and unspeakable, so tools accept an ordinal ("the second one") resolved against
/// the last listing.</item>
/// <item>Responses are prose, not tables. There is no screen to fall back on.</item>
/// <item>Writes never ride on a default connection. Sending from the wrong tenant cannot
/// be undone.</item>
/// </list>
/// </para>
/// </summary>
[McpServerToolType]
public sealed class MailTools(
    IHttpContextAccessor http,
    IConnectionStore connections,
    IMailService mail,
    IMessageIndex index,
    OutPost2.Infrastructure.Mail.IAttachmentExtractor extractor)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    [McpServerTool(Name = "list_connections")]
    [Description("List the user's named connections — which mailboxes and trackers are available, and their health.")]
    public async Task<string> ListConnections(CancellationToken ct)
    {
        var all = await connections.ListAsync(UserId, ct);
        if (all.Count == 0)
            return "No connections set up yet. Add one at https://outpost.brewingcoder.com/connections";

        var sb = new StringBuilder();
        foreach (var c in all)
        {
            sb.Append($"- {c.Label} ({c.Provider}");
            if (c.IsDefaultForProvider) sb.Append(", default");
            sb.Append($"): {c.State}");

            var off = c.Capabilities.Where(x => x.Granted && !x.Enabled).Select(x => x.Name).ToList();
            if (off.Count > 0) sb.Append($" — switched off: {string.Join(", ", off)}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "list_mail")]
    [Description("List recent messages. Omit 'connection' to use the default mailbox, or pass 'all' to check every mailbox at once.")]
    public async Task<string> ListMail(
        [Description("Connection name such as 'work' or 'home'. Use 'all' for every mailbox. Omit only if a default mailbox has been set.")] string? connection,
        [Description("Only unread messages.")] bool unreadOnly = false,
        [Description("How many to return, per mailbox.")] int count = 10,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailRead, connection, isWrite: false);

        if (!resolution.IsActionable) return resolution.SpokenSummary;

        var results = new List<(string Label, IReadOnlyList<Core.Mail.MailSummary> Items, string? Error)>();

        // Fan out in parallel. One slow or throttled tenant must not stall the whole turn —
        // a partial answer now beats a complete answer after the conversation has moved on.
        await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try { results.Add((c.Label, await mail.ListAsync(c, "inbox", Math.Clamp(count, 1, 50), unreadOnly, ct), null)); }
            catch (MailAuthRequiredException) { results.Add((c.Label, [], "needs re-authorising")); }
            catch (Exception ex) { results.Add((c.Label, [], ex.Message)); }
        }));

        var ordered = results
            .SelectMany(r => r.Items.Select(i => (r.Label, Item: i)))
            .OrderByDescending(x => x.Item.ReceivedAt)
            .Take(Math.Clamp(count, 1, 50) * Math.Max(1, resolution.Connections.Count))
            .ToList();

        index.Remember(UserId, ordered.Select(x => (x.Item.Id, x.Label)).ToList());

        var sb = new StringBuilder();
        if (ordered.Count == 0)
            sb.AppendLine(unreadOnly ? "Nothing unread." : "No messages.");
        else
        {
            sb.AppendLine($"{ordered.Count} message{(ordered.Count == 1 ? "" : "s")}:");
            var n = 1;
            foreach (var (label, m) in ordered)
            {
                // Numbered, so the next turn can say "read the second one" instead of
                // anyone having to utter a base64 id.
                sb.Append($"{n++}. ");
                if (resolution.Connections.Count > 1) sb.Append($"[{label}] ");
                sb.Append($"{m.From} — {m.Subject}");
                if (!m.IsRead) sb.Append(" (unread)");
                if (m.HasAttachments) sb.Append(" (has attachments)");
                sb.AppendLine($" — {Ago(m.ReceivedAt)}");
            }
        }

        // Report degraded mailboxes explicitly: silence reads as "you have no mail there",
        // which is a lie.
        foreach (var bad in results.Where(r => r.Error is not null))
            sb.AppendLine($"Note: {bad.Label} {bad.Error}.");
        foreach (var d in resolution.Degraded)
            sb.AppendLine($"Note: {d.Label} is unavailable ({d.State}).");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "read_mail")]
    [Description("Read a message in full. Use the number from the last list, e.g. 2 for the second message.")]
    public async Task<string> ReadMail(
        [Description("The number shown in the last listing.")] int number,
        CancellationToken ct = default)
    {
        var remembered = index.Resolve(UserId, number);
        if (remembered is null)
            return "I don't have that one — list your messages first, then tell me the number.";

        var (messageId, label) = remembered.Value;

        var c = await connections.FindByLabelAsync(UserId, label, ct);
        if (c is null) return $"The {label} connection has gone.";

        try
        {
            var m = await mail.ReadAsync(c, messageId, ct);
            if (m is null) return "That message no longer exists.";

            var sb = new StringBuilder();
            sb.AppendLine($"From: {m.From}");
            sb.AppendLine($"Subject: {m.Subject}");
            sb.AppendLine($"Received: {Ago(m.ReceivedAt)} ({label})");
            if (m.HasAttachments) sb.AppendLine("Has attachments.");
            sb.AppendLine();
            sb.AppendLine(HtmlToText.Clamp(m.Body, 8000));
            return sb.ToString().TrimEnd();
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "search_mail")]
    [Description("Search messages by text. Omit 'connection' for the default mailbox, or pass 'all' to search everywhere.")]
    public async Task<string> SearchMail(
        [Description("What to search for.")] string query,
        [Description("Connection name, or 'all'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("How many results per mailbox.")] int count = 10,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailRead, connection, isWrite: false);

        if (!resolution.IsActionable) return resolution.SpokenSummary;

        var found = new List<(string Label, Core.Mail.MailSummary Item)>();
        await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try
            {
                foreach (var m in await mail.SearchAsync(c, query, Math.Clamp(count, 1, 50), ct))
                    found.Add((c.Label, m));
            }
            catch (Exception) { /* reported below via Degraded/empty */ }
        }));

        if (found.Count == 0) return $"Nothing found for \"{query}\".";

        index.Remember(UserId, found.Select(x => (x.Item.Id, x.Label)).ToList());

        var sb = new StringBuilder($"{found.Count} result{(found.Count == 1 ? "" : "s")} for \"{query}\":\n");
        var n = 1;
        foreach (var (label, m) in found.OrderByDescending(x => x.Item.ReceivedAt))
        {
            sb.Append($"{n++}. ");
            if (resolution.Connections.Count > 1) sb.Append($"[{label}] ");
            sb.AppendLine($"{m.From} — {m.Subject} — {Ago(m.ReceivedAt)}");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "send_mail")]
    [Description("Send an email. Body may use Markdown. Always name the connection to send from.")]
    public async Task<string> SendMail(
        [Description("Recipient addresses, comma separated.")] string to,
        [Description("Subject line.")] string subject,
        [Description("Body. Markdown is supported.")] string body,
        [Description("Which connection to send from, e.g. 'work'. Required when more than one could send.")] string? connection = null,
        [Description("CC addresses, comma separated.")] string? cc = null,
        CancellationToken ct = default)
    {
        // isWrite: true. An ambiguous send ASKS rather than using the default — you cannot
        // unsend, and the recipient sees whichever identity we guessed.
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailSend, connection, isWrite: true);

        if (!resolution.IsActionable) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        var recipients = Split(to);
        if (recipients.Count == 0) return "I need at least one recipient.";

        try
        {
            await mail.SendAsync(c, new Core.Mail.OutgoingMail
            {
                To = recipients, Cc = Split(cc), Subject = subject, Body = body, IsHtml = true,
            }, ct);

            return $"Sent from {c.Label} to {string.Join(", ", recipients)}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "list_attachments")]
    [Description("List the attachments on a message, using the number from the last listing.")]
    public async Task<string> ListAttachments(
        [Description("The number shown in the last listing.")] int number,
        CancellationToken ct = default)
    {
        var remembered = index.Resolve(UserId, number);
        if (remembered is null) return "I don't have that one — list your messages first.";

        var (messageId, label) = remembered.Value;
        var c = await connections.FindByLabelAsync(UserId, label, ct);
        if (c is null) return $"The {label} connection has gone.";

        var items = await mail.ListAttachmentsAsync(c, messageId, ct);
        if (items.Count == 0) return "That message has no attachments.";

        var sb = new StringBuilder($"{items.Count} attachment{(items.Count == 1 ? "" : "s")}:\n");
        var n = 1;
        foreach (var a in items)
        {
            sb.Append($"{n++}. {a.Name} ({a.HumanSize})");
            if (a.IsLink) sb.Append(" — a cloud link, not a file");
            else if (a.IsEmbeddedItem) sb.Append(" — an embedded message");
            else if (extractor.CanExtract(a.Name, a.ContentType)) sb.Append(" — I can read this");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "read_attachment")]
    [Description("Read the text content of an attachment — PDF, Word, Excel, PowerPoint or plain text. Use the message number and the attachment number.")]
    public async Task<string> ReadAttachment(
        [Description("The message number from the last listing.")] int messageNumber,
        [Description("Which attachment, from list_attachments. Defaults to the first.")] int attachmentNumber = 1,
        CancellationToken ct = default)
    {
        var remembered = index.Resolve(UserId, messageNumber);
        if (remembered is null) return "I don't have that message — list your messages first.";

        var (messageId, label) = remembered.Value;
        var c = await connections.FindByLabelAsync(UserId, label, ct);
        if (c is null) return $"The {label} connection has gone.";

        var items = await mail.ListAttachmentsAsync(c, messageId, ct);
        if (items.Count == 0) return "That message has no attachments.";

        var i = attachmentNumber - 1;
        if (i < 0 || i >= items.Count)
            return $"There {(items.Count == 1 ? "is only 1 attachment" : $"are only {items.Count} attachments")}.";

        var a = items[i];

        // Saying why is better than failing: a cloud link genuinely has no bytes to read,
        // and the user can act on knowing that.
        if (a.IsLink)
            return $"\"{a.Name}\" is a cloud link rather than a file, so there is nothing to read directly.";
        if (a.IsEmbeddedItem)
            return $"\"{a.Name}\" is an embedded message rather than a document.";
        if (!extractor.CanExtract(a.Name, a.ContentType))
            return $"I can't read {a.Name} ({a.ContentType}). I handle PDF, Word, Excel, PowerPoint and text.";

        await using var bytes = await mail.OpenAttachmentAsync(c, messageId, a.Id, ct);
        if (bytes is null) return $"Couldn't download {a.Name}.";

        var text = await extractor.ExtractAsync(a.Name, bytes, ct);
        return $"{a.Name}:{Environment.NewLine}{Environment.NewLine}{HtmlToText.Clamp(text, 12000)}";
    }

    private static List<string> Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Relative time, because "received 2026-10-27T18:42:11Z" is unspeakable.</summary>
    private static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} minutes ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} hour{((int)d.TotalHours == 1 ? "" : "s")} ago";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";
        return when.ToString("d MMM");
    }
}
