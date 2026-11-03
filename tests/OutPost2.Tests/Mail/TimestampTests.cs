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
        Assert.Equal("30 Oct 2025", Timestamps.Ago(old, Arrived));
        Assert.Equal("1 Oct", Timestamps.Ago(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), Arrived));
    }
}
