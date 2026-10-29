namespace OutPost2.Core.Teams;

/// <summary>
/// A Teams conversation the user is part of (OP2-79). Chats only — channels are
/// deliberately out of scope, see <c>TeamsService</c> for why.
/// </summary>
public sealed record Chat
{
    public required string Id { get; init; }
    public required string ConnectionLabel { get; init; }

    public required ChatKind Kind { get; init; }

    /// <summary>
    /// The chat's own name, which a one-to-one chat NEVER has — Graph returns null for
    /// those. <see cref="DisplayName"/> is what to show; this is only here so the two
    /// can be told apart when a group chat has genuinely been left unnamed.
    /// </summary>
    public string? Topic { get; init; }

    /// <summary>Everyone in it except the user. Empty when membership could not be read.</summary>
    public IReadOnlyList<string> Others { get; init; } = [];

    /// <summary>
    /// What to call this chat out loud. A one-to-one chat has no topic, so the only
    /// usable name is the other person — and an unnamed group chat has to be described
    /// by who is in it or it reads as "a chat" with nothing to distinguish it from the
    /// other eleven.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Topic)) return Topic;
            if (Others.Count == 0) return Kind is ChatKind.OneOnOne ? "a chat" : "an unnamed group chat";
            if (Others.Count <= 3) return string.Join(", ", Others);

            return $"{string.Join(", ", Others.Take(2))} and {Others.Count - 2} others";
        }
    }

    /// <summary>First line of the most recent message, or null when the chat is empty.</summary>
    public string? LastMessage { get; init; }

    /// <summary>Who sent <see cref="LastMessage"/>. Null for a system notice or an unread preview.</summary>
    public string? LastMessageFrom { get; init; }

    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>
    /// True when the last message arrived after the user last read the chat.
    /// <para>
    /// Derived rather than reported: Graph exposes <c>viewpoint.lastMessageReadDateTime</c>
    /// and the preview's <c>createdDateTime</c>, and the comparison between them is the
    /// only unread signal available. Null where either is missing, which must read as
    /// "not known" rather than "read" — telling someone they have nothing waiting is
    /// worse than admitting the question could not be answered.
    /// </para>
    /// </summary>
    public bool? IsUnread { get; init; }
}

public enum ChatKind
{
    /// <summary>Two people. Has no topic, ever.</summary>
    OneOnOne,

    /// <summary>Three or more, named or not.</summary>
    Group,

    /// <summary>The chat attached to a meeting. Usually named after the meeting.</summary>
    Meeting,

    /// <summary>Anything Graph reports that this version does not know about.</summary>
    Other,
}

/// <summary>One message in a chat, already reduced to something worth reading aloud.</summary>
public sealed record ChatMessage
{
    public required string Id { get; init; }
    public required string ConnectionLabel { get; init; }

    /// <summary>Sender's display name. Never empty — an unattributable message is dropped upstream.</summary>
    public required string From { get; init; }

    /// <summary>
    /// True when the sender is an app rather than a person. Worth saying: a build robot
    /// and a colleague read identically in a spoken list, and only one of them is waiting
    /// for a reply.
    /// </summary>
    public bool FromBot { get; init; }

    /// <summary>True when the user sent it. "You said" and "they said" are different sentences.</summary>
    public bool FromMe { get; init; }

    /// <summary>Plain text, converted from whatever Graph sent.</summary>
    public required string Text { get; init; }

    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Set when the sender edited it afterwards, which changes what "they said" means.</summary>
    public DateTimeOffset? EditedAt { get; init; }

    public bool IsUrgent { get; init; }

    /// <summary>Names of files attached to the message. The files themselves are not fetched.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
}

/// <summary>
/// A page of messages plus whether there are older ones, so the tool can say "that is
/// the start of the conversation" rather than leaving the model to guess.
/// </summary>
public sealed record ChatTranscript
{
    public required Chat Chat { get; init; }

    /// <summary>Oldest first — the order a conversation is read in, not the order Graph returns.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>True when the window was filled and older messages exist beyond it.</summary>
    public bool MoreBefore { get; init; }

    /// <summary>
    /// How many entries were dropped as system notices ("X added Y to the chat"). Not
    /// shown as a number, but a transcript that is empty ONLY because everything in it
    /// was a join notice needs to say so rather than read as silence.
    /// </summary>
    public int SystemNoticesSkipped { get; init; }
}
