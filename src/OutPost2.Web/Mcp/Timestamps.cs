using System.Globalization;

namespace OutPost2.Web.Mcp;

/// <summary>
/// Times as the mail tools say them: the friendly relative phrase AND the exact value
/// (OP2-264).
/// <para>
/// The relative phrase alone was not enough. It floors to the unit, so "2 hours ago" covers
/// anything from 2h00m to 2h59m, and a question like "did this land before the 17:00
/// deadline" cannot be answered from it at all. The only way out was asking a human to
/// open the mail client and read the header. So the exact instant is always printed next
/// to the phrase, in the mailbox's own zone and with the offset written out, because a
/// deadline comparison depends on the offset as much as on the clock time.
/// </para>
/// </summary>
public static class Timestamps
{
    /// <summary>
    /// ISO 8601 to the second with the offset, e.g. <c>2026-11-03T00:44:12-05:00</c>.
    /// For read_mail, where the envelope is the record.
    /// </summary>
    public static string Exact(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone)
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// The same instant to the minute, e.g. <c>2026-11-03 00:44 -05:00</c>. For listings,
    /// where a line per message has to stay short enough to read down.
    /// </summary>
    public static string Short(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone)
            .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// The friendly form, kept because it is what a person says out loud. Approximate by
    /// design — the exact value beside it is what to act on.
    /// <para>
    /// Past a week it becomes a date, and a date has to be stated in the mailbox's zone like
    /// every other date here. Graph hands <c>receivedDateTime</c> back at offset zero, so
    /// formatting <paramref name="when"/> directly prints the UTC day: a message received at
    /// 20:30 Eastern is 00:30 UTC the next day, and the line read "<c>24 Oct</c>" beside an
    /// exact value saying the 23rd. The year suffix had the same hole and in the worse
    /// direction — 2025-12-31 20:00 EST is 2026-01-01Z, so last year's message compared
    /// equal to this year and printed "1 Jan" with no year, which is precisely the
    /// ambiguity the suffix exists to remove.
    /// </para>
    /// </summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var d = now - when;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} minutes ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} hour{((int)d.TotalHours == 1 ? "" : "s")} ago";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";

        // Both sides of the comparison in the same zone, or "is this the current year" is
        // being asked of two different calendars.
        var local = TimeZoneInfo.ConvertTime(when, zone ?? TimeZoneInfo.Utc);
        var today = TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Utc);

        // The year, so an old message cannot be mistaken for one from this year.
        return local.ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy",
            CultureInfo.InvariantCulture);
    }

    public static string Ago(DateTimeOffset when, TimeZoneInfo? zone = null) =>
        Ago(when, DateTimeOffset.UtcNow, zone);

}
