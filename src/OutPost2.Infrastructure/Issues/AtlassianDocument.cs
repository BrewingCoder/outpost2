using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OutPost2.Infrastructure.Issues;

/// <summary>
/// Atlassian Document Format, both ways (OP2-82). Jira's v3 API gives every description,
/// comment and rich-text custom field as an ADF tree rather than a string, and wants one
/// back on every write. v2 would take and give wiki markup instead, which reads worse and
/// turns a plain comment containing <c>*</c> or <c>{code}</c> into formatting.
/// <para>
/// Reading keeps the text and the shape a listener needs — paragraphs, list items, a
/// mention's name, a link's address — and drops presentation. Writing is plain paragraphs
/// and line breaks: what Outpost is handed is already plain text, and inventing structure
/// for it would be guessing.
/// </para>
/// </summary>
public static partial class AtlassianDocument
{
    /// <summary>Plain text for an ADF node. A bare string (v2, or a plain-text field) is returned as it is.</summary>
    public static string? ToText(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        if (e.ValueKind != JsonValueKind.Object) return null;

        var sb = new StringBuilder();
        Walk(e, sb, prefix: "");
        var text = Blank().Replace(sb.ToString().Replace("\r\n", "\n"), "\n\n").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>True for an object that is an ADF document rather than some other field value.</summary>
    public static bool IsDocument(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "doc";

    private static string Attr(JsonElement node, string name) =>
        node.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object
        && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static IEnumerable<JsonElement> Children(JsonElement node) =>
        node.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray()
            : [];

    private static void Walk(JsonElement node, StringBuilder sb, string prefix)
    {
        var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        switch (type)
        {
            case "text":
                sb.Append(node.TryGetProperty("text", out var tx) ? tx.GetString() : "");
                return;

            case "hardBreak":
                sb.Append('\n').Append(prefix);
                return;

            case "mention":
                sb.Append(Attr(node, "text") is { Length: > 0 } who ? who : "@someone");
                return;

            case "emoji":
                sb.Append(Attr(node, "text") is { Length: > 0 } em ? em : Attr(node, "shortName"));
                return;

            case "status":
                sb.Append('[').Append(Attr(node, "text")).Append(']');
                return;

            case "inlineCard" or "blockCard" or "embedCard":
                sb.Append(Attr(node, "url"));
                if (type != "inlineCard") sb.Append("\n\n");
                return;

            case "date":
                // Epoch milliseconds, as a string.
                sb.Append(long.TryParse(Attr(node, "timestamp"), out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                    : "");
                return;

            case "rule":
                sb.Append("---\n\n");
                return;

            case "media":
                sb.Append(Attr(node, "alt") is { Length: > 0 } alt ? $"[attachment: {alt}]" : "[attachment]");
                return;

            case "bulletList" or "orderedList":
            {
                var n = 1;
                foreach (var item in Children(node))
                {
                    var marker = type == "orderedList" ? $"{n++}. " : "- ";
                    sb.Append(prefix).Append(marker);
                    var inner = new StringBuilder();
                    foreach (var c in Children(item)) Walk(c, inner, prefix + "  ");
                    sb.Append(Blank().Replace(inner.ToString(), "\n").Trim()).Append('\n');
                }
                sb.Append('\n');
                return;
            }

            case "blockquote":
            {
                var inner = new StringBuilder();
                foreach (var c in Children(node)) Walk(c, inner, prefix);
                foreach (var line in inner.ToString().Trim().Split('\n')) sb.Append("> ").Append(line).Append('\n');
                sb.Append('\n');
                return;
            }

            case "tableRow":
                sb.Append(string.Join(" | ", Children(node).Select(cell =>
                {
                    var inner = new StringBuilder();
                    foreach (var c in Children(cell)) Walk(c, inner, "");
                    return Blank().Replace(inner.ToString(), " ").Trim().Replace("\n", " ");
                }))).Append('\n');
                return;

            case "paragraph" or "heading" or "codeBlock":
                foreach (var c in Children(node)) Walk(c, sb, prefix);
                sb.Append("\n\n");
                return;

            default:
                // doc, panel, expand, table, mediaSingle, an extension, and anything newer
                // than this code: the text inside is still the text.
                foreach (var c in Children(node)) Walk(c, sb, prefix);
                if (type is "table" or "panel" or "expand" or "nestedExpand") sb.Append('\n');
                return;
        }
    }

    /// <summary>
    /// An ADF document holding <paramref name="text"/> as written: a blank line starts a new
    /// paragraph, a single line break stays a line break. Empty text nodes are left out —
    /// Jira rejects a document containing one.
    /// </summary>
    public static JsonObject FromText(string text)
    {
        var paragraphs = new JsonArray();

        foreach (var para in Regex.Split((text ?? "").Replace("\r\n", "\n").Trim(), @"\n\s*\n"))
        {
            var content = new JsonArray();
            var lines = para.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) content.Add(new JsonObject { ["type"] = "hardBreak" });
                if (lines[i].Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = lines[i] });
            }

            if (content.Count > 0)
                paragraphs.Add(new JsonObject { ["type"] = "paragraph", ["content"] = content });
        }

        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = paragraphs };
    }

    [GeneratedRegex(@"\n[ \t]*\n(\s*\n)*")]
    private static partial Regex Blank();
}
