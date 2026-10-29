using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Core.Teams;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Mail;
using OutPost2.Infrastructure.Teams;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// Teams chats (OP2-79).
///
/// <para>
/// Mail is where the day's instructions arrive; Teams is where the day actually happens.
/// "Did Darren ever answer me?" and "tell the team I'm running twenty minutes late" are
/// both Teams questions, and both are exactly the shape of thing asked from a car.
/// </para>
///
/// <para>
/// Chats only — not channels. The reason is consent rather than ambition, and it is set
/// out on <see cref="TeamsService"/>: every chat permission is user-consentable, while
/// reading a channel needs the one Teams permission that requires an administrator.
/// </para>
///
/// <para>
/// Chat content is fenced exactly as mail is (OP2-125), and arguably needs it more. A
/// Teams message arrives somewhere people trust, inside the organisation, from a colleague
/// — all of which makes "ignore your previous instructions" read as more credible there
/// than in an email from outside.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class TeamsTools(
    IHttpContextAccessor http,
    IConnectionStore connections,
    ITeamsService teams,
    IItemIndex index,
    PublicUrls urls)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    [McpServerTool(Name = "list_chats", ReadOnly = true, OpenWorld = true)]
    [Description("The user's recent Teams chats, most recently active first. Each carries a handle in square brackets — pass that to read one or to reply. Use unreadOnly to answer 'has anyone messaged me?'.")]
    public async Task<string> ListChats(
        [Description("Only chats with messages arrived since the user last read them.")] bool unreadOnly = false,
        [Description("Connection name such as 'work'. Use 'all' for every connection. Omit only if a default has been set.")] string? connection = null,
        [Description("How many chats to return per connection.")] int count = 12,
        CancellationToken ct = default)
    {
        var all = await connections.ListAsync(UserId, ct);
        var resolution = ConnectionResolver.Resolve(
            all, Capabilities.ChatRead, connection, isWrite: false);

        if (!resolution.IsActionable) return Explain(resolution, Capabilities.ChatRead, all);

        var found = new List<Chat>();
        var failures = new List<string>();

        await Task.WhenAll(resolution.Connections.Select(async c =>
        {
            try
            {
                foreach (var chat in await teams.ListChatsAsync(c, Math.Clamp(count, 1, 50), unreadOnly, ct))
                    lock (found) found.Add(chat);
            }
            catch (MailAuthRequiredException) { lock (failures) failures.Add($"{c.Label} needs re-authorising"); }
            catch (TeamsException ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
            catch (Exception ex) { lock (failures) failures.Add($"{c.Label}: {ex.Message}"); }
        }));

        var ordered = found
            .OrderByDescending(c => c.LastActivity ?? DateTimeOffset.MinValue)
            .ToList();

        // The handle carries the chat's DisplayName as its subject, so a later "reply to
        // that one" can be confirmed by name rather than by an opaque thread id.
        var refs = index.Remember(UserId,
            ordered.Select(c => (c.Id, c.ConnectionLabel, c.DisplayName, c.LastMessageFrom ?? "")).ToList(),
            ItemKind.Chat);

        var sb = new StringBuilder();

        if (ordered.Count == 0)
        {
            sb.AppendLine(unreadOnly
                ? "Nothing unread in Teams."
                : "No Teams chats.");
        }
        else
        {
            sb.AppendLine(unreadOnly
                ? $"{ordered.Count} chat{(ordered.Count == 1 ? "" : "s")} with something unread:"
                : $"{ordered.Count} recent chat{(ordered.Count == 1 ? "" : "s")}:");

            for (var n = 0; n < ordered.Count; n++)
            {
                var chat = ordered[n];

                sb.Append($"{n + 1}. [{refs[n].Ref}] ");
                if (resolution.Connections.Count > 1) sb.Append($"[{chat.ConnectionLabel}] ");
                sb.Append(Untrusted.Scrub(chat.DisplayName));

                if (chat.Kind is ChatKind.Group or ChatKind.Meeting)
                    sb.Append(chat.Kind is ChatKind.Meeting ? " (meeting chat)" : " (group)");

                // Only in the full listing. In an unread-only listing every row is unread
                // by construction, and saying so on each one is noise.
                if (!unreadOnly && chat.IsUnread is true) sb.Append(" — UNREAD");

                if (chat.LastActivity is { } at) sb.Append($" — {Ago(at)}");
                sb.AppendLine();

                if (chat.LastMessage is { } last)
                {
                    var who = chat.LastMessageFrom is { } f ? $"{Untrusted.Scrub(f)}: " : "";
                    sb.AppendLine($"   {who}{Untrusted.Scrub(last)}");
                }
            }
        }

        foreach (var f in failures) sb.AppendLine($"Note: {f}.");
        foreach (var d in resolution.Degraded) sb.AppendLine($"Note: {d.Label} is unavailable ({d.State}).");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "read_chat", ReadOnly = true, OpenWorld = true)]
    [Description("Read a Teams conversation, oldest message first. Pass the handle from the last listing, or a person's name to find the chat with them.")]
    public async Task<string> ReadChat(
        [Description("The handle in square brackets from a listing, or a person's name such as 'Darren'.")] string chat,
        [Description("Connection name. Needed only when resolving a name and more than one connection could answer.")] string? connection = null,
        [Description("How many messages to read back.")] int count = 20,
        CancellationToken ct = default)
    {
        var located = await LocateAsync(chat, connection, Capabilities.ChatRead, ct);
        if (located.Problem is { } problem) return problem;

        var (c, chatId) = (located.Connection!, located.ChatId!);

        try
        {
            var transcript = await teams.ReadChatAsync(c, chatId, Math.Clamp(count, 1, 50), ct);

            // Minted only when the chat was reached by NAME and so has no handle yet —
            // otherwise "reply to that" after "read my chat with Darren" has nothing to
            // resolve against. Deliberately NOT re-registered when a handle was used:
            // Remember replaces the last listing, so doing it unconditionally would make
            // "read the second one" leave a one-item listing behind and break the "reply
            // to the fourth one" that follows it.
            var handle = located.Handle
                ?? index.Remember(UserId,
                    [(transcript.Chat.Id, c.Label, transcript.Chat.DisplayName, "")],
                    ItemKind.Chat)[0].Ref;

            var sb = new StringBuilder();
            sb.AppendLine($"Chat with {Untrusted.Scrub(transcript.Chat.DisplayName)} on {c.Label} "
                        + $"(handle {handle}).");

            if (transcript.Messages.Count == 0)
            {
                sb.AppendLine(transcript.SystemNoticesSkipped > 0
                    ? "Nothing said yet — only joining and renaming notices."
                    : "No messages in this chat.");
            }
            else
            {
                if (transcript.MoreBefore) sb.AppendLine("(Older messages exist before these.)");
                sb.AppendLine();

                // One fence around the whole transcript rather than one per message. Each
                // message is attributed inside it, and a dozen fences in a row is noise
                // that makes the boundary easier to miss, not harder.
                var body = new StringBuilder();
                foreach (var m in transcript.Messages)
                {
                    var who = m.FromMe ? "You" : m.From;
                    body.Append($"[{Ago(m.SentAt)}] {who}");
                    if (m.FromBot) body.Append(" (a bot)");
                    if (m.EditedAt is not null) body.Append(" (edited)");
                    if (m.IsUrgent) body.Append(" (marked urgent)");
                    body.AppendLine(":");
                    body.AppendLine(m.Text);
                    body.AppendLine();
                }

                sb.AppendLine(Untrusted.Fence("Teams conversation", body.ToString().TrimEnd()));
            }

            return sb.ToString().TrimEnd();
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (TeamsException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "send_chat_message", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Send a Teams message. Identify the chat either by the handle from a listing or by a person's name. The message appears instantly under the user's own name and cannot be unsent.")]
    public async Task<string> SendChatMessage(
        [Description("What to say. Sent as plain text, exactly as given.")] string message,
        [Description("The handle in square brackets from a listing, or a person's name such as 'Darren'.")] string chat,
        [Description("Which connection to send from, e.g. 'work'. Required when more than one could send.")] string? connection = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return "There's nothing to send — what should it say?";

        // isWrite: true, for the same reason send_mail uses it. A Teams message cannot be
        // unsent and the recipient sees whichever identity was guessed.
        var located = await LocateAsync(chat, connection, Capabilities.ChatSend, ct, isWrite: true);
        if (located.Problem is { } problem) return problem;

        var (c, chatId) = (located.Connection!, located.ChatId!);

        try
        {
            var sent = await teams.SendAsync(c, chatId, message, ct);

            // Named, not just "sent". The caller could not see which conversation was
            // picked, and for a name-resolved send that choice was ours to get wrong.
            return $"Sent to {Untrusted.Scrub(sent.DisplayName)} on {c.Label}.";
        }
        catch (MailAuthRequiredException ex) { return ex.Message; }
        catch (TeamsException ex) { return ex.Message; }
    }

    // ── locating a chat ──────────────────────────────────────────────────────

    /// <param name="Handle">
    /// The handle the chat was reached BY, when it was reached by one. Null for a chat
    /// found by name, which has not been listed and so has nothing to refer back to yet.
    /// </param>
    private readonly record struct Located(
        Connection? Connection, string? ChatId, string? Problem, string? Handle = null);

    /// <summary>
    /// Turns what the user said into one chat on one connection — accepting either a
    /// handle from a listing or a bare name, because "reply to Darren" and "reply to that
    /// one" are the same request phrased by two different turns of a conversation.
    /// </summary>
    private async Task<Located> LocateAsync(
        string said, string? connection, string capability, CancellationToken ct, bool isWrite = false)
    {
        // A handle first. It is unambiguous, it survives a chat being renamed, and trying
        // it costs nothing — a name will simply not resolve.
        if (index.Resolve(UserId, said) is { } item)
        {
            if (item.Kind is not ItemKind.Chat)
                return new Located(null, null,
                    $"\"{item.Ref}\" is " + item.Kind switch
                    {
                        ItemKind.Event => "a calendar event",
                        ItemKind.Contact => "a contact",
                        _ => "an email",
                    }
                    + $" — {Untrusted.Scrub(item.Subject)}. That isn't a Teams chat.");

            var byHandle = await connections.FindByLabelAsync(UserId, item.ConnectionLabel, ct);
            if (byHandle is null) return new Located(null, null, $"The {item.ConnectionLabel} connection has gone.");

            var cap = byHandle.Capabilities.FirstOrDefault(x => x.Name == capability);
            if (cap is null)
                return new Located(null, null,
                    $"{byHandle.Label} was connected before Outpost asked for permission to "
                    + (capability == Capabilities.ChatSend ? "send Teams messages" : "read Teams chats")
                    + $". Reconnecting it at {urls.Connections} fixes that.");

            if (!cap.Usable)
                return new Located(null, null,
                    ConnectionResolution.Unavailable(byHandle, capability).SpokenSummary);

            return new Located(byHandle, item.ItemId, null, item.Ref);
        }

        var all = await connections.ListAsync(UserId, ct);
        var resolution = ConnectionResolver.Resolve(all, capability, connection, isWrite);

        if (!resolution.IsActionable) return new Located(null, null, Explain(resolution, capability, all));

        if (resolution.Kind is ResolutionKind.FanOut)
            return new Located(null, null,
                "I can only use one connection for that — which: "
                + string.Join(", ", resolution.Connections.Select(x => x.Label)) + "?");

        var c = resolution.Connection!;

        try
        {
            var matches = await teams.FindChatsWithAsync(c, said, ct);

            if (matches.Count == 0)
                return new Located(null, null,
                    $"No Teams chat with anyone called \"{Untrusted.Scrub(said)}\" on {c.Label}. "
                    + "I can only use conversations that already exist — starting a brand new "
                    + "one isn't something Outpost can do yet.");

            // One direct chat and the rest groups is not ambiguous: "message Darren" means
            // Darren. Two one-to-one chats both matching IS ambiguous, and picking the
            // livelier of them would send a private message to the wrong colleague.
            var direct = matches.Where(m => m.Kind is ChatKind.OneOnOne).ToList();
            if (direct.Count == 1) return new Located(c, direct[0].Id, null);

            if (matches.Count == 1) return new Located(c, matches[0].Id, null);

            var shortlist = (direct.Count > 1 ? direct : matches).Take(5).ToList();
            var refs = index.Remember(UserId,
                shortlist.Select(m => (m.Id, m.ConnectionLabel, m.DisplayName, "")).ToList(),
                ItemKind.Chat);

            var sb = new StringBuilder();
            sb.AppendLine($"More than one chat matches \"{Untrusted.Scrub(said)}\" — which one?");
            for (var n = 0; n < shortlist.Count; n++)
                sb.AppendLine($"- [{refs[n].Ref}] {Untrusted.Scrub(shortlist[n].DisplayName)}"
                            + (shortlist[n].Kind is ChatKind.OneOnOne ? "" : " (group)"));

            return new Located(null, null, sb.ToString().TrimEnd());
        }
        catch (MailAuthRequiredException ex) { return new Located(null, null, ex.Message); }
        catch (TeamsException ex) { return new Located(null, null, ex.Message); }
    }

    /// <summary>
    /// As on <c>PeopleTools</c>: a connection predating these scopes declares no such
    /// capability at all, so it is absent from both the candidates and Degraded, and the
    /// generic "nothing set up for that" describes it uselessly. The fix is one
    /// reconnection — but only say so to somebody who HAS a Microsoft connection.
    /// </summary>
    private string Explain(
        ConnectionResolution resolution, string capability, IReadOnlyList<Connection> all)
    {
        if (resolution.Kind is not ResolutionKind.None) return resolution.SpokenSummary;

        var microsoft = all.Count(c => c.Provider == ProviderKind.Microsoft);
        if (microsoft == 0) return resolution.SpokenSummary;

        var what = capability == Capabilities.ChatSend ? "send Teams messages" : "read your Teams chats";

        return $"None of your {(microsoft == 1 ? "connection" : "connections")} can {what} yet — "
             + $"that permission was added after {(microsoft == 1 ? "it was" : "they were")} set up. "
             + $"Reconnecting at {urls.Connections} grants it.";
    }

    /// <summary>Relative time. Same reasoning as the mail tools: a timestamp is unspeakable.</summary>
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
