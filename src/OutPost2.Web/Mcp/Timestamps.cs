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
    /// </summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var d = now - when;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} minutes ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} hour{((int)d.TotalHours == 1 ? "" : "s")} ago";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";

        // The year, so an old message cannot be mistaken for one from this year.
        return when.ToString(when.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string Ago(DateTimeOffset when) => Ago(when, DateTimeOffset.UtcNow);
}
