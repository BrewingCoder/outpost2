using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Core.Teams;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Infrastructure.Teams;

public interface ITeamsService
{
    /// <summary>The user's chats, most recently active first.</summary>
    Task<IReadOnlyList<Chat>> ListChatsAsync(
        Connection connection, int top = 15, bool unreadOnly = false, CancellationToken ct = default);

    /// <summary>One chat's recent messages, oldest first.</summary>
    Task<ChatTranscript> ReadChatAsync(
        Connection connection, string chatId, int count = 20, CancellationToken ct = default);

    /// <summary>
    /// Finds the chats whose membership matches a name or address. Used to turn "message
    /// Darren on Teams" into a chat id without the user naming a conversation.
    /// </summary>
    Task<IReadOnlyList<Chat>> FindChatsWithAsync(
        Connection connection, string who, CancellationToken ct = default);

    /// <summary>Posts a message. Returns the chat it landed in, for reading back.</summary>
    Task<Chat> SendAsync(
        Connection connection, string chatId, string text, CancellationToken ct = default);
}

/// <summary>
/// Teams chats over Graph (OP2-79).
///
/// <para>
/// <b>Chats only, and that is a permissions decision rather than a scoping whim.</b> Every
/// delegated permission this needs — <c>Chat.Read</c> and <c>ChatMessage.Send</c> — is
/// user-consentable. Reading a CHANNEL needs <c>ChannelMessage.Read.All</c>, which is the
/// one Teams permission that requires admin consent, and a consent prompt is all or
/// nothing: adding it would make every connection in the product — mail, calendar,
/// people — need an administrator to approve. Channels are therefore a separate decision
/// with a real cost attached, not a later afternoon's work.
/// </para>
///
/// <para>
/// <b>A one-to-one chat has no name.</b> Graph returns <c>topic: null</c> for every one of
/// them, so the only way to say which conversation is which is to read the membership and
/// subtract the user. That is why every listing expands <c>members</c>, and why identity
/// is matched on the Entra object id out of <c>HomeAccountId</c> rather than on an email
/// address — a member's <c>email</c> is frequently null, and a UPN is not always the
/// mailbox address.
/// </para>
///
/// <para>
/// <b>System notices must be filtered, and the obvious filter misses them.</b> Joining a
/// chat, renaming it and adding a member all produce entries with <c>from: null</c> and a
/// body of <c>&lt;systemEventMessage/&gt;</c>, which read aloud as blank messages from
/// nobody. The trap is the enum: without the <c>Prefer: include-unknown-enum-members</c>
/// header Graph reports these as <c>unknownFutureValue</c>, NOT as
/// <c>systemEventMessage</c>, so matching the obvious name lets every one of them through.
/// They are identified here by the absence of a sender instead, which holds under both
/// spellings and under whatever the next event type is called.
/// </para>
///
/// <para>
/// <b>Ordering is backwards on purpose at the wire.</b> Graph supports descending order
/// only, so the newest messages are what a bounded request can get. They are reversed
/// before returning, because a conversation read newest-first is incomprehensible.
/// </para>
/// </summary>
public sealed class TeamsService(
    IMicrosoftConnectionServiceAccessor tokens,
    IHttpClientFactory httpFactory,
    ILogger<TeamsService> log) : ITeamsService
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    /// <summary>Graph's hard ceiling on both chats and messages. Asking for more is a 400.</summary>
    private const int GraphMaxTop = 50;

    /// <summary>
    /// How many chats to pull when searching membership for a name. The user's chat list
    /// is long and mostly dormant; the person being messaged is near the top in practice.
    /// </summary>
    private const int SearchDepth = 50;

    private async Task<HttpClient> ClientAsync(Connection connection, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(connection, ct)
            ?? throw new MailAuthRequiredException(connection.Label);

        var http = httpFactory.CreateClient("graph");
        http.BaseAddress = new Uri(Graph + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>
    /// The user's own Entra object id, taken from MSAL's <c>&lt;oid&gt;.&lt;tid&gt;</c>.
    /// This is what chat membership carries as <c>userId</c>, and it is the only field
    /// that matches reliably — <c>email</c> is often null on a member.
    /// </summary>
    private static string? SelfObjectId(Connection connection)
    {
        var home = connection.HomeAccountId;
        if (string.IsNullOrWhiteSpace(home)) return null;

        var dot = home.IndexOf('.');
        return dot > 0 ? home[..dot] : home;
    }

    // ── chats ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Chat>> ListChatsAsync(
        Connection connection, int top = 15, bool unreadOnly = false, CancellationToken ct = default)
    {
        var chats = await FetchChatsAsync(connection, Math.Clamp(top, 1, GraphMaxTop), ct);

        // Unread is filtered HERE rather than at the server because Graph offers no filter
        // for it — the signal is a comparison between two expanded properties, so it does
        // not exist until both have been fetched.
        if (unreadOnly) chats = chats.Where(c => c.IsUnread is true).ToList();

        return chats.Take(Math.Clamp(top, 1, GraphMaxTop)).ToList();
    }

    private async Task<List<Chat>> FetchChatsAsync(Connection connection, int top, CancellationToken ct)
    {
        using var http = await ClientAsync(connection, ct);

        // members AND lastMessagePreview in one request. Both are needed for a line that
        // can be read aloud — the first to name a one-to-one chat at all, the second so
        // the listing says what the chat is ABOUT rather than only who it is with.
        var url = $"me/chats?$top={top}" +
                  "&$expand=members,lastMessagePreview" +
                  "&$orderby=" + Uri.EscapeDataString("lastMessagePreview/createdDateTime desc");

        using var resp = await http.GetAsync(url, ct);
        await ThrowIfFailed(resp, connection, "read your Teams chats", ct);

        var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphChat>>(ct);
        var me = SelfObjectId(connection);

        return (page?.Value ?? [])
            .Select(c => c.ToChat(connection.Label, me))

            // A chat the user hid in Teams is hidden for a reason, and surfacing it in a
            // spoken list undoes a decision they already made.
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
    }

    public async Task<IReadOnlyList<Chat>> FindChatsWithAsync(
        Connection connection, string who, CancellationToken ct = default)
    {
        var needle = (who ?? "").Trim();
        if (needle.Length == 0) return [];

        var chats = await FetchChatsAsync(connection, SearchDepth, ct);

        // One-to-one first and group chats after. "Message Darren" means Darren, not the
        // six-person thread Darren happens to be in, and ordering rather than excluding
        // leaves the group reachable when there is no direct chat.
        return chats
            .Where(c => c.Others.Any(o => o.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Kind is ChatKind.OneOnOne ? 0 : 1)
            .ThenByDescending(c => c.LastActivity ?? DateTimeOffset.MinValue)
            .ToList();
    }

    // ── messages ─────────────────────────────────────────────────────────────

    public async Task<ChatTranscript> ReadChatAsync(
        Connection connection, string chatId, int count = 20, CancellationToken ct = default)
    {
        var wanted = Math.Clamp(count, 1, GraphMaxTop);

        using var http = await ClientAsync(connection, ct);

        var chat = await OneChatAsync(http, connection, chatId, ct);

        // Over-fetch, because system notices are filtered after the fact and a chat whose
        // last ten entries are all "X joined" would otherwise come back empty. Capped at
        // Graph's own ceiling rather than paging: this is a voice turn, not an archive.
        var ask = Math.Min(GraphMaxTop, wanted + 10);

        var url = $"chats/{Uri.EscapeDataString(chatId)}/messages?$top={ask}" +
                  "&$orderby=" + Uri.EscapeDataString("createdDateTime desc");

        using var resp = await http.GetAsync(url, ct);
        await ThrowIfFailed(resp, connection, "read that chat", ct);

        var page = await resp.Content.ReadFromJsonAsync<GraphList<GraphMessage>>(ct);
        var raw = page?.Value ?? [];
        var me = SelfObjectId(connection);

        var readable = new List<ChatMessage>();
        var skipped = 0;

        foreach (var m in raw)
        {
            var one = m.ToMessage(connection.Label, me);
            if (one is null) { skipped++; continue; }
            readable.Add(one);
        }

        // Graph gave newest-first; a conversation is read oldest-first. Trim before
        // reversing so the window keeps the NEWEST messages, not the oldest ones.
        var window = readable.Take(wanted).Reverse().ToList();

        return new ChatTranscript
        {
            Chat = chat,
            Messages = window,
            MoreBefore = readable.Count > wanted || page?.NextLink is not null,
            SystemNoticesSkipped = skipped,
        };
    }

    private static async Task<Chat> OneChatAsync(
        HttpClient http, Connection connection, string chatId, CancellationToken ct)
    {
        var url = $"chats/{Uri.EscapeDataString(chatId)}?$expand=members";

        using var resp = await http.GetAsync(url, ct);
        await ThrowIfFailed(resp, connection, "find that chat", ct);

        var raw = await resp.Content.ReadFromJsonAsync<GraphChat>(ct)
            ?? throw new TeamsException("Microsoft returned nothing for that chat.");

        return raw.ToChat(connection.Label, SelfObjectId(connection))
            ?? throw new TeamsException("That chat could not be read.");
    }

    public async Task<Chat> SendAsync(
        Connection connection, string chatId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new TeamsException("There was no message to send.");

        using var http = await ClientAsync(connection, ct);

        // The chat is read BEFORE the send so the confirmation can name who it went to.
        // "Sent" with nothing after it is not a confirmation when the caller could not see
        // which conversation was picked.
        var chat = await OneChatAsync(http, connection, chatId, ct);

        // contentType text, not html. The text came out of a spoken sentence, and anything
        // in it that looks like markup would either render as markup or be stripped —
        // neither of which is what the user said.
        var payload = new { body = new { contentType = "text", content = text } };

        using var resp = await http.PostAsJsonAsync(
            $"chats/{Uri.EscapeDataString(chatId)}/messages", payload, ct);

        await ThrowIfFailed(resp, connection, "send that message", ct);

        log.LogInformation("Sent a Teams message to chat {ChatId} on {Label}.", chatId, connection.Label);
        return chat;
    }

    // ── failures ─────────────────────────────────────────────────────────────

    private static async Task ThrowIfFailed(
        HttpResponseMessage resp, Connection connection, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        if (resp.StatusCode is HttpStatusCode.Forbidden)
            throw new TeamsException(
                $"{connection.Label} hasn't been given access to Teams — reconnect it to grant it.");

        // A chat id the user is not in, or one that has been deleted. Distinct from a 403,
        // and saying "no access to Teams" for it sends them to fix the wrong thing.
        if (resp.StatusCode is HttpStatusCode.NotFound)
            throw new TeamsException("That chat no longer exists, or you're no longer in it.");

        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new TeamsException(
            $"Couldn't {what} on {connection.Label}: {(int)resp.StatusCode}. "
            + (body.Length <= 300 ? body : body[..300] + "…"));
    }

    // ── wire types ───────────────────────────────────────────────────────────

    private sealed record GraphList<T>(
        [property: JsonPropertyName("value")] List<T>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink = null);

    private sealed record GraphChat(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("topic")] string? Topic,
        [property: JsonPropertyName("chatType")] string? ChatType,
        [property: JsonPropertyName("lastUpdatedDateTime")] DateTimeOffset? LastUpdated,
        [property: JsonPropertyName("members")] List<GraphMember>? Members,
        [property: JsonPropertyName("lastMessagePreview")] GraphMessage? LastMessagePreview,
        [property: JsonPropertyName("viewpoint")] GraphViewpoint? Viewpoint)
    {
        /// <summary>Null when the chat should not be listed at all.</summary>
        public Chat? ToChat(string label, string? meObjectId)
        {
            // Hidden in Teams by the user. They already decided they did not want to see
            // it; repeating it back in a spoken list undoes that decision.
            if (Viewpoint?.IsHidden is true) return null;

            var others = (Members ?? [])
                .Where(m => !m.Is(meObjectId))
                .Select(m => m.DisplayName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var preview = LastMessagePreview;
            var previewIsReadable = preview?.From is not null;

            var lastAt = preview?.CreatedDateTime ?? LastUpdated;

            return new Chat
            {
                Id = Id,
                ConnectionLabel = label,
                Topic = string.IsNullOrWhiteSpace(Topic) ? null : Topic,
                Kind = ChatType?.ToLowerInvariant() switch
                {
                    "oneonone" => ChatKind.OneOnOne,
                    "group" => ChatKind.Group,
                    "meeting" => ChatKind.Meeting,
                    _ => ChatKind.Other,
                },
                Others = others,

                // Only a real message makes a preview. A system notice preview would read
                // as "last message: (blank)", which is worse than saying nothing.
                LastMessage = previewIsReadable ? Summarise(preview!) : null,
                LastMessageFrom = previewIsReadable ? preview!.SenderName() : null,
                LastActivity = lastAt,
                IsUnread = Unread(preview?.CreatedDateTime, Viewpoint?.LastMessageReadDateTime),
            };
        }

        /// <summary>
        /// Null where it cannot be worked out, which must not collapse to false. "You have
        /// nothing waiting" is a claim; "I could not tell" is the truth when either side of
        /// the comparison is missing.
        /// </summary>
        private static bool? Unread(DateTimeOffset? lastMessage, DateTimeOffset? lastRead)
        {
            if (lastMessage is null) return null;
            if (lastRead is null) return null;

            // Graph returns 0001-01-01 for a chat that has never been opened, which is a
            // sentinel rather than a date — and it compares as "everything is unread",
            // which happens to be exactly right.
            return lastMessage > lastRead;
        }

        private static string Summarise(GraphMessage m)
        {
            var text = m.PlainText();
            if (text.Length == 0) return "(no text)";

            var firstLine = text.Split('\n', 2)[0].Trim();
            return firstLine.Length <= 120 ? firstLine : firstLine[..120] + "…";
        }
    }

    private sealed record GraphViewpoint(
        [property: JsonPropertyName("isHidden")] bool? IsHidden,
        [property: JsonPropertyName("lastMessageReadDateTime")] DateTimeOffset? LastMessageReadDateTime);

    private sealed record GraphMember(
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("userId")] string? UserId,
        [property: JsonPropertyName("email")] string? Email)
    {
        public bool Is(string? meObjectId) =>
            meObjectId is not null
            && string.Equals(UserId, meObjectId, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record GraphMessage(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("messageType")] string? MessageType,
        [property: JsonPropertyName("createdDateTime")] DateTimeOffset? CreatedDateTime,
        [property: JsonPropertyName("lastEditedDateTime")] DateTimeOffset? LastEditedDateTime,
        [property: JsonPropertyName("deletedDateTime")] DateTimeOffset? DeletedDateTime,
        [property: JsonPropertyName("importance")] string? Importance,
        [property: JsonPropertyName("from")] GraphFrom? From,
        [property: JsonPropertyName("body")] GraphBody? Body,
        [property: JsonPropertyName("attachments")] List<GraphAttachment>? Attachments)
    {
        /// <summary>Null when this is not something to read out.</summary>
        public ChatMessage? ToMessage(string label, string? meObjectId)
        {
            // The system-notice test. NOT a check for messageType == "systemEventMessage":
            // without the Prefer: include-unknown-enum-members header Graph calls these
            // "unknownFutureValue", so the obvious check passes every one of them through
            // as a blank message from nobody. The absent sender is the durable signal.
            if (From is null) return null;

            // A deleted message keeps its row and loses its body. Reading it out as an
            // empty message from a colleague is worse than not mentioning it.
            if (DeletedDateTime is not null) return null;

            var text = PlainText();
            var attachments = (Attachments ?? [])
                .Select(a => a.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();

            // An image-only or attachment-only message converts to nothing at all, because
            // the markup carrying it is all that was there. Saying so beats a blank line
            // under somebody's name.
            if (text.Length == 0)
            {
                if (attachments.Count > 0) text = $"(sent {string.Join(", ", attachments)})";
                else if (HasInlineContent()) text = "(sent an image)";
                else return null;
            }

            return new ChatMessage
            {
                Id = Id,
                ConnectionLabel = label,
                From = SenderName(),
                FromBot = From.User is null && From.Application is not null,
                FromMe = From.User is not null
                         && meObjectId is not null
                         && string.Equals(From.User.Id, meObjectId, StringComparison.OrdinalIgnoreCase),
                Text = text,
                SentAt = CreatedDateTime ?? DateTimeOffset.MinValue,
                EditedAt = LastEditedDateTime,
                IsUrgent = string.Equals(Importance, "urgent", StringComparison.OrdinalIgnoreCase),
                Attachments = attachments,
            };
        }

        public string SenderName() =>
            From?.User?.DisplayName
            ?? From?.Application?.DisplayName
            ?? "someone";

        public string PlainText()
        {
            var content = Body?.Content;
            if (string.IsNullOrWhiteSpace(content)) return "";

            // Teams sends either, per message, in the same conversation. "text" is already
            // plain and must not go through the converter — a message containing "a < b"
            // would lose everything after the "<" to the tag stripper.
            var text = string.Equals(Body!.ContentType, "text", StringComparison.OrdinalIgnoreCase)
                ? content
                : HtmlToText.Convert(content);

            return text.Trim();
        }

        private bool HasInlineContent() =>
            Body?.Content is { } c
            && (c.Contains("<img", StringComparison.OrdinalIgnoreCase)
                || c.Contains("<attachment", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record GraphBody(
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record GraphFrom(
        [property: JsonPropertyName("user")] GraphIdentity? User,
        [property: JsonPropertyName("application")] GraphIdentity? Application);

    private sealed record GraphIdentity(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName);

    private sealed record GraphAttachment(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("contentType")] string? ContentType);
}

public sealed class TeamsException(string message) : Exception(message);
