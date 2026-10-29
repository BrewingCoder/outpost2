namespace OutPost2.Core.Calendar;

/// <summary>One person's availability over the window that was asked about (OP2-78).</summary>
public sealed record ScheduleInformation
{
    public required string Address { get; init; }

    /// <summary>
    /// Set when Exchange could not answer for this address — a typo, somebody outside the
    /// organisation, or a mailbox whose free/busy is not published. Reported rather than
    /// dropped: a missing row silently reads as "they are free all day".
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// What is actually in the diary. Subject and location are present only where the
    /// viewer has more than free/busy access, so both are frequently null for a colleague.
    /// </summary>
    public IReadOnlyList<BusyPeriod> Busy { get; init; } = [];

    /// <summary>Their published working hours, where Exchange supplied them.</summary>
    public WorkingHours? WorkingHours { get; init; }

    public bool IsKnown => Error is null;
}

public sealed record BusyPeriod
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required Availability Status { get; init; }

    /// <summary>Null unless the viewer can see more than free/busy.</summary>
    public string? Subject { get; init; }
    public string? Location { get; init; }

    /// <summary>
    /// Free and working-elsewhere do not block a slot. Keeping them in the list but out of
    /// this predicate means the caller can still explain WHY a slot looked occupied.
    /// </summary>
    public bool Blocks => Status is Availability.Busy or Availability.OutOfOffice or Availability.Tentative;
}

public sealed record WorkingHours
{
    public required IReadOnlyList<DayOfWeek> Days { get; init; }
    public required TimeOnly Start { get; init; }
    public required TimeOnly End { get; init; }
}

/// <summary>
/// A slot Microsoft's own scheduler proposes, with its confidence that everyone can make
/// it. Distinct from a gap we computed: this accounts for working hours and the
/// organiser's own diary, and is ranked.
/// </summary>
public sealed record MeetingSuggestion
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }

    /// <summary>
    /// 0–100. Free counts 100, unknown 49 and busy 0 per attendee, averaged — so a
    /// suggestion at 49% means somebody's calendar could not be read, NOT that they are
    /// probably busy. Worth saying out loud, because the two are easy to confuse.
    /// </summary>
    public required double Confidence { get; init; }

    public required Availability OrganiserAvailability { get; init; }

    /// <summary>Anyone whose diary says they cannot make this slot.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    /// <summary>Anyone whose availability could not be determined at all.</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];
}

/// <summary>
/// What came back from a suggestion request. The empty case carries Microsoft's own
/// reason, which is the difference between "nobody is free" and "you asked about a
/// window in the past".
/// </summary>
public sealed record MeetingSuggestions
{
    public IReadOnlyList<MeetingSuggestion> Suggestions { get; init; } = [];

    /// <summary>Graph's <c>emptySuggestionsReason</c>, empty when there were suggestions.</summary>
    public string? EmptyReason { get; init; }
}
