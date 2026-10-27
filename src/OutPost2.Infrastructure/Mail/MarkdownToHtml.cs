using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// Renders model-authored Markdown to a small, safe subset of HTML (OP2-8).
/// <para>
/// Built as an allow-list rather than a sanitiser: everything is HTML-escaped FIRST, then
/// a fixed set of tags is introduced by us. Nothing the model writes can become markup,
/// so there is no sanitiser bypass to get wrong — which matters, because this HTML goes
/// out over the user's own identity.
/// </para>
/// </summary>
public static partial class MarkdownToHtml
{
    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)] private static partial Regex Bold();
    [GeneratedRegex(@"(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])", RegexOptions.Singleline)] private static partial Regex Italic();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex Code();
    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"^\s*[-*+]\s+(.*)$", RegexOptions.Multiline)] private static partial Regex Bullet();

    public static string Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "<p></p>";

        // Escape everything up front. After this point the only '<' in the string is one
        // we put there ourselves.
        var s = WebUtility.HtmlEncode(markdown.Replace("\r\n", "\n"));

        s = Code().Replace(s, m => $"<code>{m.Groups[1].Value}</code>");
        s = Bold().Replace(s, m => $"<strong>{m.Groups[1].Value}</strong>");
        s = Italic().Replace(s, m => $"<em>{m.Groups[1].Value}</em>");

        // Only http(s) links survive, and the URL is re-encoded. javascript: and data:
        // cannot appear because the pattern will not match them.
        s = Link().Replace(s, m =>
            $"<a href=\"{WebUtility.HtmlEncode(WebUtility.HtmlDecode(m.Groups[2].Value))}\">{m.Groups[1].Value}</a>");

        var html = new StringBuilder("<html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\">");
        var inList = false;

        foreach (var raw in s.Split('\n'))
        {
            var bullet = Bullet().Match(raw);
            if (bullet.Success)
            {
                if (!inList) { html.Append("<ul>"); inList = true; }
                html.Append("<li>").Append(bullet.Groups[1].Value.Trim()).Append("</li>");
                continue;
            }

            if (inList) { html.Append("</ul>"); inList = false; }

            var line = raw.Trim();
            html.Append(line.Length == 0 ? "<br>" : $"<p style=\"margin:0 0 .6em\">{line}</p>");
        }

        if (inList) html.Append("</ul>");
        return html.Append("</body></html>").ToString();
    }
}
