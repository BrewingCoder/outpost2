using System.IO.Compression;

namespace OutPost2.Core.Files;

/// <summary>
/// Gives a staged file the extension it is missing, from what its bytes actually are
/// (OP2-277).
/// <para>
/// The staged file's name becomes the attachment's filename verbatim. A caller following
/// the old example — "quarterly report" — produced a PDF attachment with no extension,
/// which a recipient's machine cannot open by double-clicking and which looks corrupt
/// rather than misnamed. Nothing said so at stage time or at send time.
/// </para>
/// <para>
/// The bytes are the authority, not the request's <c>Content-Type</c>: the example
/// <c>curl --data-binary</c> sends <c>application/x-www-form-urlencoded</c> whatever the
/// file is. So the common types are recognised from their signatures, and the declared
/// type is used only when it is specific.
/// </para>
/// </summary>
public static class FileTypes
{
    public sealed record Kind(string Extension, string ContentType);

    public static readonly Kind Pdf = new(".pdf", "application/pdf");
    public static readonly Kind Png = new(".png", "image/png");
    public static readonly Kind Jpeg = new(".jpg", "image/jpeg");
    public static readonly Kind Gif = new(".gif", "image/gif");
    public static readonly Kind Zip = new(".zip", "application/zip");
    public static readonly Kind Docx = new(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    public static readonly Kind Xlsx = new(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    public static readonly Kind Pptx = new(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation");

    /// <summary>What the types this recognises are, for saying so to a caller.</summary>
    public const string Recognised = "PDF, PNG, JPEG, GIF, Word, Excel, PowerPoint and ZIP";

    /// <summary>A declared type that says nothing about the file, and so is no basis for an extension.</summary>
    public static bool IsGeneric(string? contentType)
    {
        var t = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return t is "" or "application/octet-stream" or "application/x-www-form-urlencoded"
            or "multipart/form-data" or "binary/octet-stream";
    }

    /// <summary>
    /// Whether the name ends in something a machine will read as a file type. Not just "has
    /// a dot": "report v1.2" and "Mr. Smith notes" both have one, and both would go out as
    /// files nothing can open. A real extension is short, letters and digits only, and has
    /// at least one letter — ".pdf", ".docx", ".7z", ".mp3" — so ".2" and ". Smith notes"
    /// are not.
    /// </summary>
    public static bool HasExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName.TrimEnd());
        if (ext.Length < 2 || ext.Length > 11) return false;
        var body = ext.AsSpan(1);
        foreach (var ch in body)
            if (!char.IsAsciiLetterOrDigit(ch)) return false;
        foreach (var ch in body)
            if (char.IsAsciiLetter(ch)) return true;
        return false;
    }

    /// <summary>
    /// What a seekable stream's bytes are, or null. Leaves the position where it found it.
    /// </summary>
    public static Kind? Sniff(Stream seekable)
    {
        var start = seekable.Position;
        try
        {
            Span<byte> head = stackalloc byte[8];
            var read = seekable.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var h = head[..read];

            if (h.StartsWith("%PDF-"u8)) return Pdf;
            if (h.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return Png;
            if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return Jpeg;
            if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8)) return Gif;

            if (h.StartsWith((ReadOnlySpan<byte>)[(byte)'P', (byte)'K', 0x03, 0x04]))
            {
                seekable.Position = start;
                return Office(seekable) ?? Zip;
            }

            return null;
        }
        finally
        {
            seekable.Position = start;
        }
    }

    /// <summary>
    /// An Office Open XML document is a ZIP, and which one is told by the folder its main
    /// part lives in. Read from the central directory, so it costs a seek, not a read of
    /// the whole file. A ZIP that will not open is just a ZIP.
    /// </summary>
    private static Kind? Office(Stream seekable)
    {
        try
        {
            using var zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            if (!names.Contains("[Content_Types].xml")) return null;
            if (names.Any(n => n.StartsWith("word/", StringComparison.Ordinal))) return Docx;
            if (names.Any(n => n.StartsWith("xl/", StringComparison.Ordinal))) return Xlsx;
            if (names.Any(n => n.StartsWith("ppt/", StringComparison.Ordinal))) return Pptx;
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>From a declared type, when it is specific enough to trust.</summary>
    public static Kind? FromContentType(string? contentType) =>
        (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "application/pdf" => Pdf,
            "image/png" => Png,
            "image/jpeg" => Jpeg,
            "image/gif" => Gif,
            "application/zip" => Zip,
            "text/plain" => new(".txt", "text/plain"),
            "text/csv" => new(".csv", "text/csv"),
            "text/html" => new(".html", "text/html"),
            "application/json" => new(".json", "application/json"),
            _ => null,
        };

    /// <summary>
    /// The filename and content type a staged file should go out with: the extension added
    /// when it has none and the bytes say what it is, and a generic declared type replaced
    /// by the real one. A name that already has an extension keeps it — the caller said
    /// what it is, and second-guessing a deliberate name is worse than trusting it.
    /// </summary>
    public static (string FileName, string ContentType) Complete(
        string fileName, string contentType, Stream seekable)
    {
        var sniffed = Sniff(seekable);
        var kind = sniffed ?? (IsGeneric(contentType) ? null : FromContentType(contentType));

        var name = HasExtension(fileName) || kind is null
            ? fileName
            : fileName.TrimEnd('.', ' ') + kind.Extension;

        var type = IsGeneric(contentType) && kind is not null ? kind.ContentType : contentType;

        return (name, type);
    }
}
