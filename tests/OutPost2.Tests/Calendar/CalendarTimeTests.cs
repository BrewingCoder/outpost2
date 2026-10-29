using OutPost2.Infrastructure.Calendar;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Calendar;

/// <summary>
/// OP2-75. Everything here is about timezones, because on a calendar that is where all
/// the bugs are. A meeting booked an hour out is not a cosmetic failure — somebody sits
/// in a room alone, and nothing in the transcript shows anything went wrong.
/// </summary>
public class CalendarTimeTests
{
    /// <summary>
    /// Denver: well west of the server, and on a DST schedule that differs from Europe's,
    /// so a test that passes here is not passing by accident of a shared offset.
    /// </summary>
    private static TimeZoneInfo Denver => Find("America/Denver", "Mountain Standard Time");

    private static TimeZoneInfo London => Find("Europe/London", "GMT Standard Time");

    private static TimeZoneInfo Find(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    // ── reading what the user said ───────────────────────────────────────────

    [Theory]
    [InlineData("2026-11-03T14:00")]
    [InlineData("2026-11-03T14:00:00")]
    [InlineData("2026-11-03 14:00")]
    public void A_spoken_time_is_read_as_wall_clock_with_no_offset(string said)
    {
        Assert.True(CalendarTools.TryParseLocal(said, out var local));

        Assert.Equal(new DateTime(2026, 11, 3, 14, 0, 0), local);

        // Unspecified is the point. The moment this carries a Kind, something downstream
        // will helpfully convert it, and "two o'clock" becomes two o'clock somewhere else.
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
    }

    [Fact]
    public void A_date_with_no_time_is_midnight_that_day()
    {
        Assert.True(CalendarTools.TryParseLocal("2026-11-03", out var local));
        Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0), local);
    }

    [Fact]
    public void An_offset_supplied_by_the_caller_does_not_move_the_clock()
    {
        // A model that appends "Z" has guessed at the user's zone. The digits are what the
        // user actually said; the offset is the invented part. Keeping the digits and
        // discarding the guess is the behaviour that cannot silently shift a meeting.
        Assert.True(CalendarTools.TryParseLocal("2026-11-03T14:00:00Z", out var zulu));
        Assert.Equal(14, zulu.Hour);
        Assert.Equal(DateTimeKind.Unspecified, zulu.Kind);

        Assert.True(CalendarTools.TryParseLocal("2026-11-03T14:00:00-07:00", out var offset));
        Assert.Equal(14, offset.Hour);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tomorrow at two")]
    [InlineData("half past")]
    public void Anything_unreadable_is_refused_rather_than_guessed(string said)
    {
        Assert.False(CalendarTools.TryParseLocal(said, out _));
    }

    // ── "today" is the mailbox's today ───────────────────────────────────────

    [Fact]
    public void Today_is_computed_in_the_mailbox_zone_not_the_servers()
    {
        // The regression this guards: a container running UTC answering "what's on today"
        // for a Denver mailbox. At 00:30 UTC it is still the previous evening in Denver,
        // so a server-side "today" would skip the whole of the user's actual day.
        var (from, to) = CalendarTools.Window(Denver, null, days: 1);

        var localStart = TimeZoneInfo.ConvertTime(from, Denver);
        Assert.Equal(TimeSpan.Zero, localStart.TimeOfDay);

        var nowThere = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Denver);
        Assert.Equal(nowThere.Date, localStart.Date);

        Assert.Equal(TimeSpan.FromDays(1), to - from);
    }

    [Fact]
    public void A_named_day_starts_at_midnight_in_the_mailbox_zone()
    {
        var (from, to) = CalendarTools.Window(Denver, "2026-11-03", days: 1);

        Assert.Equal(new DateTime(2026, 11, 3), TimeZoneInfo.ConvertTime(from, Denver).Date);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(from, Denver).TimeOfDay);
        Assert.Equal(new DateTime(2026, 11, 4), TimeZoneInfo.ConvertTime(to, Denver).Date);
    }

    [Fact]
    public void A_week_that_crosses_the_clock_change_is_still_seven_days_long()
    {
        // The UK moves on 25 October 2026. Counting a week as 7 × 24 hours loses or gains
        // an hour at the far end, which drops a Sunday-evening event or picks up a Monday
        // one. The window is built from local dates for exactly that reason.
        var (from, to) = CalendarTools.Window(London, "2026-10-22", days: 7);

        Assert.Equal(new DateTime(2026, 10, 29), TimeZoneInfo.ConvertTime(to, London).Date);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(to, London).TimeOfDay);

        // And it really did cross the boundary: 7 local days, 169 actual hours.
        Assert.Equal(TimeSpan.FromHours(169), to - from);
    }

    [Fact]
    public void The_window_is_clamped_rather_than_letting_a_silly_number_through()
    {
        // Not a guess at intent — a month of calendar read aloud is already far past
        // useful, and asking Graph for a decade is how a tool call times out.
        var (from, to) = CalendarTools.Window(London, "2026-01-01", days: 31);
        Assert.Equal(31, (to - from).Days);
    }

    // ── what Graph gives back ────────────────────────────────────────────────

    [Fact]
    public void A_naive_graph_time_is_resolved_in_the_zone_it_names()
    {
        // Graph returns the zone asked for via Prefer, as a naive string. 14:00 in Denver
        // in November is UTC-7.
        var t = new GraphTime("2026-11-03T14:00:00.0000000", "Mountain Standard Time");
        var resolved = t.Resolve(Denver);

        Assert.Equal(14, resolved.Hour);
        Assert.Equal(TimeSpan.FromHours(-7), resolved.Offset);
        Assert.Equal(new DateTime(2026, 11, 3, 21, 0, 0), resolved.UtcDateTime);
    }

    [Fact]
    public void The_summer_offset_is_used_for_a_summer_date()
    {
        // Same mailbox, same zone, July: UTC-6. A fixed offset captured once would put
        // every summer meeting an hour out, and only an hour, which is the error least
        // likely to be noticed before somebody misses something.
        var t = new GraphTime("2026-07-03T14:00:00.0000000", "Mountain Standard Time");
        Assert.Equal(TimeSpan.FromHours(-6), t.Resolve(Denver).Offset);
    }

    [Fact]
    public void Graph_saying_UTC_is_believed_over_the_zone_we_asked_for()
    {
        // The Prefer header is honoured on a best-effort basis: an unrecognised zone name
        // comes back as UTC with no error. Trusting the request rather than the response
        // would reinterpret a UTC time as local and shift every event by the offset.
        var t = new GraphTime("2026-11-03T21:00:00.0000000", "UTC");
        var resolved = t.Resolve(Denver);

        Assert.Equal(TimeSpan.Zero, resolved.Offset);
        Assert.Equal(new DateTime(2026, 11, 3, 21, 0, 0), resolved.UtcDateTime);
    }

    [Fact]
    public void A_missing_time_resolves_to_default_rather_than_throwing()
    {
        // A malformed event must not take down a whole listing. It reads as the epoch,
        // which is obviously wrong to anyone looking, rather than as a plausible time.
        Assert.Equal(default, new GraphTime(null, "UTC").Resolve(TimeZoneInfo.Utc));
        Assert.Equal(default, new GraphTime("not a date", "UTC").Resolve(TimeZoneInfo.Utc));
    }
}
