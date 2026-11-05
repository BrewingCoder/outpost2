using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OutPost2.Infrastructure.Teams;

/// <summary>
/// Slack's mrkdwn, reduced to text that can be read aloud (OP2-86).
/// <para>
/// What arrives is not what the person saw. A mention is <c>&lt;@U024BE7LH&gt;</c>, a
/// channel <c>&lt;#C024BE7LR|general&gt;</c>, a link <c>&lt;https://example.com|the doc&gt;</c>
/// and a broadcast <c>&lt;!here&gt;</c>, and the three characters Slack reserves for that
/// syntax — <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> — are escaped as HTML entities
/// everywhere else. Read raw, "<c>&lt;@U024BE7LH&gt;</c> can you look" is noise; rendered,
/// it is "@Darren can you look".
/// </para>
/// <para>
/// <c>*bold*</c>, <c>_italic_</c> and <c>~strike~</c> are left as they are: a model reads
/// them correctly, and stripping them would also strip the asterisks out of "2*3".
/// </para>
/// </summary>
public static partial class SlackText
{
    [GeneratedRegex(@"<([^<>]*)>")]
    private static partial Regex Token();

    [GeneratedRegex(@"<@([UW][A-Z0-9]+)(?:\|[^<>]*)?>")]
    private static partial Regex Mention();

    /// <summary>Every user id mentioned, so the names can be looked up before rendering.</summary>
    public static IEnumerable<string> MentionedUsers(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : Mention().Matches(text).Select(m => m.Groups[1].Value).Distinct();

    /// <param name="nameOf">The display name for a user id, or null when it is not known.</param>
    public static string Render(string? text, Func<string, string?> nameOf)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var rendered = Token().Replace(text, m => Replace(m.Groups[1].Value, nameOf));

        // Entities last, so an escaped "&lt;@U1&gt;" the sender literally typed stays text
        // rather than becoming a mention after decoding.
        return rendered.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    }

    private static string Replace(string inner, Func<string, string?> nameOf)
    {
        var bar = inner.IndexOf('|');
        var target = bar < 0 ? inner : inner[..bar];
        var label = bar < 0 ? null : inner[(bar + 1)..];

        if (target.StartsWith('@'))
        {
            var id = target[1..];
            return "@" + (nameOf(id) ?? NonEmpty(label) ?? "someone");
        }

        if (target.StartsWith('#'))
            return "#" + (NonEmpty(label) ?? "a channel");

        if (target.StartsWith('!'))
        {
            var special = target[1..];
            if (special is "here" or "channel" or "everyone") return "@" + special;

            // <!subteam^S123|@design>, <!date^1392734382^{date}|Feb 18th>: the label is
            // the readable form Slack itself would fall back to.
            return NonEmpty(label) ?? (special.StartsWith("subteam", StringComparison.Ordinal) ? "@a user group" : "");
        }

        // A link. "mailto:" and a URL whose label is the URL are said once, not twice.
        var url = target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? target[7..] : target;
        if (NonEmpty(label) is not { } text || text == url || text == target) return url;
        return $"{text} ({url})";
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// The escaping Slack requires on text it is sent. Without it a message saying "a &lt; b
    /// &gt; c" is parsed as a link, and "&lt;!channel&gt;" typed as words notifies everyone.
    /// </summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// A name on one line. A Slack display name is free text the PERSON chose, and a name
    /// substituted for a mention lands inside somebody else's message — so a name such as
    /// "Mallory\n[just now] You" would otherwise put a forged line from the user into the
    /// middle of a transcript. Flattened here, where the substitution happens; the tools
    /// scrub the sender again on the way out.
    /// </summary>
    public static string OneLine(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var sb = new StringBuilder(name.Length);
        var space = false;
        foreach (var ch in name)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)
                || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = sb.Length > 0;
                continue;
            }

            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }

        var one = sb.ToString();
        return one.Length <= 80 ? one : one[..80] + "…";
    }

    /// <summary>A Slack timestamp — seconds and microseconds, as a string — as a time.</summary>
    public static DateTimeOffset? Time(string? ts) =>
        decimal.TryParse(ts, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s) && s > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000m))
            : null;

    /// <summary>Compares two Slack timestamps. Null when either is missing.</summary>
    public static bool? After(string? ts, string? than)
    {
        if (!decimal.TryParse(ts, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a)) return null;
        if (!decimal.TryParse(than, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var b)) return null;
        return a > b;
    }
}
