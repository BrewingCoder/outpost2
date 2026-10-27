using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// Turns mail HTML into text a model can actually use (OP2-8).
/// <para>
/// This is not cosmetic. Every wasted token of markup is paid for twice — in context
/// budget and in voice-turn latency — and the baseline's habit of handing a raw HTML blob
/// to the model is the difference between "summarise my unread" working and timing out.
/// </para>
/// </summary>
public static partial class HtmlToText
{
    [GeneratedRegex(@"<(script|style|head|svg)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContentBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>Tracking pixels and spacer images: content-free, and common enough to matter.</summary>
    [GeneratedRegex(@"<img\b[^>]*?(width\s*=\s*[""']?[01][""']?|height\s*=\s*[""']?[01][""']?)[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrackingPixels();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Links();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"</(p|div|tr|li|h[1-6]|table|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t ​‌‍﻿]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();

    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var s = html;
        s = NonContentBlocks().Replace(s, " ");
        s = Comments().Replace(s, " ");
        s = TrackingPixels().Replace(s, " ");

        // Keep the destination of a link — "click here" with the URL stripped is actively
        // misleading, and a model asked "what did they link to?" has nothing to answer with.
        s = Links().Replace(s, m =>
        {
            var text = AnyTag().Replace(m.Groups[2].Value, "").Trim();
            var href = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(text)) return href;
            if (string.Equals(text, href, StringComparison.OrdinalIgnoreCase)) return text;
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return text;

            // Parentheses, NOT angle brackets: the generic <[^>]+> tag stripper below runs
            // after this and would swallow "<https://...>" as though it were a tag,
            // silently discarding every link destination. Parentheses also read better
            // when a summary is spoken aloud.
            return $"{text} ({href})";
        });

        s = ListItems().Replace(s, "\n- ");
        s = LineBreaks().Replace(s, "\n");
        s = BlockEnds().Replace(s, "\n");
        s = AnyTag().Replace(s, "");

        s = WebUtility.HtmlDecode(s);

        // Collapse runs of spaces AND the zero-width characters bulk senders use for
        // spacing, which otherwise survive as invisible tokens.
        s = HorizontalWhitespace().Replace(s, " ");
        s = string.Join("\n", s.Split('\n').Select(l => l.Trim()));
        s = ExcessBlankLines().Replace(s, "\n\n");

        return s.Trim();
    }

    private static readonly string[] SignatureMarkers =
    [
        "-- ",
        "sent from my ",
        "this email and any attachments",
        "this e-mail and any attachments",
        "the information contained in this",
        "confidentiality notice",
        "disclaimer:",
        "please consider the environment",
    ];

    /// <summary>
    /// Trims a trailing signature or legal disclaimer.
    /// <para>
    /// Conservative on purpose: it only cuts in the last third of the message, and only
    /// when what follows is short. Over-eager trimming silently deletes real content,
    /// which is far worse than leaving a disclaimer in — the user can ignore boilerplate,
    /// but cannot recover a sentence we removed.
    /// </para>
    /// </summary>
    public static string TrimSignature(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var lines = text.Split('\n');
        if (lines.Length < 6) return text;

        var floor = (int)(lines.Length * 0.66);

        for (var i = floor; i < lines.Length; i++)
        {
            var probe = lines[i].Trim().ToLowerInvariant();
            if (probe.Length == 0) continue;

            if (SignatureMarkers.Any(m => probe.StartsWith(m, StringComparison.Ordinal)))
            {
                var remaining = lines.Length - i;
                if (remaining <= 12) return string.Join("\n", lines[..i]).TrimEnd();
            }
        }

        return text;
    }

    /// <summary>
    /// Clamps text to a budget, cutting on a line boundary and saying it was cut.
    /// Silent truncation would let a model confidently summarise half a message.
    /// </summary>
    public static string Clamp(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        var cut = text.LastIndexOf('\n', Math.Min(maxChars, text.Length - 1));
        if (cut < maxChars / 2) cut = maxChars;

        return new StringBuilder(text[..cut].TrimEnd())
            .Append("\n\n[… truncated, ")
            .Append(text.Length - cut)
            .Append(" more characters — ask for the full message if you need it]")
            .ToString();
    }
}
