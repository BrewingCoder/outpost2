using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// The MCP tool surface. Per D1 this IS the product — the web app is configuration only
/// and never renders a mailbox, so every tool must be able to answer fully in speech.
/// <para>
/// Four rules shape every signature here:
/// <list type="bullet">
/// <item>No opaque identifiers in the conversational path. Graph message ids are long
/// base64 and unspeakable, so listings carry a short handle and tools accept that, or an
/// ordinal ("the second one") resolved against the last listing.</item>
/// <item>Anything that changes the mailbox takes the handle, not the ordinal, and reads
/// back the subject it acted on. See <see cref="ItemIndex"/>.</item>
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
    IItemIndex index,
    OutPost2.Infrastructure.Mail.IAttachmentExtractor extractor,
    PublicUrls urls)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    [McpServerTool(Name = "list_connections", ReadOnly = true, OpenWorld = true)]
    [Description("List the user's named connections — which mailboxes and trackers are available, and their health.")]
    public async Task<string> ListConnections(CancellationToken ct)
    {
        var all = await connections.ListAsync(UserId, ct);
        if (all.Count == 0)
            return $"No connections set up yet. Add one at {urls.Connections}";

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

    [McpServerTool(Name = "list_mail", ReadOnly = true, OpenWorld = true)]
    [Description("List recent messages. Omit 'connection' to use the default mailbox, or pass 'all' to check every mailbox at once. Each result carries a handle in square brackets — pass that to read or act on it.")]
    public async Task<string> ListMail(
        [Description("Connection name such as 'work' or 'home'. Use 'all' for every mailbox. Omit only if a default mailbox has been set.")] string? connection,
        [Description("Only unread messages.")] bool unreadOnly = false,
        [Description("How many to return, per mailbox.")] int count = 10,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailRead, connection, isWrite: false);

        if (!resolution.IsActionable) return resolution.SpokenSummary;

        // Fan out in parallel. One slow or throttled tenant must not stall the whole turn —
        // a partial answer now beats a complete answer after the conversation has moved on.
        //
        // Returned, not added to a shared list: these tasks run at the same time, and
        // concurrent List<T>.Add can drop an entry or corrupt the list. Task.WhenAll hands
        // back the results in order and costs nothing.
        var results = await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try
            {
                var items = await mail.ListAsync(c, "inbox", Math.Clamp(count, 1, 50), unreadOnly, ct);
                return (c.Label, Items: items, Error: (string?)null);
            }
            catch (MailAuthRequiredException)
            {
                return (c.Label, Items: (IReadOnlyList<MailSummary>)[], Error: "needs re-authorising");
            }
            catch (Exception ex)
            {
                return (c.Label, Items: (IReadOnlyList<MailSummary>)[], Error: ex.Message);
            }
        }));

        var ordered = results
            .SelectMany(r => r.Items.Select(i => (r.Label, Item: i)))
            .OrderByDescending(x => x.Item.ReceivedAt)
            .Take(Math.Clamp(count, 1, 50) * Math.Max(1, resolution.Connections.Count))
            .ToList();

        var refs = index.Remember(UserId,
            ordered.Select(x => (x.Item.Id, x.Label, x.Item.Subject, x.Item.From)).ToList());

        var sb = new StringBuilder();
        if (ordered.Count == 0)
            sb.AppendLine(unreadOnly ? "Nothing unread." : "No messages.");
        else
        {
            sb.AppendLine($"{ordered.Count} message{(ordered.Count == 1 ? "" : "s")}:");
            for (var n = 0; n < ordered.Count; n++)
            {
                var (label, m) = ordered[n];

                // Numbered so the next turn can say "read the second one", and handled so
                // anything that changes the mailbox has something that cannot shift under it.
                sb.Append($"{n + 1}. [{refs[n].Ref}] ");
                if (resolution.Connections.Count > 1) sb.Append($"[{label}] ");
                sb.Append($"{Untrusted.Scrub(m.From)} — {Untrusted.Scrub(m.Subject)}");
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

    [McpServerTool(Name = "read_mail", ReadOnly = true, OpenWorld = true)]
    [Description("Read a message in full. Pass the handle from the last listing, e.g. 'a7kq', or the number, e.g. '2'.")]
    public async Task<string> ReadMail(
        [Description("The handle in square brackets from the listing, or the number shown beside it.")] string message,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailRead);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        try
        {
            var m = await mail.ReadAsync(c, item.ItemId, ct);
            if (m is null) return "That message no longer exists.";

            var sb = new StringBuilder();
            sb.AppendLine($"From: {Untrusted.Scrub(m.From)}");
            sb.AppendLine($"Subject: {Untrusted.Scrub(m.Subject)}");
            sb.AppendLine($"Received: {Ago(m.ReceivedAt)} ({c.Label})");
            sb.AppendLine($"Handle: {item.Ref}");
            if (m.HasAttachments) sb.AppendLine("Has attachments.");
            sb.AppendLine();
            sb.AppendLine(Untrusted.Fence("message body", HtmlToText.Clamp(m.Body, 8000)));
            return sb.ToString().TrimEnd();
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "search_mail", ReadOnly = true, OpenWorld = true)]
    [Description("Search messages by text. Omit 'connection' for the default mailbox, or pass 'all' to search everywhere. Results carry a handle in square brackets.")]
    public async Task<string> SearchMail(
        [Description("What to search for.")] string query,
        [Description("Connection name, or 'all'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("How many results per mailbox.")] int count = 10,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailRead, connection, isWrite: false);

        if (!resolution.IsActionable) return resolution.SpokenSummary;

        // Each task RETURNS its outcome rather than adding to a shared list. These run
        // concurrently, and List<T>.Add from several of them at once can lose an entry or
        // corrupt the list outright — a bug that only shows up for the users who have
        // more than one mailbox, which is everybody this product is for.
        var perConnection = await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try
            {
                var hits = await mail.SearchAsync(c, query, Math.Clamp(count, 1, 50), ct);
                return (c.Label, Items: hits, Error: (string?)null);
            }
            // The comment that used to stand here said these were "reported below via
            // Degraded/empty". They were not: a mailbox that threw was indistinguishable
            // from one with no matches, so a failed search across three mailboxes read as
            // a confident answer from all three (OP2-145). list_mail already collects and
            // names them; this now does the same.
            catch (MailAuthRequiredException)
            {
                return (c.Label, Items: (IReadOnlyList<MailSummary>)[], Error: "needs re-authorising");
            }
            catch (Exception ex)
            {
                return (c.Label, Items: (IReadOnlyList<MailSummary>)[], Error: ex.Message);
            }
        }));

        var found = perConnection
            .SelectMany(r => r.Items.Select(i => (r.Label, Item: i)))
            .ToList();

        var failed = perConnection
            .Where(r => r.Error is not null)
            .Select(r => (r.Label, Error: r.Error!))
            .ToList();

        if (found.Count == 0)
            return failed.Count == 0
                ? $"Nothing found for \"{query}\"."
                : $"Nothing found for \"{query}\". "
                  + string.Join(" ", failed.Select(f => $"Note: {f.Label} {f.Error}."));

        var ordered = found.OrderByDescending(x => x.Item.ReceivedAt).ToList();
        var refs = index.Remember(UserId,
            ordered.Select(x => (x.Item.Id, x.Label, x.Item.Subject, x.Item.From)).ToList());

        var sb = new StringBuilder($"{ordered.Count} result{(ordered.Count == 1 ? "" : "s")} for \"{query}\":\n");
        for (var n = 0; n < ordered.Count; n++)
        {
            var (label, m) = ordered[n];
            sb.Append($"{n + 1}. [{refs[n].Ref}] ");
            if (resolution.Connections.Count > 1) sb.Append($"[{label}] ");
            sb.AppendLine($"{Untrusted.Scrub(m.From)} — {Untrusted.Scrub(m.Subject)} — {Ago(m.ReceivedAt)}");
        }

        // A partial answer has to say that it is partial. Silence here reads as "there is
        // nothing else", which is the one thing it is not entitled to claim.
        foreach (var bad in failed)
            sb.AppendLine($"Note: {bad.Label} {bad.Error}.");
        foreach (var d in resolution.Degraded)
            sb.AppendLine($"Note: {d.Label} is unavailable ({d.State}).");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "send_mail", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Send a NEW email. To answer one you have been reading, use reply_mail instead. Body may use Markdown. Always name the connection to send from.")]
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

        // "send it from all my accounts" resolves to FanOut, which carries no single
        // Connection — so without this the dereference below is a null reference on a
        // perfectly sayable instruction. Sending the same message from three identities
        // is not what anyone means by it either.
        if (resolution.Kind is ResolutionKind.FanOut)
            return "I can only send from one mailbox — which one: "
                 + string.Join(", ", resolution.Connections.Select(x => x.Label)) + "?";

        var c = resolution.Connection!;

        var recipients = Split(to);
        if (recipients.Count == 0) return "I need at least one recipient.";

        try
        {
            await mail.SendAsync(c, new OutgoingMail
            {
                To = recipients, Cc = Split(cc), Subject = subject, Body = body, IsHtml = true,
            }, ct);

            return $"Sent from {c.Label} to {string.Join(", ", recipients)}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "reply_mail", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Reply to a message, keeping the thread and quoting the original. Pass the handle from the listing. Set replyAll to include everyone who was copied.")]
    public async Task<string> ReplyMail(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("What to say. Markdown is supported. Goes above the quoted original.")] string body,
        [Description("Reply to everyone on the thread rather than just the sender.")] bool replyAll = false,
        CancellationToken ct = default)
    {
        // The thread decides which mailbox and which identity, so there is nothing for the
        // resolver to ask about and the capabilities are the only gate. Both of them:
        // sending is obvious, filing less so — Graph composes a threaded reply as a draft
        // in the mailbox before it goes anywhere.
        var found = await ResolveAsync(message, ct, Capabilities.MailSend, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        if (string.IsNullOrWhiteSpace(body)) return "I need something to say in the reply.";

        try
        {
            var subject = await mail.SendReplyAsync(
                c, item.ItemId, replyAll ? ReplyKind.All : ReplyKind.Sender, body, null, ct);

            return replyAll
                ? $"Replied to everyone on \"{subject}\" from {c.Label}."
                : $"Replied to {item.From} on \"{subject}\" from {c.Label}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "forward_mail", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Forward a message to someone else, quoting the original. Pass the handle from the listing.")]
    public async Task<string> ForwardMail(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("Who to forward it to, comma separated.")] string to,
        [Description("A note to put above the forwarded message. Markdown is supported.")] string? body = null,
        CancellationToken ct = default)
    {
        // Both: sending is obvious, filing is not — Graph composes a threaded reply as a
        // draft in the mailbox before sending it.
        var found = await ResolveAsync(message, ct, Capabilities.MailSend, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        var recipients = Split(to);
        if (recipients.Count == 0) return "I need someone to forward it to.";

        try
        {
            var subject = await mail.SendReplyAsync(
                c, item.ItemId, ReplyKind.Forward, body ?? "", recipients, ct);

            return $"Forwarded \"{subject}\" to {string.Join(", ", recipients)} from {c.Label}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "mark_mail", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Mark a message read or unread. Pass the handle from the listing.")]
    public async Task<string> MarkMail(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("True to mark read, false to mark unread.")] bool read = true,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        try
        {
            await mail.MarkReadAsync(c, item.ItemId, read, ct);
            return $"Marked \"{item.Subject}\" {(read ? "read" : "unread")} in {c.Label}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "move_mail", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("File a message away. Destination can be 'archive', 'trash' (Deleted Items), 'inbox', 'junk', or the name of any folder in the mailbox. This is how you delete or archive something. Pass the handle from the listing.")]
    public async Task<string> MoveMail(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("Where to put it: 'archive', 'trash', 'inbox', 'junk', or a folder name.")] string destination = "archive",
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        try
        {
            var target = WellKnown(destination);
            string spoken;

            if (target is not null)
                spoken = Spoken(destination);
            else
            {
                // Only list folders when the destination is not one of the standard ones.
                // A round trip per archive would be a round trip nobody asked for.
                var folders = await mail.ListFoldersAsync(c, ct);
                var match = folders.FirstOrDefault(f => Same(f.Name, destination))
                            ?? folders.FirstOrDefault(f => Same(f.Path, destination));

                if (match is null)
                    return $"No folder called \"{destination}\" in {c.Label}. There is: " +
                           string.Join(", ", folders.Select(f => f.Path)) + ".";

                target = match.Id;
                spoken = match.Path;
            }

            var movedId = await mail.MoveAsync(c, item.ItemId, target, ct);

            // Graph reissues the id, so the handle has to follow the message or the next
            // "put it back" acts on an address that no longer resolves.
            index.Retarget(UserId, item.Ref, movedId);

            return $"Moved \"{item.Subject}\" to {spoken} in {c.Label}. Handle is still {item.Ref}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "report_junk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Report a message as junk or phishing: blocks the sender and files it in Junk Email, which trains the filter. Prefer this over deleting spam. Pass the handle from the listing.")]
    public async Task<string> ReportJunk(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("True if it looks like a phishing attempt rather than ordinary spam.")] bool phishing = false,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        try
        {
            var movedId = await mail.MarkAsJunkAsync(c, item.ItemId, ct);
            if (movedId is not null) index.Retarget(UserId, item.Ref, movedId);

            var said = $"Reported \"{item.Subject}\" as junk in {c.Label}. {item.From} is now blocked " +
                       "and the message is in Junk Email.";

            // Said plainly rather than quietly skipped: submitting a phish to Microsoft for
            // analysis is a different action from blocking a sender, and this cannot do it.
            // Letting the user believe otherwise is worse than telling them to press a button.
            if (phishing)
                said += " Microsoft's phishing analysis needs the Report button in Outlook itself — " +
                        "this blocks the sender but does not submit the message to them.";

            return said;
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "flag_mail", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Flag a message for follow-up, optionally with a due date, or clear an existing flag. Pass the handle from the listing.")]
    public async Task<string> FlagMail(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("True to flag, false to clear the flag.")] bool flagged = true,
        [Description("Days from now it is due. Omit for a flag with no date.")] int? dueInDays = null,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        var due = flagged && dueInDays is > 0
            ? DateTimeOffset.UtcNow.AddDays(dueInDays.Value)
            : (DateTimeOffset?)null;

        try
        {
            await mail.FlagAsync(c, item.ItemId, flagged, due, ct);

            if (!flagged) return $"Cleared the flag on \"{item.Subject}\" in {c.Label}.";

            return due is null
                ? $"Flagged \"{item.Subject}\" in {c.Label}."
                : $"Flagged \"{item.Subject}\" in {c.Label}, due {due:d MMM yyyy}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "list_categories", ReadOnly = true, OpenWorld = true)]
    [Description("List the Outlook colour categories defined on a mailbox — the names a message may be filed under.")]
    public async Task<string> ListCategories(
        [Description("Connection name such as 'work'. Omit only if a default mailbox has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailRead, connection, isWrite: false);

        if (resolution.Kind != ResolutionKind.Single) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        try
        {
            var all = await mail.ListCategoriesAsync(c, ct);
            if (all is null) return PaletteUnreadable(c.Label);
            if (all.Count == 0) return $"No categories defined on {c.Label} yet.";

            return $"{all.Count} categor{(all.Count == 1 ? "y" : "ies")} on {c.Label}: " +
                   string.Join(", ", all.Select(x => $"{x.Name} ({x.Colour})")) + ".";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "categorise_mail", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Apply or remove Outlook colour categories on a message. Pass the handle from the listing. Categories must already exist on the mailbox unless 'colour' is given, which creates the missing one.")]
    public async Task<string> CategoriseMail(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("Category names, comma separated.")] string categories,
        [Description("True to take these categories off instead of adding them.")] bool remove = false,
        [Description("Colour to create any missing category with: red, orange, blue, green, purple and so on. Omit to refuse unknown names instead of creating them.")] string? colour = null,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        var wanted = Split(categories);
        if (wanted.Count == 0) return "Name at least one category.";

        try
        {
            var defined = await mail.ListCategoriesAsync(c, ct);

            // The palette is unreadable on a connection consented before MailboxSettings
            // was asked for. Applying a name still works, so the category goes on and the
            // reply says what could not be checked — refusing here would make the whole
            // feature wait on a re-authorisation the user may never get round to.
            if (defined is null)
            {
                if (colour is not null)
                    return $"I can't create categories on {c.Label} — {PaletteUnreadable(c.Label)}";

                var applied = await ApplyAsync(c, item, wanted, remove, ct);
                return applied + (remove
                    ? ""
                    : $" I couldn't check {c.Label}'s category list first, so if a name is new "
                      + "it will show without a colour until it's added in Outlook.");
            }

            if (!remove)
            {
                var missing = wanted
                    .Where(w => !defined.Any(d => Same(d.Name, w)))
                    .ToList();

                if (missing.Count > 0)
                {
                    // Creating a category is mailbox-wide and shows up in Outlook forever,
                    // so it happens only when asked for by name — a typo must not quietly
                    // leave "Ugrent" in the palette.
                    if (colour is null)
                        return $"{c.Label} has no categor{(missing.Count == 1 ? "y" : "ies")} called " +
                               $"{string.Join(", ", missing)}. It has: " +
                               string.Join(", ", defined.Select(d => d.Name)) +
                               ". Say which colour to create them with if you want them added.";

                    var preset = CategoryColours.Preset(colour);
                    if (preset is null)
                        return $"\"{colour}\" isn't one of Outlook's category colours. Try: " +
                               string.Join(", ", CategoryColours.All.Take(9)) + ".";

                    foreach (var name in missing)
                        if (await mail.CreateCategoryAsync(c, name, preset, ct) is null)
                            return $"I can't create categories on {c.Label} — {PaletteUnreadable(c.Label)}";
                }
            }

            return await ApplyAsync(c, item, wanted, remove, ct);
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "list_attachments", ReadOnly = true, OpenWorld = true)]
    [Description("List the attachments on a message. Pass the handle from the listing, or the number.")]
    public async Task<string> ListAttachments(
        [Description("The handle from the listing, or the number shown beside it.")] string message,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailRead);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        var items = await mail.ListAttachmentsAsync(c, item.ItemId, ct);
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

    [McpServerTool(Name = "read_attachment", ReadOnly = true, OpenWorld = true)]
    [Description("Read the text content of an attachment — PDF, Word, Excel, PowerPoint or plain text. Pass the message handle and the attachment number.")]
    public async Task<string> ReadAttachment(
        [Description("The message handle from the listing, or the number shown beside it.")] string message,
        [Description("Which attachment, from list_attachments. Defaults to the first.")] int attachmentNumber = 1,
        CancellationToken ct = default)
    {
        var found = await ResolveAsync(message, ct, Capabilities.MailRead);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        var items = await mail.ListAttachmentsAsync(c, item.ItemId, ct);
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

        await using var bytes = await mail.OpenAttachmentAsync(c, item.ItemId, a.Id, ct);
        if (bytes is null) return $"Couldn't download {a.Name}.";

        var text = await extractor.ExtractAsync(a.Name, bytes, ct);
        return $"{Untrusted.Scrub(a.Name)}:{Environment.NewLine}{Environment.NewLine}"
             + Untrusted.Fence("attachment text", HtmlToText.Clamp(text, 12000));
    }

    // ── shared plumbing ──────────────────────────────────────────────────────

    /// <summary>
    /// Read-modify-write, because Graph replaces the whole array. Sending only the new
    /// names would silently strip every category already on the message.
    /// </summary>
    private async Task<string> ApplyAsync(
        Connection c, ItemRef item, List<string> wanted, bool remove, CancellationToken ct)
    {
        var current = await mail.GetCategoriesAsync(c, item.ItemId, ct);

        var next = remove
            ? current.Where(x => !wanted.Any(w => Same(w, x))).ToList()
            : current.Concat(wanted.Where(w => !current.Any(x => Same(x, w)))).ToList();

        await mail.SetCategoriesAsync(c, item.ItemId, next, ct);

        return next.Count == 0
            ? $"\"{item.Subject}\" now has no categories in {c.Label}."
            : $"\"{item.Subject}\" is now categorised {string.Join(", ", next)} in {c.Label}.";
    }

    /// <summary>
    /// Says which thing is missing and what fixes it. The palette lives in mailbox
    /// settings rather than in mail, so this is a consent gap and not a bug — and the user
    /// can only act on it if the difference is spelled out.
    /// </summary>
    private string PaletteUnreadable(string label) =>
        $"{label} was connected before Outpost asked for permission to read Outlook's "
        + "category list, so I can't see or add to it. Reconnecting it at "
        + $"{urls.Connections} fixes that.";

    private sealed record Resolved(ItemRef? Item, Connection? Connection, string? Problem);

    /// <summary>
    /// Turns what the conversation said into the message AND the connection that holds it,
    /// refusing when the connection cannot do what is being asked.
    /// <para>
    /// The capability is checked here rather than at the Graph call because the message
    /// decides the mailbox — the user never names one for an action, so there is no
    /// resolver step to catch a mailbox with filing switched off.
    /// </para>
    /// </summary>
    private async Task<Resolved> ResolveAsync(
        string message, CancellationToken ct, params string[] capabilities)
    {
        var item = index.Resolve(UserId, message);
        if (item is null)
            return new(null, null, "I don't have that one — list your messages first, then tell me which.");

        // Handles are shared with the calendar, so an ordinal said after a list of events
        // lands here pointing at a meeting. Say what it is instead of asking Graph for a
        // message by an event id and reporting "that message no longer exists".
        if (item.Kind is not ItemKind.Mail)
            return new(null, null,
                $"\"{item.Ref}\" is a calendar event — {item.Subject}. Use the calendar tools for it.");

        var c = await connections.FindByLabelAsync(UserId, item.ConnectionLabel, ct);
        if (c is null) return new(null, null, $"The {item.ConnectionLabel} connection has gone.");

        // Every capability, not the first one that happens to be present. A threaded reply
        // needs both send AND filing, because Graph builds it as a draft in the mailbox —
        // and a connection with filing switched off would otherwise pass this check and
        // then fail on the first of three calls with a bare 403.
        foreach (var capability in capabilities)
        {
            var cap = c.Capabilities.FirstOrDefault(x => x.Name == capability);
            if (cap is null || !cap.Usable)
                return new(null, null, ConnectionResolution.Unavailable(c, capability).SpokenSummary);
        }

        return new(item, c, null);
    }

    /// <summary>
    /// Graph accepts a well-known folder name wherever it accepts a folder id, so the
    /// common destinations need no lookup at all. The synonyms are what people actually
    /// say: nobody asks to move something to "deleteditems".
    /// </summary>
    public static string? WellKnown(string? said) => (said ?? "").Trim().ToLowerInvariant() switch
    {
        "archive" or "archived" => "archive",
        "trash" or "bin" or "delete" or "deleted" or "deleted items" or "rubbish" => "deleteditems",
        "junk" or "spam" or "junk email" or "junk e-mail" => "junkemail",
        "inbox" => "inbox",
        "drafts" or "draft" => "drafts",
        "sent" or "sent items" => "sentitems",
        _ => null,
    };

    private static string Spoken(string destination) => WellKnown(destination) switch
    {
        "deleteditems" => "Deleted Items",
        "junkemail" => "Junk Email",
        "sentitems" => "Sent Items",
        "archive" => "Archive",
        "inbox" => "the Inbox",
        "drafts" => "Drafts",
        _ => destination,
    };

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

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
