using OutPost2.Core.Calendar;

namespace OutPost2.Tests.Calendar;

/// <summary>
/// OP2-78. The interesting cases are all the same shape: an absence of information
/// looking exactly like a positive answer. "Exchange could not read their diary" and
/// "their diary is empty" produce the same silence, and only one of them means it is safe
/// to book the slot.
/// </summary>
public class AvailabilityTests
{
    private static BusyPeriod At(int fromHour, int toHour, Availability status) => new()
    {
        Start = new DateTimeOffset(2026, 11, 3, fromHour, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 11, 3, toHour, 0, 0, TimeSpan.Zero),
        Status = status,
    };

    [Theory]
    [InlineData(Availability.Busy, true)]
    [InlineData(Availability.OutOfOffice, true)]
    [InlineData(Availability.Tentative, true)]
    [InlineData(Availability.Free, false)]
    [InlineData(Availability.WorkingElsewhere, false)]
    public void Only_a_status_that_really_occupies_the_slot_blocks_it(Availability status, bool blocks)
    {
        // workingElsewhere is the one people get wrong: the person is working, so it looks
        // like busy, but they are available to meet and Exchange publishes the slot as
        // open. Treating it as busy would hide half the week for anyone hybrid.
        Assert.Equal(blocks, At(9, 10, status).Blocks);
    }

    [Fact]
    public void A_row_Exchange_could_not_answer_is_not_a_free_row()
    {
        var unknown = new ScheduleInformation
        {
            Address = "someone@external.example",
            Error = "no free/busy information published",
        };

        Assert.False(unknown.IsKnown);

        // And it has no busy periods — which is exactly why IsKnown has to be checked
        // first. Reading Busy.Count == 0 as "free" is the bug this guards.
        Assert.Empty(unknown.Busy);
    }

    [Fact]
    public void A_genuinely_free_row_is_distinguishable_from_an_unknown_one()
    {
        var free = new ScheduleInformation { Address = "colleague@example.com" };

        Assert.True(free.IsKnown);
        Assert.Empty(free.Busy);
    }

    [Fact]
    public void Non_blocking_periods_are_kept_so_the_reason_can_still_be_explained()
    {
        var row = new ScheduleInformation
        {
            Address = "colleague@example.com",
            Busy = [At(9, 10, Availability.Free), At(11, 12, Availability.Busy)],
        };

        // Both are retained; only one counts. Dropping the free one at parse time would
        // make "you have something then, but it is marked free" impossible to say.
        Assert.Equal(2, row.Busy.Count);
        Assert.Single(row.Busy.Where(b => b.Blocks));
    }

    [Fact]
    public void Unknown_attendees_are_reported_separately_from_unavailable_ones()
    {
        var suggestion = new MeetingSuggestion
        {
            Start = new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 11, 3, 14, 30, 0, TimeSpan.Zero),

            // Graph scores an unreadable calendar at 49%, so a confidence just under half
            // means "could not read a diary", NOT "probably booked". Conflating the two
            // turns a slot everyone can make into a reported clash.
            Confidence = 49.0,
            OrganiserAvailability = Availability.Free,
            Unknown = ["external@partner.example"],
        };

        Assert.Empty(suggestion.Unavailable);
        Assert.Single(suggestion.Unknown);
    }

    [Fact]
    public void An_empty_suggestion_set_carries_the_reason_it_was_empty()
    {
        // "No times work" and "you asked about a window in the past" need different
        // things from the user, and only Microsoft knows which happened.
        var result = new MeetingSuggestions { EmptyReason = "AttendeesUnavailable" };

        Assert.Empty(result.Suggestions);
        Assert.Equal("AttendeesUnavailable", result.EmptyReason);
    }

    [Fact]
    public void Working_hours_carry_the_days_as_well_as_the_times()
    {
        var hours = new WorkingHours
        {
            Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                    DayOfWeek.Thursday, DayOfWeek.Friday],
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(17, 0),
        };

        // Times without days would make Saturday look like a working day, which is how a
        // weekend slot gets proposed to somebody who does not work weekends.
        Assert.DoesNotContain(DayOfWeek.Saturday, hours.Days);
        Assert.Equal(5, hours.Days.Count);
    }
}
