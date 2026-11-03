using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Core.Files;
using OutPost2.Core.Jobs;
using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Jobs;
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
public sealed partial class MailTools(
    IHttpContextAccessor http,
    IConnectionStore connections,
    IMailService mail,
    IItemIndex index,
    ISearchContinuations pages,
    OutPost2.Infrastructure.Mail.IAttachmentExtractor extractor,
    IJobService jobs,
    OutPost2.Infrastructure.Calendar.ICalendarService calendar,
    OutPost2.Infrastructure.Files.IDownloadTicketService downloads,
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

        var zones = await ZonesAsync(
            resolution.Connections.Where(c => ordered.Any(x => x.Label == c.Label)), ct);

        var sb = new StringBuilder();
        if (ordered.Count == 0)
        {
            // The same distinction search_mail needs (OP2-239): "no messages" is a claim
            // about the mailbox, and a mailbox that threw was not looked at.
            sb.AppendLine(Empty(
                unreadOnly ? "Nothing unread." : "No messages.",
                results.Length,
                [.. results.Where(r => r.Error is not null).Select(r => (r.Label, r.Error!))]));
        }
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
                sb.Append(Marker(m.Importance));
                sb.AppendLine($" — {When(m.ReceivedAt, label, zones)}");
            }
        }

        // Report degraded mailboxes explicitly: silence reads as "you have no mail there",
        // which is a lie. Skipped when there were no results, because Empty above has
        // already said it as the headline rather than as a footnote.
        if (ordered.Count > 0)
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

            // Who else is on it. Without this a reply cannot be corrected — you cannot name
            // a recipient to take off a thread you were never shown (OP2-187).
            if (m.To.Count > 0) sb.AppendLine($"To: {Untrusted.Scrub(string.Join(", ", m.To))}");
            if (m.Cc.Count > 0) sb.AppendLine($"Cc: {Untrusted.Scrub(string.Join(", ", m.Cc))}");

            sb.AppendLine($"Subject: {Untrusted.Scrub(m.Subject)}");
            // Exact first, because that is the value a deadline is checked against; the
            // phrase is there for reading aloud (OP2-264). Sent is shown too and labelled:
            // it is the sender's clock, the Date header, and can differ from receipt.
            var mz = await ZoneAsync(c, ct);
            var zone = mz.Zone;
            var received = Timestamps.Exact(m.ReceivedAt, zone);

            sb.AppendLine($"Received: {received} "
                        + $"({Timestamps.Ago(m.ReceivedAt, zone)}, {c.Label}{Unknown(mz)})");

            // Compared as RENDERED, not as instants. Graph keeps milliseconds and these
            // print to the second, so a message sent and received 40 ms apart passed an
            // inequality on the instants and printed the same timestamp twice.
            if (m.SentAt is { } sent && Timestamps.Exact(sent, zone) is var sentText
                && sentText != received)
                sb.AppendLine($"Sent: {sentText}");
            sb.AppendLine($"Handle: {item.Ref}");
            if (m.Importance != MailImportance.Normal)
                sb.AppendLine($"Importance: {m.Importance.Wire()}");
            if (m.HasAttachments) sb.AppendLine("Has attachments.");
            sb.AppendLine();
            sb.AppendLine(Untrusted.Fence("message body", HtmlToText.Clamp(m.Body, 8000)));
            return sb.ToString().TrimEnd();
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "search_mail", ReadOnly = true, OpenWorld = true)]
    [Description("Search messages by text. Omit 'connection' for the default mailbox, or pass 'all' to search everywhere. Searches every folder unless 'folder' is given, and says which folder each hit is in. When there are more matches than were returned, the reply gives a page token — pass it back as 'page' to get the next lot. Results carry a handle in square brackets.")]
    public async Task<string> SearchMail(
        [Description("What to search for. Omit when passing 'page'.")] string? query = null,
        [Description("Connection name, or 'all'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("How many results per mailbox, up to 200. The reply says if there are more.")] int count = 10,
        [Description("Restrict to one folder — 'inbox', 'archive', or a folder name. Omit to search the whole mailbox, including filed mail.")] string? folder = null,
        [Description("A page token from a previous search, to carry on where it stopped. The query, folder and count come from that search, so they need not be repeated.")] string? page = null,
        CancellationToken ct = default)
    {
        Continuation? resume = null;

        if (!string.IsNullOrWhiteSpace(page))
        {
            resume = pages.Resolve(UserId, page);

            if (resume is null)
                return pages.Forgot(UserId, page)
                    ? "That page token has already been used, or the search it came from has "
                      + "aged out. Run the search again — if you have been filing as you go, "
                      + "the ones you have dealt with will have dropped out of it."
                    : $"I don't know the page token \"{page.Trim()}\". Search again and I'll "
                      + "give you a fresh one.";

            // Everything about the search comes from the token. Letting a caller re-state
            // the query alongside it invites the two to disagree, and the link Graph gave
            // us carries its own query regardless — so the stated one would be silently
            // ignored rather than honoured.
            (query, folder, count) = (resume.Query, resume.Folder, resume.Count);
        }

        if (string.IsNullOrWhiteSpace(query))
            return "What should I search for?";

        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailRead, connection, isWrite: false);

        if (!resolution.IsActionable) return resolution.SpokenSummary;

        var wanted = Math.Clamp(count, 1, MailService.MaxSearchResults);

        // A continuation only knows about the mailboxes that had more to give, so carrying
        // on deliberately narrows the fan-out rather than re-asking the exhausted ones.
        var searching = resume is null
            ? resolution.Connections
            : resolution.Connections.Where(c => resume.Next.ContainsKey(c.Label)).ToList();

        if (searching.Count == 0)
            return "That page was the end of it — nothing more to fetch.";

        // Each task RETURNS its outcome rather than adding to a shared list. These run
        // concurrently, and List<T>.Add from several of them at once can lose an entry or
        // corrupt the list outright — a bug that only shows up for the users who have
        // more than one mailbox, which is everybody this product is for.
        var perConnection = await Task.WhenAll(searching.Select(async c =>
        {
            try
            {
                // The folder list is fetched for one of two reasons, and skipped when
                // neither applies: to turn a spoken folder name into an id, or to label
                // each hit with where it lives. A search already scoped to one folder
                // needs no labels — the header says it once.
                var listing = folder is null || WellKnown(folder) is null
                    ? mail.ListFoldersAsync(c, ct)
                    : Task.FromResult((IReadOnlyList<MailFolder>)[]);

                // Started, not awaited. Since OP2-213 the walk costs a request per folder
                // that has children — 670 ms on the deepest mailbox here — and when it is
                // only wanted for labels there is nothing to wait for: the search can be
                // in flight at the same time. A scoped search does have to wait, because
                // the name has to become an id before there is a search to send.
                var unscoped = folder is null || resume is not null;
                var searched = unscoped
                    ? mail.SearchAsync(c, query, wanted, null,
                        resume?.Next.GetValueOrDefault(c.Label), ct)
                    : null;

                // Both, together, so that one of them throwing cannot leave the other's
                // exception unobserved — and so an expired token is reported once rather
                // than racing itself.
                if (searched is not null) await Task.WhenAll(listing, searched);

                var folders = await listing;

                string? folderId = null;
                if (folder is not null && resume is null)
                {
                    // Folder names are per-mailbox, so this belongs inside the fan-out:
                    // "Receipts" may exist in one connection and not another, and a scope
                    // that is missing must be reported for THAT mailbox rather than
                    // failing the whole answer.
                    folderId = WellKnown(folder);
                    if (folderId is null)
                    {
                        var scoped = Matches(folders, folder);

                        // "No such folder" would be a lie for a name that matches several,
                        // and scoping a search to an arbitrary one of them reads as a clean
                        // empty answer about the wrong folder.
                        if (scoped.Count != 1)
                            return (c.Label, Items: (IReadOnlyList<MailSummary>)[], More: false,
                                    Next: (string?)null, Folders: folders,
                                    Error: scoped.Count == 0
                                        ? $"has no folder called \"{folder}\""
                                        : $"has {scoped.Count} folders called \"{folder}\" "
                                          + $"({Untrusted.Scrub(string.Join(", ", scoped.Select(f => f.Path)))}) "
                                          + "— say the full path");

                        folderId = scoped[0].Id;
                    }
                }

                var hits = await (searched ?? mail.SearchAsync(
                    c, query, wanted, folderId, resume?.Next.GetValueOrDefault(c.Label), ct));

                return (c.Label, hits.Items, hits.More, Next: hits.Continuation,
                        Folders: folders, Error: (string?)null);
            }
            // The comment that used to stand here said these were "reported below via
            // Degraded/empty". They were not: a mailbox that threw was indistinguishable
            // from one with no matches, so a failed search across three mailboxes read as
            // a confident answer from all three (OP2-145). list_mail already collects and
            // names them; this now does the same.
            catch (MailAuthRequiredException)
            {
                return (c.Label, Items: (IReadOnlyList<MailSummary>)[], More: false,
                        Next: (string?)null, Folders: (IReadOnlyList<MailFolder>)[],
                        Error: "needs re-authorising");
            }
            catch (Exception ex)
            {
                return (c.Label, Items: (IReadOnlyList<MailSummary>)[], More: false,
                        Next: (string?)null, Folders: (IReadOnlyList<MailFolder>)[],
                        Error: ex.Message);
            }
        }));

        // One id-to-name map per mailbox, built once rather than per result.
        var folderNames = perConnection.ToDictionary(
            r => r.Label,
            r => r.Folders.Where(f => f.Id.Length > 0)
                  .GroupBy(f => f.Id, StringComparer.Ordinal)
                  .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        var found = perConnection
            .SelectMany(r => r.Items.Select(i => (r.Label, Item: i)))
            .ToList();

        var failed = perConnection
            .Where(r => r.Error is not null)
            .Select(r => (r.Label, Error: r.Error!))
            .ToList();

        var scope = folder is null ? "" : $" in {Spoken(folder)}";

        if (found.Count == 0)
            return Empty($"Nothing found for \"{query}\"{scope}.", searching.Count, failed);

        var ordered = found.OrderByDescending(x => x.Item.ReceivedAt).ToList();
        var refs = index.Remember(UserId,
            ordered.Select(x => (x.Item.Id, x.Label, x.Item.Subject, x.Item.From)).ToList());

        var zones = await ZonesAsync(
            searching.Where(c => ordered.Any(x => x.Label == c.Label)), ct);

        var sb = new StringBuilder(
            $"{ordered.Count} result{(ordered.Count == 1 ? "" : "s")} for \"{query}\"{scope}:\n");

        for (var n = 0; n < ordered.Count; n++)
        {
            var (label, m) = ordered[n];
            sb.Append($"{n + 1}. [{refs[n].Ref}] ");
            if (resolution.Connections.Count > 1) sb.Append($"[{label}] ");
            sb.Append($"{Untrusted.Scrub(m.From)} — {Untrusted.Scrub(m.Subject)}{Marker(m.Importance)} — {When(m.ReceivedAt, label, zones)}");

            // Where it is, when the search covered more than one folder. Without this,
            // mail that has already been filed is indistinguishable from mail still in the
            // Inbox, so a sorting pass keeps picking up its own finished work (OP2-196).
            if (folder is null && Located(folderNames, label, m.FolderId) is { } where)
                sb.Append($" — in {Untrusted.Scrub(where)}");

            sb.AppendLine();
        }

        // Says when it is showing part of the answer, and — since OP2-208 — hands back
        // something that reaches the rest. Silence here reads as "that is all of them",
        // and a bulk action built on that belief leaves most of the mail in place while
        // reporting success (OP2-189). Saying it without a way forward is barely better:
        // re-running the same search returns the same first page for ever.
        var unfinished = perConnection
            .Where(r => r.More && r.Next is not null)
            .ToDictionary(r => r.Label, r => r.Next!, StringComparer.OrdinalIgnoreCase);

        if (unfinished.Count > 0)
        {
            var token = pages.Remember(UserId, new Continuation
            {
                Query = query, Folder = folder, Count = wanted, Next = unfinished,
            });

            sb.AppendLine($"More to come in {string.Join(" and ", unfinished.Keys)} — "
                        + $"say page {token} for the next {wanted}.");
        }
        else
        {
            // More, but no link to follow it with: the page cap, or a provider that said
            // there was more and offered no way to reach it. Worth distinguishing, because
            // "narrow the search" is the only advice that helps here.
            foreach (var r in perConnection.Where(r => r.More))
                sb.AppendLine($"There are more than {wanted} matches in {r.Label}, and I "
                            + "can't page any further — narrow the search.");
        }

        // A partial answer has to say that it is partial. Silence here reads as "there is
        // nothing else", which is the one thing it is not entitled to claim.
        foreach (var bad in failed)
            sb.AppendLine($"Note: {bad.Label} {bad.Error}.");
        foreach (var d in resolution.Degraded)
            sb.AppendLine($"Note: {d.Label} is unavailable ({d.State}).");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The folder a hit is in, or null when there is nothing worth saying — the provider
    /// did not give one, or it is below the two levels the folder listing reaches.
    /// </summary>
    private static string? Located(
        Dictionary<string, Dictionary<string, string>> byConnection,
        string label, string? folderId)
    {
        if (folderId is null) return null;
        if (!byConnection.TryGetValue(label, out var names)) return null;

        // "somewhere else" rather than silence: the question being asked is "is this still
        // in the Inbox", and for a folder we cannot name the answer is still no.
        return names.TryGetValue(folderId, out var path) ? path : "another folder";
    }

    [McpServerTool(Name = "send_mail", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Send a NEW email. To answer one you have been reading, use reply_mail instead. Body may use Markdown. Always name the connection to send from.")]
    public async Task<string> SendMail(
        [Description("Recipient addresses, comma separated.")] string to,
        [Description("Subject line.")] string subject,
        [Description("Body. Markdown is supported.")] string body,
        [Description("Which connection to send from, e.g. 'work'. Required when more than one could send.")] string? connection = null,
        [Description("CC addresses, comma separated.")] string? cc = null,
        [Description("Importance: 'high' for Outlook's red exclamation mark and High Importance label, 'low', or 'normal' (the default). Only set it when the user asked.")] string? importance = null,
        CancellationToken ct = default)
    {
        if (ImportanceProblem(importance, out var level) is { } bad) return bad;

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
                Importance = level,
            }, ct);

            return $"Sent from {c.Label} to {string.Join(", ", recipients)}{Marked(level, c.Provider)}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "reply_mail", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Reply to a message, keeping the thread and quoting the original. Pass the handle from the listing. Set replyAll to include everyone who was copied. Use addTo or addCc to bring someone else in, and drop to take a wrong address off before it goes — read_mail shows who is on the thread.")]
    public async Task<string> ReplyMail(
        [Description("The handle from the listing, e.g. 'a7kq'.")] string message,
        [Description("What to say. Markdown is supported. Goes above the quoted original.")] string body,
        [Description("Reply to everyone on the thread rather than just the sender.")] bool replyAll = false,
        [Description("Extra addresses to put on the To line, comma separated.")] string? addTo = null,
        [Description("Extra addresses to copy in, comma separated.")] string? addCc = null,
        [Description("Addresses or names already on the thread to take off, comma separated. Must match exactly what read_mail showed; nothing is sent if one matches nobody.")] string? drop = null,
        [Description("Importance: 'high' for Outlook's red exclamation mark and High Importance label, 'low', or 'normal' (the default). Only set it when the user asked.")] string? importance = null,
        CancellationToken ct = default)
    {
        if (ImportanceProblem(importance, out var level) is { } bad) return bad;

        // The thread decides which mailbox and which identity, so there is nothing for the
        // resolver to ask about and the capabilities are the only gate. Both of them:
        // sending is obvious, filing less so — Graph composes a threaded reply as a draft
        // in the mailbox before it goes anywhere.
        var found = await ResolveAsync(message, ct, Capabilities.MailSend, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        if (string.IsNullOrWhiteSpace(body)) return "I need something to say in the reply.";

        var edit = new RecipientEdit
        {
            AddTo = Split(addTo), AddCc = Split(addCc), Drop = Split(drop),
        };

        try
        {
            var sent = await mail.SendReplyAsync(
                c, item.ItemId, replyAll ? ReplyKind.All : ReplyKind.Sender, body, null, edit, level, ct);

            // Read back who it ACTUALLY went to rather than what was asked for. When
            // recipients were edited that is the only confirmation the user gets that the
            // wrong address is off and the right one is on.
            if (!edit.Any)
                return replyAll
                    ? $"Replied to everyone on \"{sent.Subject}\" from {c.Label}{Marked(level, c.Provider)}."
                    : $"Replied to {item.From} on \"{sent.Subject}\" from {c.Label}{Marked(level, c.Provider)}.";

            return $"Replied on \"{sent.Subject}\" from {c.Label}. It went to {Who(sent.To)}"
                 + (sent.Cc.Count > 0 ? $", copying {Who(sent.Cc)}" : "")
                 + (sent.Dropped.Count > 0 ? $". Took {Who(sent.Dropped)} off" : "")
                 + Marked(level, c.Provider) + ".";
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
        [Description("Importance: 'high' for Outlook's red exclamation mark and High Importance label, 'low', or 'normal' (the default). Only set it when the user asked.")] string? importance = null,
        CancellationToken ct = default)
    {
        if (ImportanceProblem(importance, out var level) is { } bad) return bad;

        // Both: sending is obvious, filing is not — Graph composes a threaded reply as a
        // draft in the mailbox before sending it.
        var found = await ResolveAsync(message, ct, Capabilities.MailSend, Capabilities.MailWrite);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        var recipients = Split(to);
        if (recipients.Count == 0) return "I need someone to forward it to.";

        try
        {
            var sent = await mail.SendReplyAsync(
                c, item.ItemId, ReplyKind.Forward, body ?? "", recipients, null, level, ct);

            return $"Forwarded \"{sent.Subject}\" to {Who(sent.To)} from {c.Label}{Marked(level, c.Provider)}.";
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

                // Deliberately not created on the fly. A misheard destination would
                // otherwise leave a folder named after the mistake and file the message
                // into it, which is the same failure categorise_mail's colour gate avoids.
                var (match, problem) = Pick(folders, destination, c.Label);
                if (problem is not null) return problem;

                target = match!.Id;
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

    // ── folders (OP2-188) ────────────────────────────────────────────────────

    [McpServerTool(Name = "list_folders", ReadOnly = true, OpenWorld = true)]
    [Description("List the mail folders on a mailbox — the places a message can be filed. Shows how many messages are in each. Deep branches are collapsed; pass 'under' with a folder name to open one up.")]
    public async Task<string> ListFolders(
        [Description("Connection name such as 'work'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("Show what is inside this folder instead of the whole mailbox. A name, a path, or 'inbox'.")] string? under = null,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailRead, connection, isWrite: false);

        if (resolution.Kind != ResolutionKind.Single) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        try
        {
            var all = await mail.ListFoldersAsync(c, ct);
            if (all.Count == 0) return $"{c.Label} has no folders I can see.";

            MailFolder? root = null;
            if (!string.IsNullOrWhiteSpace(under))
            {
                var wk = WellKnown(under);
                var (match, problem) = wk is null
                    ? Pick(all, under, c.Label)
                    : (all.FirstOrDefault(f => Same(f.WellKnownName, wk)), null);

                if (problem is not null) return problem;
                if (match is null) return NoFolder(c.Label, under, all);
                root = match;
            }

            return Listing(all, root, c.Label);
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    /// <summary>
    /// The folder listing as it is read back.
    /// <para>
    /// Before OP2-213 this printed every folder it had, which was bearable because it only
    /// ever had two levels. The walk now returns all of them — 112 on the mailbox that
    /// reported the ticket — and 112 lines read out to somebody driving is not an answer. So
    /// the default shows one level under the root and says what each collapsed branch holds,
    /// which is both shorter and more useful: the branch count is the thing that tells you
    /// whether to look inside.
    /// </para>
    /// </summary>
    public static string Listing(IReadOnlyList<MailFolder> all, MailFolder? root, string label)
    {
        var prefix = root is null ? "" : root.Path + "/";

        bool Immediate(MailFolder f) =>
            f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !f.Path.AsSpan(prefix.Length).Contains('/')
            && f.Path.Length > prefix.Length;

        var shown = all.Where(Immediate).ToList();

        if (shown.Count == 0)
            return root is null
                ? $"{label} has no folders I can see."
                : $"{Untrusted.Scrub(root.Path)} has nothing inside it. "
                  + $"It holds {root.MessageCount} message{(root.MessageCount == 1 ? "" : "s")}.";

        // Outlook's own folders last and named as such: it is the difference between
        // "you have 14 folders" and "you have 6 you made".
        var ordered = shown.Where(f => !f.IsWellKnown).Concat(shown.Where(f => f.IsWellKnown));

        var sb = new StringBuilder();

        sb.AppendLine(root is null
            ? $"{all.Count} folders on {label}"
              + (shown.Count == all.Count ? ":" : $", {shown.Count} of them at the top:")
            : $"Inside {Untrusted.Scrub(root.Path)} on {label}:");

        foreach (var f in ordered)
        {
            // Relative to the root: "Receipts" under Inbox, not "Inbox/Receipts" repeated
            // down the whole list.
            var name = f.Path.Length > prefix.Length ? f.Path[prefix.Length..] : f.Path;

            // How many are really under it, not just immediately under it — the number that
            // answers "is it worth looking in there".
            var below = all.Count(x =>
                x.Path.StartsWith(f.Path + "/", StringComparison.OrdinalIgnoreCase));

            sb.AppendLine($"  {Untrusted.Scrub(name)} — {f.MessageCount} message"
                        + $"{(f.MessageCount == 1 ? "" : "s")}"
                        + (below > 0
                            ? $", {below} folder{(below == 1 ? "" : "s")} inside"
                            : f.ChildCount > 0 ? $", {f.ChildCount} inside I couldn't reach" : "")
                        + (f.IsWellKnown ? " (Outlook's own)" : ""));
        }

        var collapsed = ordered.Count(f =>
            all.Any(x => x.Path.StartsWith(f.Path + "/", StringComparison.OrdinalIgnoreCase)));

        if (collapsed > 0)
            sb.AppendLine("(Say \"what's in\" one of those to see inside — "
                        + $"{collapsed} {(collapsed == 1 ? "has" : "have")} folders of their own. "
                        + "Filing works on any of them whether or not they're listed here.)");

        if (Deeper(all) is > 0 and var n)
            sb.AppendLine($"({n} folder{(n == 1 ? "" : "s")} here have subfolders I ran out of "
                        + "requests to fetch, so a few deep ones may be missing.)");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "create_folder", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Make a new mail folder to file messages into, optionally inside another one. Then move_mail can use its name as a destination.")]
    public async Task<string> CreateFolder(
        [Description("What to call it.")] string name,
        [Description("Connection name such as 'work'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("Put it inside this folder — a name, or 'inbox'. Omit for the top level.")] string? parent = null,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailWrite, connection, isWrite: true);

        if (resolution.Kind != ResolutionKind.Single) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        if (Invalid(name) is { } why) return why;

        try
        {
            var folders = await mail.ListFoldersAsync(c, ct);

            string? parentId = null;
            string? parentPath = null;
            var under = "";

            if (!string.IsNullOrWhiteSpace(parent))
            {
                parentId = WellKnown(parent);
                if (parentId is null)
                {
                    var (match, problem) = Pick(folders, parent, c.Label);
                    if (problem is not null) return problem;
                    (parentId, parentPath, under) = (match!.Id, match.Path, $" inside {match.Path}");
                }
                else
                {
                    parentPath = folders.FirstOrDefault(f => Same(f.WellKnownName, parentId))?.Path;
                    under = $" inside {Spoken(parent)}";
                }
            }

            // Graph would answer with its own ErrorFolderExists, which reads as a failure
            // rather than as "that already exists and here it is". For a cleanup the user is
            // talking through, the second is the useful answer.
            var wanted = parentPath is null ? name.Trim() : $"{parentPath}/{name.Trim()}";
            var clash = folders.FirstOrDefault(f => Same(f.Path, wanted));

            if (clash is not null)
                return $"{c.Label} already has a folder called {Untrusted.Scrub(clash.Path)} "
                     + $"({clash.MessageCount} message{(clash.MessageCount == 1 ? "" : "s")} in it). "
                     + "You can file into it as it is.";

            // A folder of that name somewhere else in the tree is not a reason to refuse.
            // Only two levels were visible before OP2-213, so this almost never came up.
            // Now a dormant archive branch of 84 folders would otherwise veto half the
            // names in the mailbox. It does have to be said, because the new folder makes
            // that name ambiguous and filing by it will start asking for a path.
            var elsewhere = folders.Where(f => Same(f.Name, name)).Select(f => f.Path).ToList();

            var made = await mail.CreateFolderAsync(c, name.Trim(), parentId, ct);

            return $"Made a folder called {Untrusted.Scrub(made.Name)}{under} in {c.Label}. "
                 + (elsewhere.Count > 0
                     ? $"There {(elsewhere.Count == 1 ? "was already one" : $"were already {elsewhere.Count}")} "
                       + $"of that name — {Untrusted.Scrub(string.Join(", ", elsewhere.Take(4)))} — so "
                       + "filing by the name alone will ask you which one from now on."
                     : $"Say \"file it under {made.Name}\" to move things there.");
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "rename_folder", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Rename one of your own mail folders. Outlook's own folders (Inbox, Archive, Sent Items and so on) cannot be renamed.")]
    public async Task<string> RenameFolder(
        [Description("The folder to rename, by name.")] string folder,
        [Description("What to call it instead.")] string name,
        [Description("Connection name such as 'work'. Omit only if a default mailbox has been set.")] string? connection = null,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailWrite, connection, isWrite: true);

        if (resolution.Kind != ResolutionKind.Single) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        if (Invalid(name) is { } why) return why;

        try
        {
            var folders = await mail.ListFoldersAsync(c, ct);
            var (match, problem) = Pick(folders, folder, c.Label);
            if (problem is not null) return problem;

            // Graph lets some of these through, and a mailbox whose Inbox is called
            // something else is one the user has to repair in Outlook by hand.
            if (match!.IsWellKnown)
                return $"{match.Path} is one of Outlook's own folders, so I can't rename it. "
                     + "Only folders you made yourself.";

            var renamed = await mail.RenameFolderAsync(c, match.Id, name.Trim(), ct);

            return $"Renamed {Untrusted.Scrub(match.Path)} to {Untrusted.Scrub(renamed.Name)} in {c.Label}. "
                 + "Everything in it stayed where it was.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "delete_folder", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Delete one of your own mail folders. Anything inside goes to Deleted Items with it. A folder that is not empty is refused unless confirm is true, so the count can be said out loud first.")]
    public async Task<string> DeleteFolder(
        [Description("The folder to delete, by name.")] string folder,
        [Description("Connection name such as 'work'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("True to go ahead when the folder has messages or subfolders in it. Only set this after the user has heard what is in it.")] bool confirm = false,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailWrite, connection, isWrite: true);

        if (resolution.Kind != ResolutionKind.Single) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        try
        {
            var folders = await mail.ListFoldersAsync(c, ct);
            var (match, problem) = Pick(folders, folder, c.Label);
            if (problem is not null) return problem;

            if (Refuse(match!, confirm) is { } stop) return stop;

            await mail.DeleteFolderAsync(c, match!.Id, ct);

            return match.MessageCount > 0
                ? $"Deleted {Untrusted.Scrub(match.Path)} from {c.Label}, with its "
                  + $"{match.MessageCount} message{(match.MessageCount == 1 ? "" : "s")}. "
                  + "It is in Deleted Items if that was wrong."
                : $"Deleted the empty folder {Untrusted.Scrub(match.Path)} from {c.Label}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    // ── clearing out a sender (OP2-224) ──────────────────────────────────────

    /// <summary>
    /// How many messages <c>clear_mail</c> will move while the caller waits.
    /// <para>
    /// Twenty per batch and a listing between each, so this is about a dozen requests —
    /// comfortably inside a tool call. Above it the work goes to the queue, because the
    /// alternative was measured: 430 messages timed out the client while the server kept
    /// deleting, and nobody could say whether it had finished (OP2-231).
    /// </para>
    /// </summary>
    public const int InlineClearLimit = 150;

    [McpServerTool(Name = "clear_mail", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Move every message from a sender to Deleted Items in one go — the whole lot, not one at a time. Call it once WITHOUT confirm to hear the count and who they are from, then again with confirm true. By default it only clears the Inbox and Clutter; pass folder 'all' to include folders you have filed mail into.")]
    public async Task<string> ClearMail(
        [Description("Who to clear out: an email address, or part of one such as 'linkedin'.")] string from,
        [Description("Connection name such as 'work'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("Where to clear. Omit for the Inbox and Clutter only. 'all' for the whole mailbox including filed mail, or a folder name for just that one.")] string? folder = null,
        [Description("Only messages older than this: a date like '2025-01-01', or '6 months ago'.")] string? before = null,
        [Description("True to actually move them. Only set this after the user has heard the count and said to go ahead.")] bool confirm = false,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailWrite, connection, isWrite: true);

        if (resolution.Kind != ResolutionKind.Single) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        if (string.IsNullOrWhiteSpace(from) || from.Trim().Length < 3)
            return "Tell me who to clear out — an address, or enough of one to be sure. "
                 + "Anything shorter would match half the mailbox.";

        DateTimeOffset? cutoff = null;
        if (!string.IsNullOrWhiteSpace(before))
        {
            if (Older(before, DateTimeOffset.UtcNow) is not { } parsed)
                return $"I couldn't read \"{before}\" as a date. Try \"2025-01-01\", "
                     + "or something like \"6 months ago\".";
            cutoff = parsed;
        }

        try
        {
            var folders = await mail.ListFoldersAsync(c, ct);

            var (scope, where, problem) = Scope(folders, folder, c.Label);
            if (problem is not null) return problem;

            var target = new ClearTarget { From = from.Trim(), FolderIds = scope, Before = cutoff };
            var when = cutoff is null ? "" : $", received before {cutoff:d MMMM yyyy}";

            if (!confirm)
            {
                var preview = await mail.PreviewClearAsync(c, target, ct);

                if (preview.Matched == 0 && preview.Everywhere == 0)
                    return $"Nothing from \"{Untrusted.Scrub(from)}\"{where}{when} in {c.Label}. "
                         + "Nothing to delete.";

                return Proposal(from, c.Label, where, when, preview, folders);
            }

            // How many there are decides whether this can be answered in the call at all.
            var expected = await mail.PreviewClearAsync(c, target, ct);

            if (expected.Matched == 0)
                return $"Nothing from \"{Untrusted.Scrub(from)}\"{where}{when} in {c.Label} any more — "
                     + "it looks like it has already gone.";

            // A small clear-out is done here and now, because a job the user has to ask
            // about is worse than a two-second wait.
            if (expected.Matched <= InlineClearLimit)
            {
                var done = await mail.ClearAsync(c, target, ct: ct);

                var said = $"Moved {done.Deleted} message{(done.Deleted == 1 ? "" : "s")} from "
                         + $"\"{Untrusted.Scrub(from)}\"{where} to Deleted Items in {c.Label}. "
                         + "They stay there until you empty it, so this is undoable.";

                // Said before the other notes, because it changes what the listener should
                // do next: everything else invites another go, this asks them to wait.
                if (done.Throttled) said += " " + Slow(done.RetryAfter);

                // Said plainly rather than rounded off. "Moved 400" when 12 are still
                // sitting there is the failure this tool exists to replace.
                if (done.Failed > 0)
                    said += $" {done.Failed} wouldn't move — that usually means they had already "
                          + "shifted. Run it again and I'll pick up anything still there.";

                if (done.Remaining > 0)
                    said += $" There {(done.Remaining == 1 ? "is" : "are")} still {done.Remaining} "
                          + "to go. Say go ahead again for the next lot.";

                return said;
            }

            // Anything bigger goes to the queue (OP2-231). Four hundred and thirty messages
            // did not fit in a tool call: the client gave up with a generic failure while
            // the server carried on deleting, and every retry then hit the concurrency
            // limit the first attempt was still using. The key is derived from the request
            // so that exact retry joins the running job instead of starting a second one.
            var key = Fingerprint(c.Id, from.Trim(), scope, cutoff);

            var (job, existed) = await jobs.EnqueueAsync(
                UserId, JobKind.ClearMail,
                $"clear {expected.Matched} from \"{Untrusted.Scrub(from)}\"{where}",
                new ClearMailPayload(c.Id, from.Trim(), scope?.ToList(), cutoff, expected.Matched),
                key, c.Label, requiresApproval: false, ct);

            if (existed && !job.IsTerminal)
                return "That one is already running — I didn't start a second. " + job.Spoken()
                     + " Ask me to check the queue for how it's getting on.";

            if (existed) return job.Spoken();

            return $"Started on {expected.Matched} message{(expected.Matched == 1 ? "" : "s")} from "
                 + $"\"{Untrusted.Scrub(from)}\"{where} in {c.Label}. That's too many to do while "
                 + "you wait, so it runs in the background — ask me to check the queue and I'll "
                 + "tell you how far it has got. Everything goes to Deleted Items.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "recover_mail", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Bring a sender's mail back out of Recoverable Items into the Inbox. This is where Outlook keeps messages that were deleted without going through Deleted Items — use it if mail has gone missing after a bulk delete. Call without confirm first to hear how many are there.")]
    public async Task<string> RecoverMail(
        [Description("Whose mail to bring back: an email address, or part of one.")] string from,
        [Description("Connection name such as 'work'. Omit only if a default mailbox has been set.")] string? connection = null,
        [Description("True to actually move them back into the Inbox.")] bool confirm = false,
        CancellationToken ct = default)
    {
        var resolution = ConnectionResolver.Resolve(
            await connections.ListAsync(UserId, ct), Capabilities.MailWrite, connection, isWrite: true);

        if (resolution.Kind != ResolutionKind.Single) return resolution.SpokenSummary;
        var c = resolution.Connection!;

        if (string.IsNullOrWhiteSpace(from) || from.Trim().Length < 3)
            return "Tell me whose mail to bring back — an address, or enough of one to be sure.";

        try
        {
            if (!confirm)
            {
                var waiting = await mail.RecoverableCountAsync(c, from.Trim(), ct);

                return waiting == 0
                    ? $"Nothing from \"{Untrusted.Scrub(from)}\" is in Recoverable Items on {c.Label}. "
                      + "If it went missing, it may have been there longer than the retention window."
                    : $"{waiting} message{(waiting == 1 ? "" : "s")} from \"{Untrusted.Scrub(from)}\" "
                      + $"{(waiting == 1 ? "is" : "are")} in Recoverable Items on {c.Label}. "
                      + "Say go ahead and I'll put them back in the Inbox.";
            }

            var done = await mail.RecoverAsync(c, from.Trim(), ct: ct);

            if (done.Deleted == 0 && done.Failed == 0)
                return $"Nothing from \"{Untrusted.Scrub(from)}\" left in Recoverable Items on {c.Label}.";

            var said = $"Put {done.Deleted} message{(done.Deleted == 1 ? "" : "s")} from "
                     + $"\"{Untrusted.Scrub(from)}\" back in the Inbox on {c.Label}.";

            if (done.Throttled) said += " " + Slow(done.RetryAfter);

            if (done.Failed > 0)
                said += $" {done.Failed} wouldn't move — those may be past the retention window.";

            if (done.Remaining > 0)
                said += $" {done.Remaining} still to go — say go ahead again for the next lot.";

            return said;
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (MailException ex) { return ex.Message; }
    }

    /// <summary>
    /// Which folders a clear-out covers, and how to say it.
    /// <para>
    /// The default is the Inbox and Clutter rather than the whole mailbox, and that is
    /// OP2-227: clearing everything also emptied a folder the user had just filed that
    /// sender's receipts into. Filing something is an act of keeping it, so the places mail
    /// is <em>put</em> are now out of scope unless asked for by name. "all" is the way to
    /// ask for everything, said out loud as part of the proposal.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<string>? Scope, string Where, string? Problem) Scope(
        IReadOnlyList<MailFolder> folders, string? said, string label)
    {
        if (!string.IsNullOrWhiteSpace(said)
            && said.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return (null, " everywhere in the mailbox, including folders you've filed into", null);

        if (!string.IsNullOrWhiteSpace(said))
        {
            var wk = WellKnown(said);
            if (wk is not null)
            {
                var one = folders.FirstOrDefault(f => Same(f.WellKnownName, wk));
                return one is null
                    ? (null, "", NoFolder(label, said, folders))
                    : ((IReadOnlyList<string>)[one.Id], $" in {Spoken(said)}", null);
            }

            var (match, problem) = Pick(folders, said, label);
            if (problem is not null) return (null, "", problem);
            return ([match!.Id], $" in {match.Path}", null);
        }

        // Clutter is included because it is where mail lands rather than where it is put —
        // Outlook files there on the user's behalf, not on their instruction.
        var landing = folders
            .Where(f => Same(f.WellKnownName, "inbox") || Same(f.Name, "Clutter"))
            .Select(f => f.Id)
            .ToList();

        return landing.Count == 0
            ? (null, " in the whole mailbox", null)
            : (landing, " in the Inbox and Clutter", null);
    }

    /// <summary>
    /// What a clear-out would do, written to be read out loud before it happens.
    /// <para>
    /// Two things have to survive being said in a car. The <b>senders</b>: "everything from
    /// Dave" matching three different Daves has to be heard before several hundred messages
    /// move, and a bare count cannot say that. And the <b>folders being skipped</b>: OP2-227
    /// was filed because a clear-out silently took mail out of a folder the user had just
    /// filed it into, and the dry run never mentioned that any match was outside the Inbox.
    /// </para>
    /// </summary>
    public static string Proposal(
        string said, string label, string where, string when, ClearPreview preview,
        IReadOnlyList<MailFolder> folders)
    {
        var sb = new StringBuilder();

        if (preview.Matched == 0)
        {
            sb.Append($"Nothing from \"{Untrusted.Scrub(said)}\"{where}{when} in {label}");
        }
        else
        {
            sb.Append($"{preview.Matched} message{(preview.Matched == 1 ? "" : "s")} match "
                    + $"\"{Untrusted.Scrub(said)}\"{where}{when} in {label}");

            if (preview.Senders.Count == 1)
            {
                sb.Append($", all from {Untrusted.Scrub(preview.Senders[0].Display)} "
                        + $"<{Untrusted.Scrub(preview.Senders[0].Address)}>");
            }
            else if (preview.Senders.Count > 1)
            {
                sb.Append($", from {preview.Senders.Count} different senders: ");
                sb.Append(Untrusted.Scrub(string.Join(", ", preview.Senders
                    .Take(5)
                    .Select(s => $"{s.Address} ({s.Count}"
                               + (preview.Sampled ? " of the ones I looked at)" : ")")))));

                if (preview.Senders.Count > 5) sb.Append(", and more");
            }
        }

        sb.Append('.');

        if (preview.Sampled && preview.Senders.Count > 1)
            sb.Append(" That's from a sample, so there may be senders I haven't named.");

        // The whole point of OP2-227. A number said here is mail that would otherwise go
        // without anybody mentioning it.
        var filed = preview.Everywhere - preview.Matched;
        if (filed > 0)
        {
            sb.Append($" There {(filed == 1 ? "is" : "are")} another {filed} from them "
                    + "in folders you've filed into");

            // Deleted Items and Junk are left out: the service does not count them, so
            // naming them here would describe a number that does not include them.
            var worth = preview.SkippedFolderIds
                .Select(id => folders.FirstOrDefault(f => f.Id == id))
                .Where(f => f is not null && !Disposal(f.WellKnownName))
                .Select(f => f!.Path)
                .ToList();

            var named = worth.Take(4).ToList();

            if (named.Count > 0)
                sb.Append($" — {Untrusted.Scrub(string.Join(", ", named))}"
                        // Against the folders worth naming, not every id skipped. Counting
                        // the bins here produced ", and others" whose others were precisely
                        // the ones deliberately left out — a second count describing things
                        // it had excluded.
                        + (worth.Count > named.Count ? ", and others" : ""));

            sb.Append(". I'm leaving those alone. Say \"everywhere\" if you want them too.");
        }

        if (preview.Matched > 0)
            sb.Append(" Nothing has been moved. Say go ahead and they all go to Deleted Items, "
                    + "where they stay until you empty it.");

        return sb.ToString();
    }

    /// <summary>
    /// What to say when a fan-out came back with nothing (OP2-239).
    /// <para>
    /// "Nothing found" is a claim about the mailbox, and it is only ours to make if we
    /// actually looked. OP2-239 was filed because a database error demoted to a footnote
    /// left the headline reading as a confident empty answer — and an empty answer is acted
    /// on, while an error is retried. Those are opposite outcomes, so the distinction
    /// belongs in the first sentence rather than a note after it.
    /// </para>
    /// </summary>
    /// <param name="nothing">What to say when the search genuinely found nothing.</param>
    /// <param name="asked">How many mailboxes were tried.</param>
    public static string Empty(
        string nothing, int asked, IReadOnlyList<(string Label, string Error)> failed)
    {
        if (failed.Count == 0) return nothing;

        var trouble = string.Join("; ", failed.Select(f => $"{f.Label} {f.Error}"));

        // Every mailbox failed, so there is no result at all — not an empty one. Said
        // plainly, because this is the case that was being reported as "nothing".
        if (failed.Count >= asked)
            return $"I couldn't look: {Untrusted.Scrub(trouble)}. "
                 + "That was every mailbox, so I don't know whether there's anything there "
                 + "— this isn't an empty result, it's a failed one.";

        // Some worked. The empty answer stands for those, and has to be qualified.
        return $"{nothing} That's only the {asked - failed.Count} of {asked} mailboxes I could "
             + $"reach — {Untrusted.Scrub(trouble)}, so there may be more there.";
    }

    /// <summary>
    /// Whether a folder is somewhere mail has already been thrown away rather than filed.
    /// </summary>
    private static bool Disposal(string? wellKnownName) =>
        Same(wellKnownName, "deleteditems") || Same(wellKnownName, "junkemail")
        || Same(wellKnownName, "recoverableitemsdeletions");

    /// <summary>
    /// A stable name for one clear-out request, so a retried tool call joins the job that is
    /// already running rather than starting a second one against the same mailbox (OP2-231).
    /// </summary>
    public static string Fingerprint(
        Guid connectionId, string from, IReadOnlyList<string>? scope, DateTimeOffset? before)
    {
        var material = $"clear|{connectionId}|{from.ToLowerInvariant()}|"
                     + $"{(scope is null ? "all" : string.Join(",", scope.Order(StringComparer.Ordinal)))}|"
                     + $"{before?.UtcDateTime:O}";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    /// <summary>
    /// What to say when Microsoft started refusing requests part-way through (OP2-238).
    /// <para>
    /// Written for the agent reading it as much as the person hearing it. A throttle is the
    /// one outcome where the correct next move is to do nothing for a bit, and an agent told
    /// only "that didn't finish" will try again immediately — which is how a thirty-second
    /// limit becomes a five-minute one. So: a number of seconds, and why.
    /// </para>
    /// </summary>
    public static string Slow(TimeSpan? retryAfter)
    {
        var seconds = Math.Max(5, (int)Math.Ceiling((retryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds));

        return "Microsoft then started rate-limiting the mailbox, so I stopped there rather "
             + "than push. Nothing was lost — what I've said is done is done. Wait about "
             + $"{seconds} seconds before asking for the rest, and don't retry immediately: "
             + "that extends the limit instead of clearing it.";
    }

    /// <summary>
    /// A cut-off date from what somebody said: an ISO date, or "6 months ago" / "older than
    /// two years". Null when it cannot be read — a date this tool guesses at is a date that
    /// deletes the wrong mail.
    /// </summary>
    public static DateTimeOffset? Older(string? said, DateTimeOffset now)
    {
        var text = (said ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return null;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact)
            && text.Any(char.IsAsciiDigit) && text.Contains('-'))
            return exact;

        // "older than 6 months", "6 months ago", "a year ago".
        var m = AgoPattern().Match(text);
        if (!m.Success) return null;

        var n = m.Groups["n"].Value is { Length: > 0 } digits
            ? int.Parse(digits, CultureInfo.InvariantCulture)
            : Word(m.Groups["word"].Value);

        if (n <= 0) return null;

        return m.Groups["unit"].Value switch
        {
            "day" or "days" => now.AddDays(-n),
            "week" or "weeks" => now.AddDays(-7 * n),
            "month" or "months" => now.AddMonths(-n),
            "year" or "years" => now.AddYears(-n),
            _ => null,
        };

        static int Word(string w) => w switch
        {
            "a" or "an" or "one" => 1,
            "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6,
            "nine" => 9, "ten" => 10, "twelve" => 12, "eighteen" => 18,
            _ => 0,
        };
    }

    [GeneratedRegex(
        @"(?:older\s+than\s+|more\s+than\s+|before\s+)?(?:(?<n>\d+)|(?<word>[a-z]+))\s+(?<unit>day|days|week|weeks|month|months|year|years)(?:\s+ago)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex AgoPattern();

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
    [Description("Read the text content of an attachment — PDF, Word, Excel, PowerPoint or plain text. Pass the message handle and the attachment number. To keep the original file itself rather than its text, use save_attachment.")]
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

    [McpServerTool(Name = "save_attachment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Save the ORIGINAL file of an attachment — byte for byte, any type — to disk on the machine you are running on. Use this rather than read_attachment when the file itself is wanted, not its text. This server cannot write to your filesystem, so it returns a one-use download link per file and the exact command to fetch it into 'directory'; you run the command. Pass the message handle and the attachment number, or all=true for every attachment on the message.")]
    public async Task<string> SaveAttachment(
        [Description("The message handle from the listing, or the number shown beside it.")] string message,
        [Description("Which attachment, from list_attachments. Defaults to the first. Ignored when all is true.")] int attachmentNumber = 1,
        [Description("Every attachment on the message in one call.")] bool all = false,
        [Description("The directory on YOUR machine to save into, e.g. 'C:/archive/2026/acme' or '/home/me/records'. Must be a full path — '~' is refused because it does not expand on Windows. Created if missing. Omit to save into the current directory.")] string? directory = null,
        CancellationToken ct = default)
    {
        var problem = DirectoryProblem(directory);
        if (problem is not null) return problem;

        var found = await ResolveAsync(message, ct, Capabilities.MailRead);
        if (found.Problem is not null) return found.Problem;
        var (item, c) = (found.Item!, found.Connection!);

        try
        {
            var items = await mail.ListAttachmentsAsync(c, item.ItemId, ct);
            if (items.Count == 0) return "That message has no attachments.";

            List<int> chosen;
            if (all) chosen = Enumerable.Range(0, items.Count).ToList();
            else
            {
                var i = attachmentNumber - 1;
                if (i < 0 || i >= items.Count)
                    return $"There {(items.Count == 1 ? "is only 1 attachment" : $"are only {items.Count} attachments")}.";
                chosen = [i];
            }

            // Names made safe and distinct across the WHOLE message, not just the chosen
            // ones, so attachment 3 saved today and attachment 5 saved tomorrow cannot land
            // on the same path when both were called image001.png.
            var safeNames = SafeFileName.Distinct(items.Select(a => a.Name));

            var skipped = new List<string>();
            var wanted = new List<(int N, MailAttachment A, string Safe)>();
            foreach (var i in chosen)
            {
                var a = items[i];
                if (a.IsLink) skipped.Add($"{i + 1}. {Untrusted.Scrub(a.Name)} — a cloud link, so there is no file behind it to save.");
                else if (a.IsEmbeddedItem) skipped.Add($"{i + 1}. {Untrusted.Scrub(a.Name)} — an embedded message rather than a file.");
                else wanted.Add((i + 1, a, safeNames[i]));
            }

            if (wanted.Count == 0)
                return "Nothing there can be saved as a file:\n" + string.Join("\n", skipped);

            var minted = await downloads.MintAsync(UserId, c.Id, item.ItemId,
                wanted.Select(w => new OutPost2.Infrastructure.Files.DownloadRequest(w.A.Id, w.Safe, w.A.SizeBytes)).ToList(),
                ct);

            return SaveInstructions(item.Subject, directory,
                wanted.Select((w, k) => (w.N, w.A.Name, w.Safe, w.A.HumanSize,
                    Url: $"{urls.Base}/files/attachment/{minted[k].Token}")).ToList(),
                skipped);
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (TooManyTicketsException ex) { return ex.Message; }
    }

    /// <summary>
    /// Refuses a directory that could not be put safely inside single quotes. The directory
    /// comes from the caller, not a sender, but it still ends up in a command that runs, and
    /// a quote in it would end the quoting.
    /// </summary>
    public static string? DirectoryProblem(string? directory)
    {
        if (directory is null) return null;
        if (directory.Trim().Length == 0) return "Give a directory, or leave it out to save into the current one.";
        if (directory.Length > 400) return "That directory path is too long.";
        var trimmed = directory.Trim();

        // Refused rather than guessed at: whether "~" means anything depends on the shell
        // the caller runs the command in, and this server cannot know which that is.
        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal)
            || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            return "I can't use \"~\" — whether it means your home directory depends on the "
                 + "shell, and on Windows it would make a folder actually called \"~\". "
                 + "Give me the full path instead, or leave the directory out to save into "
                 + "the current one.";

        if (directory.Any(ch => ch == '\'' || ch == '`' || char.IsControl(ch)))
            return "That directory has a quote or a control character in it, which I can't put "
                 + "into a command safely. Use a path without one.";
        return null;
    }

    /// <summary>
    /// Where the file goes, joined with a forward slash: curl accepts that on Windows as well
    /// as everywhere else. A leading <c>~</c> is left for the shell to expand, so it is
    /// written outside the quotes.
    /// </summary>
    public static string Target(string? directory, string safeName)
    {
        if (directory is null) return Quote(safeName);

        var dir = directory.Trim().TrimEnd('/', '\\');

        // No tilde. It was emitted outside the quotes so a POSIX shell would expand it,
        // but this same message tells Windows callers to use curl.exe — and neither
        // PowerShell, cmd nor curl.exe expands it in a native argument. With --create-dirs
        // that silently made a directory literally named "~" in the shell's working
        // directory and reported success. Refused in DirectoryProblem instead, where it
        // can be explained; by here it cannot occur.
        return Quote((dir.Length == 0 ? "/" : dir + "/") + safeName);
    }

    /// <summary>
    /// Single quotes, which are literal in sh, bash and PowerShell. Safe because neither a
    /// <see cref="SafeFileName"/> nor a directory that passed <see cref="DirectoryProblem"/>
    /// can contain one.
    /// </summary>
    private static string Quote(string s) => $"'{s}'";

    public static string SaveInstructions(
        string subject, string? directory,
        IReadOnlyList<(int Number, string Original, string Safe, string Size, string Url)> files,
        IReadOnlyList<string> skipped)
    {
        var sb = new StringBuilder();
        sb.AppendLine(files.Count == 1
            ? $"Ready to save 1 attachment from \"{Untrusted.Scrub(subject)}\". Run:"
            : $"Ready to save {files.Count} attachments from \"{Untrusted.Scrub(subject)}\". Run each:");

        foreach (var f in files)
        {
            sb.Append($"{f.Number}. {f.Safe} ({f.Size})");
            if (f.Safe != f.Original) sb.Append($" — renamed from {Untrusted.Scrub(f.Original)} to be safe on disk");
            sb.AppendLine();
            sb.AppendLine($"   curl -fsS --create-dirs -o {Target(directory, f.Safe)} '{f.Url}'");
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine("Not saved:");
            foreach (var s in skipped) sb.AppendLine(s);
        }

        sb.AppendLine();
        sb.AppendLine($"Each link works once and expires in {(int)DownloadTicket.Lifetime.TotalMinutes} minutes. "
            + "The file is the original bytes; the response's X-Content-SHA256 header is its "
            + "SHA-256 if you need to prove the copy is exact.");
        sb.Append("If a command exits non-zero, that file was NOT saved — do not say it was. "
            + "Ask for a new link and run it again. In Windows PowerShell, call curl.exe rather than curl.");
        return sb.ToString();
    }

    /// <summary>
    /// Reads the importance a caller asked for, or says why it cannot (OP2-276). A value
    /// that is not understood is refused rather than sent as normal: the user asked for
    /// something specific and would believe it went out that way.
    /// </summary>
    public static string? ImportanceProblem(string? said, out MailImportance level)
    {
        var parsed = MailImportances.Parse(said);
        level = parsed ?? MailImportance.Normal;
        return parsed is null
            ? $"I don't know the importance \"{said}\". It can be high, normal or low. Nothing was sent."
            : null;
    }

    /// <summary>Read back with the confirmation, so "it went out flagged" is something the user was told rather than assumed.</summary>
    public static string Marked(MailImportance level) => Marked(level, ProviderKind.Microsoft);

    /// <summary>
    /// The same, per provider. Gmail has no importance flag of its own: Outpost sets the
    /// Importance and X-Priority headers that Outlook and most clients show, and Gmail's own
    /// interface does not — so the confirmation says exactly that rather than promising a
    /// marker the user will not see in Gmail (OP2-281).
    /// </summary>
    public static string Marked(MailImportance level, ProviderKind provider)
    {
        var said = level switch
        {
            MailImportance.High => ", marked high importance",
            MailImportance.Low => ", marked low importance",
            _ => "",
        };
        return said.Length > 0 && provider == ProviderKind.Google
            ? said + " (shown by Outlook and most mail apps; Gmail itself doesn't display it)"
            : said;
    }

    /// <summary>For listings: only the exception is said, so a normal line reads as before.</summary>
    public static string Marker(MailImportance level) => level switch
    {
        MailImportance.High => " (high importance)",
        MailImportance.Low => " (low importance)",
        _ => "",
    };

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
            return new(null, null, Handles.Unknown(index, UserId, message, "messages"));

        // Handles are shared with the calendar, so an ordinal said after a list of events
        // lands here pointing at a meeting. Say what it is instead of asking Graph for a
        // message by an event id and reporting "that message no longer exists".
        if (item.Kind is not ItemKind.Mail)
            return new(null, null,
                $"\"{item.Ref}\" is a calendar event — {item.Subject}. Use the calendar tools for it.");

        var c = await connections.FindByLabelAsync(UserId, item.ConnectionLabel, ct);
        if (c is null) return new(null, null, $"The {item.ConnectionLabel} connection has gone.");

        // A connection of a kind this build has no mail implementation for (OP2-278). Said
        // here, once, because every tool that acts on a message comes through this — the
        // attachment tools included, which have no catch of their own.
        if (mail is MailServiceRouter router && !router.Supports(c.Provider))
            return new(null, null, MailProviderNotSupportedException.Describe(c));

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

    /// <summary>
    /// Why a folder must not be deleted — yet, or at all — or null when it can go (OP2-188).
    /// <para>
    /// Separated out and tested directly because it is the only thing standing between a
    /// misheard folder name and a few hundred messages. This product is driven by voice,
    /// where names are misheard rather than mistyped, so the count has to be said out loud
    /// before anything happens; <paramref name="confirm"/> is what the model sets on the
    /// second turn, once the user has heard it.
    /// </para>
    /// </summary>
    public static string? Refuse(MailFolder folder, bool confirm)
    {
        if (folder.IsWellKnown)
            return $"{folder.Path} is one of Outlook's own folders, so I can't delete it. "
                 + "Only folders you made yourself.";

        if (confirm || (folder.MessageCount == 0 && folder.ChildCount == 0)) return null;

        return $"{folder.Path} isn't empty — "
             + $"{folder.MessageCount} message{(folder.MessageCount == 1 ? "" : "s")}"
             + (folder.ChildCount > 0
                 ? $" and {folder.ChildCount} subfolder{(folder.ChildCount == 1 ? "" : "s")}"
                 : "")
             + ". Deleting it sends all of that to Deleted Items. "
             + "Nothing has been deleted — ask the user, then say so to go ahead.";
    }

    /// <summary>
    /// Every folder someone could have meant by what they said, best spelling first.
    /// <para>
    /// A deep tree makes the bare name a poor address: the mailbox behind OP2-213 has 112
    /// folders, and a leaf name there is not remotely unique. So an id and a full path are
    /// accepted ahead of it (OP2-213 asked for both), a trailing run of path segments is
    /// accepted because <c>xInactive/Subscriptions</c> is what someone says rather than
    /// <c>Inbox/xInactive/Subscriptions</c>, and the bare name comes last. More than one hit
    /// is returned rather than resolved: the caller refuses, because filing mail into
    /// whichever folder happened to come back first is the quiet version of losing it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MailFolder> Matches(IReadOnlyList<MailFolder> folders, string? said)
    {
        var term = (said ?? "").Trim().Trim('/');
        if (term.Length == 0) return [];

        // An id first, and by exact case: a Graph folder id is a 120-character base64 run
        // that nobody says out loud, so it cannot collide with anything spoken.
        var byId = folders.FirstOrDefault(f => string.Equals(f.Id, term, StringComparison.Ordinal));
        if (byId is not null) return [byId];

        var byPath = folders.Where(f => Same(f.Path, term)).ToList();
        if (byPath.Count > 0) return byPath;

        if (term.Contains('/'))
        {
            var bySuffix = folders
                .Where(f => f.Path.EndsWith("/" + term, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (bySuffix.Count > 0) return bySuffix;
        }

        return folders.Where(f => Same(f.Name, term)).ToList();
    }

    /// <summary>
    /// The one folder someone meant, or null when nothing or several things match. Shared by
    /// every folder tool so that "Clients/Acme" means the same thing to all of them.
    /// </summary>
    public static MailFolder? Find(IReadOnlyList<MailFolder> folders, string? said) =>
        Matches(folders, said) is [var only] ? only : null;

    /// <summary>
    /// Either the folder meant, or what to say instead — not found, or found several times.
    /// </summary>
    public static (MailFolder? Folder, string? Problem) Pick(
        IReadOnlyList<MailFolder> folders, string? said, string label)
    {
        var hits = Matches(folders, said);

        return hits switch
        {
            [var only] => (only, null),
            [] => (null, NoFolder(label, said, folders)),
            _ => (null, $"\"{(said ?? "").Trim()}\" is the name of {hits.Count} folders in "
                      + $"{label} — {Untrusted.Scrub(string.Join(", ", hits.Select(f => f.Path)))}. "
                      + "Say the full path and I'll use that one."),
        };
    }

    /// <summary>
    /// Says what is there instead, and — since OP2-188 — that a missing folder is now
    /// something that can be made rather than a dead end.
    /// <para>
    /// Reading 112 folder paths out to somebody who is driving is not an answer, so a near
    /// miss is offered where there is one and the rest is a count. Before OP2-213 this
    /// listed every folder it knew, which was tolerable only because it knew so few.
    /// </para>
    /// </summary>
    public static string NoFolder(string label, string? said, IReadOnlyList<MailFolder> folders)
    {
        var term = (said ?? "").Trim();

        var near = folders
            .Where(f => term.Length > 1
                        && (f.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                            || term.Contains(f.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Path)
            .Take(5)
            .ToList();

        var instead = near.Count > 0
            ? $"Closest {(near.Count == 1 ? "is" : "are")} "
              + Untrusted.Scrub(string.Join(", ", near)) + "."
            : $"There {(folders.Count == 1 ? "is" : "are")} {folders.Count} folder"
              + $"{(folders.Count == 1 ? "" : "s")}, including "
              + Untrusted.Scrub(string.Join(", ", folders.Take(6).Select(f => f.Path))) + ".";

        return $"No folder called \"{term}\" in {label}. {instead} I can make one if you want it"
            // Not "it doesn't exist" when we can see that we did not finish looking.
            // Offering to create a folder that is really there, five levels down, is how a
            // mailbox ends up with two folders of the same name and mail in both.
            + (Deeper(folders) > 0
                ? ", though this mailbox has more folders than I was able to walk, so it may "
                  + "be there and out of my reach."
                : "")
            + ".";
    }

    /// <summary>
    /// How many listed folders have subfolders that the listing does not show.
    /// <para>
    /// Since OP2-213 the walk goes all the way down, so this is normally zero and a non-zero
    /// answer means the walk hit its request budget. It is kept because the surface saying
    /// "that folder does not exist" when it merely stopped looking is the whole of OP2-213.
    /// </para>
    /// </summary>
    public static int Deeper(IReadOnlyList<MailFolder> folders) =>
        folders.Count(f => f.ChildCount > 0
            && !folders.Any(x =>
                x.Path.StartsWith(f.Path + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Why a folder name is unusable, or null. '/' is refused because this surface uses it
    /// to qualify a folder by its parent — a folder called "A/B" could never be named back
    /// unambiguously, by the user or by us.
    /// </summary>
    public static string? Invalid(string? name) => (name ?? "").Trim() switch
    {
        "" => "I need a name for the folder.",
        var n when n.Contains('/') => $"\"{n}\" has a slash in it, which I use to mean "
            + "\"inside\" — pick a name without one and say which folder to put it in.",
        var n when n.Length > 255 => "That name is too long for a folder.",
        _ => null,
    };

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Names people for reading back. Scrubbed: a display name is whatever the sender chose
    /// to call themselves, and it reaches the model as content like any other.
    /// </summary>
    private static string Who(IEnumerable<MailRecipient> recipients) =>
        Untrusted.Scrub(string.Join(", ", recipients.Select(r => r.Display)));

    private static List<string> Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// The mailbox's own zone, for printing exact times (OP2-264). Never allowed to break a
    /// read: if the zone cannot be had, UTC is still an exact and unambiguous instant, and
    /// the offset printed beside it says which one it is.
    /// </summary>
    private async Task<MailboxZone> ZoneAsync(Connection c, CancellationToken ct)
    {
        // The zone comes from Microsoft Graph's mailbox settings, so only a Microsoft
        // connection can be asked (OP2-278). Anything else is "not known" — UTC, labelled
        // as such — rather than a Graph call made with somebody else's token.
        if (c.Provider != ProviderKind.Microsoft) return new MailboxZone(TimeZoneInfo.Utc, false);

        try { return new MailboxZone(await calendar.TimeZoneAsync(c, ct), true); }
        catch (OperationCanceledException) { throw; }

        // Known: false, so the surface can say the time is UTC because the zone could not
        // be had — NOT inferred from the zone being UTC, because a mailbox genuinely set to
        // UTC is not an unknown one and must not be labelled as one.
        catch (Exception) { return new MailboxZone(TimeZoneInfo.Utc, false); }
    }

    /// <summary>A mailbox's zone, and whether it is really the mailbox's.</summary>
    public readonly record struct MailboxZone(TimeZoneInfo Zone, bool Known);

    /// <summary>
    /// Zones for every mailbox a listing drew from, fetched one after another AFTER the
    /// fan-out rather than inside it. They are cached, so this is usually free, and it adds
    /// no new concurrent work on the request's scoped services (OP2-239).
    /// </summary>
    private async Task<Dictionary<string, MailboxZone>> ZonesAsync(
        IEnumerable<Connection> used, CancellationToken ct)
    {
        var zones = new Dictionary<string, MailboxZone>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in used)
            if (!zones.ContainsKey(c.Label)) zones[c.Label] = await ZoneAsync(c, ct);
        return zones;
    }

    /// <summary>"2 hours ago (2026-11-03 00:44 -05:00)": the phrase, then the value to act on.</summary>
    private static string When(DateTimeOffset at, string label, Dictionary<string, MailboxZone> zones)
    {
        var z = zones.TryGetValue(label, out var found) ? found : new MailboxZone(TimeZoneInfo.Utc, false);
        return $"{Timestamps.Ago(at, z.Zone)} ({Timestamps.Short(at, z.Zone)}{Unknown(z)})";
    }

    /// <summary>
    /// Says when a time is in UTC because the mailbox's own zone could not be had, rather
    /// than because the mailbox is in UTC.
    /// <para>
    /// MailboxSettings is an optional scope, so a connection consented before it was asked
    /// for answers 403 — and that fallback is cached for six hours. Unlabelled, somebody in
    /// Eastern would read a correct UTC instant as their local clock on the one line the
    /// whole ticket exists to make checkable against a deadline.
    /// </para>
    /// </summary>
    public static string Unknown(MailboxZone zone) =>
        zone.Known ? "" : " — UTC, mailbox zone unavailable";
}
