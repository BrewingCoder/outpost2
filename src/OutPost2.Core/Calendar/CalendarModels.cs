namespace OutPost2.Core.Calendar;

/// <summary>
/// One event in a listing. Carries its connection label for the same reason mail does:
/// a merged result that cannot say which calendar an entry came from is worse than no
/// merge at all, and two tenants routinely hold the same meeting twice.
/// </summary>
public sealed record CalendarEventSummary
{
    public required string Id { get; init; }
    public required string ConnectionLabel { get; init; }
    public required string Subject { get; init; }

    /// <summary>Who called it. Empty for an appointment the user made for themselves.</summary>
    public required string Organiser { get; init; }

    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required bool IsAllDay { get; init; }

    public string? Location { get; init; }

    /// <summary>True when Teams or another provider has attached a join link.</summary>
    public required bool IsOnlineMeeting { get; init; }

    /// <summary>
    /// What the user has answered, if anything. The difference between "you are going to
    /// this" and "you have not replied" is the whole reason to read a calendar aloud.
    /// </summary>
    public required InviteStatus MyResponse { get; init; }

    /// <summary>Free, busy, tentative, out of office. Drives whether a slot really is taken.</summary>
    public required Availability ShowAs { get; init; }

    /// <summary>True when the event is one occurrence of a series.</summary>
    public required bool IsRecurring { get; init; }

    public bool IsCancelled { get; init; }
}

public sealed record CalendarEvent
{
    public required string Id { get; init; }
    public required string ConnectionLabel { get; init; }
    public required string Subject { get; init; }
    public required string Organiser { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required bool IsAllDay { get; init; }
    public string? Location { get; init; }
    public required InviteStatus MyResponse { get; init; }
    public required Availability ShowAs { get; init; }
    public required bool IsRecurring { get; init; }
    public bool IsCancelled { get; init; }

    /// <summary>True when the signed-in account called the meeting, which decides whether
    /// changing it is an edit or an imposition on somebody else's diary.</summary>
    public required bool IOrganised { get; init; }

    public required IReadOnlyList<EventAttendee> Attendees { get; init; }

    /// <summary>The join link, where there is one. Spoken aloud it is useless, but it is
    /// what a client needs in order to offer "join".</summary>
    public string? OnlineMeetingUrl { get; init; }

    /// <summary>Plain text, converted from whatever Graph returned. Third-party content.</summary>
    public required string Body { get; init; }

    /// <summary>How the series repeats, in a form a person would say. Null when it does not.</summary>
    public string? Recurrence { get; init; }
}

public sealed record EventAttendee
{
    public required string Name { get; init; }
    public required string Address { get; init; }

    /// <summary>Optional attendees are the ones it is safe to double-book.</summary>
    public required bool IsOptional { get; init; }

    public required InviteStatus Response { get; init; }
}

/// <summary>
/// Where a reply has got to. <see cref="None"/> and <see cref="NotResponded"/> are
/// genuinely different: the first is an appointment nobody was invited to, the second is
/// an invitation sitting unanswered, and only one of them is something to tell the user about.
/// </summary>
public enum InviteStatus
{
    None,
    NotResponded,
    Accepted,
    TentativelyAccepted,
    Declined,

    /// <summary>The organiser's own row. Always counts as attending.</summary>
    Organiser,
}

/// <summary>
/// How the time is published to other people. Distinct from whether the event exists:
/// an event marked Free does not block the slot, so reading it as "you are busy" would
/// send the user into a meeting they deliberately left open.
/// </summary>
public enum Availability
{
    Free,
    Tentative,
    Busy,
    OutOfOffice,
    WorkingElsewhere,
    Unknown,
}

/// <summary>What to do with an invitation. All three notify the organiser.</summary>
public enum InviteReply
{
    Accept,
    Decline,
    Tentative,
}

/// <summary>A new or amended event, in the mailbox's own local time.</summary>
public sealed record OutgoingEvent
{
    public required string Subject { get; init; }

    /// <summary>
    /// Local wall-clock time in the mailbox's timezone, with no offset attached. Deliberately
    /// not a <see cref="DateTimeOffset"/>: "move it to 2pm" means two o'clock where the user
    /// is, and resolving that to an instant before the mailbox's zone is known is how a
    /// meeting lands an hour out across a DST boundary.
    /// </summary>
    public required DateTime Start { get; init; }

    public required DateTime End { get; init; }

    public bool IsAllDay { get; init; }

    public IReadOnlyList<string> RequiredAttendees { get; init; } = [];
    public IReadOnlyList<string> OptionalAttendees { get; init; } = [];

    public string? Location { get; init; }

    /// <summary>Markdown, converted and sanitised before it goes out, as mail bodies are.</summary>
    public string? Body { get; init; }

    /// <summary>Ask Graph to attach a Teams meeting.</summary>
    public bool IsOnlineMeeting { get; init; }

    public Availability ShowAs { get; init; } = Availability.Busy;
}

/// <summary>
/// A change to an existing event. Every field is nullable and only the ones set are sent:
/// a PATCH that echoes back unchanged values would clobber a concurrent edit, and sending
/// a full body for "move it half an hour" re-notifies every attendee about everything.
/// </summary>
public sealed record EventChanges
{
    public string? Subject { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public string? Location { get; init; }
    public Availability? ShowAs { get; init; }

    public bool IsEmpty =>
        Subject is null && Start is null && End is null && Location is null && ShowAs is null;
}
