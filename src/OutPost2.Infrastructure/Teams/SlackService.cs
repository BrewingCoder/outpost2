using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.Teams;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Teams;

/// <summary>
/// Slack behind the chat tools (OP2-86), over the Web API with the user's own token.
///
/// <para>
/// <b>There is no Teams-shaped chat.</b> Slack has one family, <c>conversations.*</c>, for
/// four kinds of thing told apart by flags and id prefix: public channels (<c>C…</c>),
/// private channels (<c>C…</c>, or <c>G…</c> on older workspaces), group DMs and DMs
/// (<c>D…</c>). <c>list_chats</c> maps to <c>users.conversations</c> — every conversation
/// the USER is a member of, of all four kinds — and not to <c>conversations.list</c>,
/// which lists every public channel in the workspace whether the user is in it or not.
/// A DM becomes a one-to-one chat named after the other person, a group DM a group chat
/// named by who is in it, and a channel a <see cref="ChatKind.Channel"/> named <c>#name</c>.
/// </para>
///
/// <para>
/// <b>Nothing lists conversations by recent activity.</b> Graph sorts chats by their last
/// message; Slack's listing has no such order and no last message. So a listing takes the
/// user's conversations — DMs and group DMs first, because "has anyone messaged me" is
/// about them, then channels — reads the newest message and the read marker of up to
/// <see cref="ProbeDepth"/> of them, and sorts those. A user in two hundred channels gets
/// the most recent of the DMs plus as many channels as fit, not all two hundred.
/// </para>
///
/// <para>
/// <b>Threads are counted, not read.</b> A thread reply is not a message in the channel —
/// <c>conversations.history</c> does not return it — and the transcript is the channel,
/// oldest first. Inlining replies would either break that order or interleave several
/// conversations into one, and would cost a <c>conversations.replies</c> call per thread.
/// Instead each message with replies says how many (<see cref="ChatMessage.ThreadReplies"/>),
/// and a reply the sender also posted to the channel is marked as one. Reading a thread
/// is a separate tool if it is wanted.
/// </para>
///
/// <para>
/// <b>Everything here is provider text, names included.</b> A Slack display name is free
/// text the person chose, and it is substituted into OTHER people's messages wherever they
/// are mentioned — so every name is flattened to one line where it is looked up
/// (<see cref="SlackText.OneLine"/>), and the tools scrub the sender again on the way out.
/// </para>
/// </summary>
public sealed class SlackService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    IMemoryCache cache,
    ILogger<SlackService> log) : IChatProvider
{
    public ProviderKind Provider => ProviderKind.Slack;

    /// <summary>
    /// How many conversations a listing reads the newest message of. Two calls each
    /// (history and info), both Tier 3 — about fifty a minute per method per workspace —
    /// so twenty-five leaves room for a second listing in the same minute.
    /// </summary>
    internal const int ProbeDepth = 25;

    /// <summary>How many DMs a name search resolves the other person of.</summary>
    private const int SearchDepth = 100;

    /// <summary>conversations.history's own page ceiling is higher; a voice turn does not need it.</summary>
    private const int HistoryMax = 100;

    /// <summary>
    /// How long a looked-up name is kept. Names change rarely and are looked up constantly —
    /// every mention in every message — and users.info is a call per person.
    /// </summary>
    private static readonly TimeSpan NameLifetime = TimeSpan.FromHours(1);

    /// <summary>Entries that are not something said: joins, leaves, renames, topic changes.</summary>
    private static readonly HashSet<string> SystemSubtypes = new(StringComparer.Ordinal)
    {
        "channel_join", "channel_leave", "channel_topic", "channel_purpose", "channel_name",
        "channel_archive", "channel_unarchive", "channel_convert_to_private", "channel_convert_to_public",
        "group_join", "group_leave", "group_topic", "group_purpose", "group_name",
        "group_archive", "group_unarchive", "pinned_item", "unpinned_item", "reminder_add",
        "bot_add", "bot_remove", "channel_posting_permissions", "joiner_notification_for_inviter",
        "joiner_notification", "tombstone", "huddle_thread", "sh_room_created",
    };

    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct)
    {
        var token = await connections.GetCredentialAsync(c, ct)
            ?? throw new TeamsException($"{c.Label} has no stored Slack token. Replace it on the connections page.");

        var http = SlackApi.Configure(httpFactory.CreateClient("slack"), token);
        await connections.TouchAsync(c.Id, ct);
        return http;
    }

    /// <summary>The user's own Slack id (U…), recorded from auth.test when it was connected.</summary>
    private static string Me(Connection c) => c.HomeAccountId ?? "";

    private static async Task<T> GetAsync<T>(HttpClient http, Connection c, string url, string what, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        return await SlackApi.ReadAsync<T>(resp, c.Label, what, ct);
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    // ── listing ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Chat>> ListChatsAsync(
        Connection connection, int top = 15, bool unreadOnly = false, CancellationToken ct = default)
    {
        using var http = await ClientAsync(connection, ct);
        var all = await MemberConversationsAsync(http, connection, ct);

        var probe = all
            .OrderBy(x => x.IsIm ? 0 : x.IsMpim ? 1 : 2)
            .Take(ProbeDepth)
            .ToList();

        // Bounded, because Slack's limits are per method per workspace and a burst of
        // twenty-five at once is the quickest way to meet one.
        using var gate = new SemaphoreSlim(4);
        var chats = await Task.WhenAll(probe.Select(async conv =>
        {
            await gate.WaitAsync(ct);
            try { return await ProbedChatAsync(http, connection, conv, ct); }
            finally { gate.Release(); }
        }));

        IEnumerable<Chat> listed = chats.OrderByDescending(c => c.LastActivity ?? DateTimeOffset.MinValue);
        if (unreadOnly) listed = listed.Where(c => c.IsUnread is true);

        return listed.Take(Math.Clamp(top, 1, ProbeDepth)).ToList();
    }

    /// <summary>
    /// The conversations the user is IN, all four kinds, archived ones left out. Paged by
    /// cursor, three pages at most: six hundred conversations is more than any listing reads.
    /// </summary>
    private static async Task<List<SlackConversation>> MemberConversationsAsync(
        HttpClient http, Connection c, CancellationToken ct)
    {
        var found = new List<SlackConversation>();
        string? cursor = null;

        for (var page = 0; page < 3; page++)
        {
            var url = "users.conversations?types=" + Q("public_channel,private_channel,mpim,im")
                    + "&exclude_archived=true&limit=200"
                    + (cursor is null ? "" : "&cursor=" + Q(cursor));

            var resp = await GetAsync<ConversationsPage>(http, c, url, "list your conversations", ct);
            found.AddRange((resp.Channels ?? []).Where(x => !(x.IsIm && x.IsUserDeleted)));

            cursor = resp.ResponseMetadata?.NextCursor;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return found;
    }

    private async Task<Chat> ProbedChatAsync(HttpClient http, Connection c, SlackConversation conv, CancellationToken ct)
    {
        // The read marker is on conversations.info; users.conversations does not carry it.
        var info = await GetAsync<ConversationInfo>(http, c, $"conversations.info?channel={Q(conv.Id)}",
            "read a conversation", ct);
        var history = await GetAsync<HistoryPage>(http, c, $"conversations.history?channel={Q(conv.Id)}&limit=1",
            "read a conversation", ct);

        var full = info.Channel ?? conv;
        var latest = history.Messages?.FirstOrDefault();
        var chat = await ToChatAsync(http, c, full, ct);

        string? preview = null, from = null;
        if (latest is not null && IsReadable(latest))
        {
            var names = await NamesAsync(http, c, SlackText.MentionedUsers(latest.Text), ct);
            preview = Summarise(Render(latest, names));
            from = latest.User == Me(c) && latest.BotId is null ? "You" : await SenderAsync(http, c, latest, ct);
        }

        return chat with
        {
            LastMessage = preview,
            LastMessageFrom = from,
            LastActivity = SlackText.Time(latest?.Ts),

            // Null where either side is missing, never false: "nothing waiting" is a claim.
            IsUnread = latest is null ? null : SlackText.After(latest.Ts, full.LastRead),
        };
    }

    private async Task<Chat> ToChatAsync(HttpClient http, Connection c, SlackConversation conv, CancellationToken ct)
    {
        if (conv.IsIm)
        {
            var other = conv.User is { Length: > 0 } u ? await NameAsync(http, c, u, ct) : null;
            return new Chat
            {
                Id = conv.Id, ConnectionLabel = c.Label, Kind = ChatKind.OneOnOne,
                Others = other is null ? [] : [other],
            };
        }

        if (conv.IsMpim)
        {
            // A group DM's "name" is mpdm-alice--bob--carol-1: handles, not names, and
            // Slack's own clients do not show it. The membership is what names it.
            var members = await GetAsync<MembersPage>(http, c,
                $"conversations.members?channel={Q(conv.Id)}&limit=20", "read who is in a group DM", ct);
            var others = new List<string>();
            foreach (var id in (members.Members ?? []).Where(m => m != Me(c)))
                if (await NameAsync(http, c, id, ct) is { } n) others.Add(n);

            return new Chat
            {
                Id = conv.Id, ConnectionLabel = c.Label, Kind = ChatKind.Group,
                Others = others.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        return new Chat
        {
            Id = conv.Id, ConnectionLabel = c.Label, Kind = ChatKind.Channel,
            Topic = string.IsNullOrWhiteSpace(conv.Name) ? null : "#" + SlackText.OneLine(conv.Name),
        };
    }

    // ── finding by name ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Chat>> FindChatsWithAsync(
        Connection connection, string who, CancellationToken ct = default)
    {
        var needle = (who ?? "").Trim();
        if (needle.Length == 0) return [];

        using var http = await ClientAsync(connection, ct);
        var all = await MemberConversationsAsync(http, connection, ct);
        var found = new List<Chat>();

        // A DM matches on any name the person goes by — display name, full name or
        // handle — because "message Darren" may mean any of the three.
        foreach (var im in all.Where(x => x.IsIm && x.User is { Length: > 0 }).Take(SearchDepth))
        {
            var person = await PersonAsync(http, connection, im.User!, ct);
            if (person is null || !person.Matches(needle)) continue;
            found.Add(new Chat
            {
                Id = im.Id, ConnectionLabel = connection.Label, Kind = ChatKind.OneOnOne, Others = [person.Name],
            });
        }

        foreach (var mp in all.Where(x => x.IsMpim).Take(20))
        {
            var chat = await ToChatAsync(http, connection, mp, ct);
            if (chat.Others.Any(o => o.Contains(needle, StringComparison.OrdinalIgnoreCase))) found.Add(chat);
        }

        // A channel only by its exact name: "Al" must not find #general.
        var channel = needle.TrimStart('#');
        foreach (var ch in all.Where(x => !x.IsIm && !x.IsMpim
                                          && string.Equals(x.Name, channel, StringComparison.OrdinalIgnoreCase)))
            found.Add(await ToChatAsync(http, connection, ch, ct));

        return found
            .OrderBy(c => c.Kind switch { ChatKind.OneOnOne => 0, ChatKind.Group => 1, _ => 2 })
            .ToList();
    }

    // ── reading ──────────────────────────────────────────────────────────────

    public async Task<ChatTranscript> ReadChatAsync(
        Connection connection, string chatId, int count = 20, CancellationToken ct = default)
    {
        var wanted = Math.Clamp(count, 1, 50);
        using var http = await ClientAsync(connection, ct);

        var chat = await OneChatAsync(http, connection, chatId, ct);

        // Over-fetched for the same reason as Teams: joins and topic changes are dropped
        // after the fact, and a channel whose last ten entries are joins would read empty.
        var page = await GetAsync<HistoryPage>(http, connection,
            $"conversations.history?channel={Q(chatId)}&limit={Math.Min(HistoryMax, wanted + 10)}",
            "read that conversation", ct);

        var raw = page.Messages ?? [];
        var names = await NamesAsync(http, connection, raw.SelectMany(m => SlackText.MentionedUsers(m.Text)), ct);

        var readable = new List<ChatMessage>();
        var skipped = 0;

        foreach (var m in raw)
        {
            if (!IsReadable(m)) { skipped++; continue; }

            // A reply that was NOT also sent to the channel. history should not return
            // one; if it ever does, it belongs to a thread this transcript does not show.
            if (m.ThreadTs is { } t && t != m.Ts && m.Subtype != "thread_broadcast") continue;

            var text = Render(m, names);
            var files = (m.Files ?? []).Select(f => SlackText.OneLine(f.Name ?? f.Title)).Where(n => n.Length > 0).ToList();
            if (text.Length == 0)
            {
                if (files.Count == 0) continue;
                text = $"(sent {string.Join(", ", files)})";
            }

            var isBot = m.BotId is not null || m.Subtype == "bot_message";
            readable.Add(new ChatMessage
            {
                Id = m.Ts ?? "",
                ConnectionLabel = connection.Label,
                From = await SenderAsync(http, connection, m, ct),
                FromBot = isBot,
                FromMe = !isBot && m.User is { Length: > 0 } u && u == Me(connection),
                Text = text,
                SentAt = SlackText.Time(m.Ts) ?? DateTimeOffset.MinValue,
                EditedAt = SlackText.Time(m.Edited?.Ts),
                Attachments = files,
                ThreadReplies = m.ReplyCount,
                IsThreadReply = m.Subtype == "thread_broadcast",
            });
        }

        // Slack gives newest first. Trim, then reverse, so the window keeps the newest.
        var window = readable.Take(wanted).Reverse().ToList();

        return new ChatTranscript
        {
            Chat = chat,
            Messages = window,
            MoreBefore = readable.Count > wanted || page.HasMore,
            SystemNoticesSkipped = skipped,
        };
    }

    private async Task<Chat> OneChatAsync(HttpClient http, Connection c, string id, CancellationToken ct)
    {
        var info = await GetAsync<ConversationInfo>(http, c, $"conversations.info?channel={Q(id)}",
            "find that conversation", ct);
        return await ToChatAsync(http, c, info.Channel
            ?? throw new TeamsException("Slack returned nothing for that conversation."), ct);
    }

    private static bool IsReadable(SlackMessage m) =>
        m.Hidden is not true && (m.Subtype is null || !SystemSubtypes.Contains(m.Subtype));

    private static string Render(SlackMessage m, IReadOnlyDictionary<string, string> names) =>
        SlackText.Render(m.Text, id => names.GetValueOrDefault(id)).Trim();

    private static string Summarise(string text)
    {
        if (text.Length == 0) return "(no text)";
        var first = text.Split('\n', 2)[0].Trim();
        return first.Length <= 120 ? first : first[..120] + "…";
    }

    // ── sending ──────────────────────────────────────────────────────────────

    public async Task<Chat> SendAsync(
        Connection connection, string chatId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new TeamsException("There was no message to send.");

        using var http = await ClientAsync(connection, ct);

        // Read first so the confirmation can name where it went.
        var chat = await OneChatAsync(http, connection, chatId, ct);

        // Escaped, so the text arrives as said: an "<" is not the start of a link and the
        // words "<!channel>" do not notify a whole channel. Top level, never a thread.
        using var resp = await http.PostAsJsonAsync("chat.postMessage",
            new { channel = chatId, text = SlackText.Escape(text) }, ct);
        await SlackApi.ReadAsync<Posted>(resp, connection.Label, "send that message", ct);

        log.LogInformation("Sent a Slack message to {ChatId} on {Label}.", chatId, connection.Label);
        return chat;
    }

    // ── people ───────────────────────────────────────────────────────────────

    private async Task<string> SenderAsync(HttpClient http, Connection c, SlackMessage m, CancellationToken ct)
    {
        if (m.BotId is not null || m.Subtype == "bot_message")
            return SlackText.OneLine(m.BotProfile?.Name ?? m.Username) is { Length: > 0 } bot ? bot : "a bot";

        return m.User is { Length: > 0 } u ? await NameAsync(http, c, u, ct) ?? "someone" : "someone";
    }

    private async Task<IReadOnlyDictionary<string, string>> NamesAsync(
        HttpClient http, Connection c, IEnumerable<string> ids, CancellationToken ct)
    {
        var names = new ConcurrentDictionary<string, string>();
        foreach (var id in ids.Distinct())
            if (await NameAsync(http, c, id, ct) is { } n) names[id] = n;
        return names;
    }

    private async Task<string?> NameAsync(HttpClient http, Connection c, string id, CancellationToken ct) =>
        (await PersonAsync(http, c, id, ct))?.Name;

    /// <summary>
    /// One person, through a cache shared by every request (OP2-86): a transcript names
    /// the same few people dozens of times. Keyed by workspace as well as id, because two
    /// workspaces' user ids are unrelated. A failed lookup is not cached and not fatal — a
    /// message from "someone" is still worth reading.
    /// </summary>
    private async Task<Person?> PersonAsync(HttpClient http, Connection c, string id, CancellationToken ct)
    {
        var key = $"slack-user:{c.Instance}:{id}";
        if (cache.TryGetValue(key, out Person? hit)) return hit;

        try
        {
            var resp = await GetAsync<UserInfo>(http, c, $"users.info?user={Q(id)}", "look a person up", ct);
            if (resp.User is not { } u) return null;

            var p = u.Profile;
            var display = SlackText.OneLine(p?.DisplayName);
            var real = SlackText.OneLine(p?.RealName ?? u.RealName);
            var handle = SlackText.OneLine(u.Name);
            var name = display.Length > 0 ? display : real.Length > 0 ? real : handle.Length > 0 ? handle : id;

            var person = new Person(name, [display, real, handle]);
            cache.Set(key, person, NameLifetime);
            return person;
        }
        catch (SlackApiException ex) when (ex.Error is not "ratelimited" and not "invalid_auth" and not "token_revoked")
        {
            log.LogInformation("Couldn't look up Slack user {Id} on {Label}: {Error}.", id, c.Label, ex.Error);
            return null;
        }
    }

    private sealed record Person(string Name, IReadOnlyList<string> Aliases)
    {
        public bool Matches(string needle) =>
            Aliases.Any(a => a.Length > 0 && a.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    // ── wire types (snake_case via SlackApi.Json) ────────────────────────────

    private sealed record ConversationsPage(List<SlackConversation>? Channels, ResponseMetadata? ResponseMetadata);

    private sealed record ResponseMetadata(string? NextCursor);

    private sealed record ConversationInfo(SlackConversation? Channel);

    private sealed record SlackConversation(
        string Id,
        string? Name = null,
        bool IsIm = false,
        bool IsMpim = false,
        bool IsChannel = false,
        bool IsGroup = false,
        bool IsPrivate = false,
        bool IsArchived = false,
        bool IsUserDeleted = false,
        string? User = null,
        string? LastRead = null);

    private sealed record MembersPage(List<string>? Members);

    private sealed record HistoryPage(List<SlackMessage>? Messages, bool HasMore = false);

    private sealed record SlackMessage(
        string? Ts,
        string? Text,
        string? User = null,
        string? Subtype = null,
        string? BotId = null,
        string? Username = null,
        BotProfile? BotProfile = null,
        string? ThreadTs = null,
        int ReplyCount = 0,
        bool? Hidden = null,
        Edited? Edited = null,
        List<SlackFile>? Files = null);

    private sealed record BotProfile(string? Name);

    private sealed record Edited(string? Ts);

    private sealed record SlackFile(string? Name, string? Title);

    private sealed record UserInfo(SlackUser? User);

    private sealed record SlackUser(string Id, string? Name, string? RealName, UserProfile? Profile);

    private sealed record UserProfile(string? DisplayName, string? RealName);

    private sealed record Posted(string? Ts);
}
