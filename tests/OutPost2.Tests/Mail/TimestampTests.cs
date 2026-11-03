using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// Exact times beside the friendly ones (OP2-264). The report: "2 hours ago" covered an
/// actual 2h48m, and the receipt time against a compliance deadline could not be had from
/// any tool.
/// </summary>
public sealed class TimestampTests
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static readonly DateTimeOffset Arrived = new(2026, 11, 3, 5, 44, 12, TimeSpan.Zero);

    [Fact]
    public void Exact_is_iso_8601_to_the_second_with_the_mailbox_offset()
    {
        Assert.Equal("2026-11-03T00:44:12-05:00", Timestamps.Exact(Arrived, Eastern));
    }

    [Fact]
    public void Exact_in_utc_still_writes_the_offset_out()
    {
        // A missing zone falls back to UTC; the value must still say which instant it is.
        Assert.Equal("2026-11-03T05:44:12+00:00", Timestamps.Exact(Arrived, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Exact_follows_daylight_saving_on_the_day_it_applied()
    {
        var summer = new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero);
        Assert.Equal("2026-07-01T12:00:00-04:00", Timestamps.Exact(summer, Eastern));
    }

    [Fact]
    public void Short_is_to_the_minute_with_the_offset()
    {
        Assert.Equal("2026-11-03 00:44 -05:00", Timestamps.Short(Arrived, Eastern));
    }

    [Fact]
    public void The_relative_phrase_is_kept_and_the_exact_value_disambiguates_it()
    {
        // The reported case: 2h48m reads as "2 hours ago". That stays — it is the friendly
        // form — and is no longer the only thing said.
        var now = Arrived + TimeSpan.FromMinutes(168);
        Assert.Equal("2 hours ago", Timestamps.Ago(Arrived, now));
    }

    [Fact]
    public void An_old_message_from_another_year_carries_its_year()
    {
        var old = new DateTimeOffset(2025, 10, 30, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal("30 Oct 2025", Timestamps.Ago(old, Arrived, Eastern));
        Assert.Equal("1 Oct",
            Timestamps.Ago(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), Arrived, Eastern));
    }

    // ── review findings ──────────────────────────────────────────────────────

    [Fact]
    public void The_date_is_the_date_in_the_mailbox_zone_not_in_utc()
    {
        // Graph returns receivedDateTime at offset zero, so formatting it directly printed
        // the UTC day. Received 20:30 Eastern is 00:30Z the next day, and the line read
        // "24 Oct" beside an exact value saying the 23rd.
        var evening = new DateTimeOffset(2026, 10, 24, 0, 30, 0, TimeSpan.Zero);
        var tenDaysLater = evening + TimeSpan.FromDays(10);

        Assert.Equal("23 Oct", Timestamps.Ago(evening, tenDaysLater, Eastern));
        Assert.StartsWith("2026-10-23", Timestamps.Exact(evening, Eastern));
    }

    [Fact]
    public void A_message_from_last_year_in_local_terms_still_carries_its_year()
    {
        // The worse half of the same bug. 2025-12-31 20:00 EST is 2026-01-01Z, so the year
        // compared equal to this year and the suffix — added to stop exactly this — was
        // dropped.
        var newYearsEve = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("31 Dec 2025", Timestamps.Ago(newYearsEve, later, Eastern));
    }

    [Fact]
    public void Without_a_zone_it_still_answers_in_utc_rather_than_throwing()
    {
        var old = new DateTimeOffset(2025, 10, 30, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal("30 Oct 2025", Timestamps.Ago(old, Arrived));
        Assert.Equal("30 Oct 2025", Timestamps.Ago(old, Arrived, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_zone_that_could_not_be_had_is_labelled_and_a_real_utc_mailbox_is_not()
    {
        // An unlabelled UTC time on the line being checked against a local deadline is
        // worse than an obviously missing one — so the fallback is labelled. But the label
        // comes from the lookup FAILING, not from the zone being UTC: a mailbox genuinely
        // set to UTC is not an unknown one, and saying so would be a plain lie.
        Assert.Contains("mailbox zone unavailable",
            MailTools.Unknown(new MailTools.MailboxZone(TimeZoneInfo.Utc, Known: false)));

        Assert.Equal("", MailTools.Unknown(new MailTools.MailboxZone(TimeZoneInfo.Utc, Known: true)));
        Assert.Equal("", MailTools.Unknown(new MailTools.MailboxZone(Eastern, Known: true)));
    }

    [Fact]
    public void Sent_and_received_milliseconds_apart_render_identically()
    {
        // Which is why read_mail compares the rendered strings rather than the instants:
        // Graph keeps milliseconds, this prints to the second, so an inequality on the
        // instants printed the same timestamp twice.
        var received = new DateTimeOffset(2026, 11, 3, 5, 44, 12, 400, TimeSpan.Zero);
        var sent = new DateTimeOffset(2026, 11, 3, 5, 44, 12, 40, TimeSpan.Zero);

        Assert.NotEqual(sent, received);
        Assert.Equal(Timestamps.Exact(received, Eastern), Timestamps.Exact(sent, Eastern));
    }
}
