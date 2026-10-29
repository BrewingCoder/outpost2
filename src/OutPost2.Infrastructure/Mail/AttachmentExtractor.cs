using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace OutPost2.Infrastructure.Mail;

/// <summary>
/// Turns an attachment into text (flow C of OP2-9).
/// <para>
/// When the model is asked "what does the invoice say?", it wants the <b>content</b>, not
/// the bytes. Handing it base64 would burn context for no benefit and tell it nothing.
/// Extraction happens server-side and only text crosses the MCP boundary.
/// </para>
/// </summary>
public interface IAttachmentExtractor
{
    bool CanExtract(string fileName, string? contentType);
    Task<string> ExtractAsync(string fileName, Stream content, CancellationToken ct = default);
}

/// <remarks>
/// The null checks in the per-format readers are load-bearing (OP2-145). These files
/// arrive by email from outside the organisation, so a malformed one is not an edge case
/// — it is the normal shape of spam, and it is the one input to this server an attacker
/// fully controls. The Open XML SDK hands back a part whose body is null for several
/// kinds of truncation.
/// <para>
/// ExtractAsync's catch-all means an unguarded dereference was never a 500. It was worse
/// in a quieter way: the user was told the file was "password-protected or damaged" when
/// what actually happened was a null reference in our own code, and a whole workbook was
/// abandoned because one sheet part failed to parse. Guarding each dereference reads the
/// sheets that are intact and reserves the catch-all for files that really are broken.
/// </para>
/// </remarks>
public sealed class AttachmentExtractor(ILogger<AttachmentExtractor> log) : IAttachmentExtractor
{
    private static readonly HashSet<string> Extractable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".txt", ".md", ".csv", ".json", ".xml", ".log", ".yml", ".yaml", ".html", ".htm",
    };

    public bool CanExtract(string fileName, string? contentType) =>
        Extractable.Contains(Path.GetExtension(fileName)) ||
        (contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false);

    public async Task<string> ExtractAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        // Buffer to a seekable MemoryStream: the OpenXML and PDF readers both seek, and a
        // network stream will not.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        try
        {
            return ext switch
            {
                ".pdf" => ExtractPdf(buffer),
                ".docx" => ExtractWord(buffer),
                ".xlsx" => ExtractExcel(buffer),
                ".pptx" => ExtractPowerPoint(buffer),
                ".html" or ".htm" => HtmlToText.Convert(Encoding.UTF8.GetString(buffer.ToArray())),
                _ => Encoding.UTF8.GetString(buffer.ToArray()),
            };
        }
        catch (Exception ex)
        {
            // A corrupt or password-protected file is a normal thing to receive. Say so
            // rather than failing the whole conversation turn.
            log.LogWarning(ex, "Could not extract text from {FileName}.", fileName);
            return $"[Could not read {fileName}: {ex.Message}. It may be password-protected or damaged.]";
        }
    }

    private static string ExtractPdf(Stream s)
    {
        using var pdf = PdfDocument.Open(s);
        var sb = new StringBuilder();

        foreach (var page in pdf.GetPages())
        {
            var text = page.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            sb.AppendLine($"--- page {page.Number} ---");
            sb.AppendLine(text);
        }

        // A scanned PDF is all images and yields nothing. Saying so is far better than
        // returning empty and letting the model conclude the document was blank.
        return sb.Length == 0
            ? "[This PDF contains no extractable text — it is probably a scan or images only.]"
            : sb.ToString().TrimEnd();
    }

    private static string ExtractWord(Stream s)
    {
        using var doc = WordprocessingDocument.Open(s, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "[Empty document.]";

        var sb = new StringBuilder();
        foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var text = p.InnerText;
            if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
        }

        return sb.Length == 0 ? "[No text found.]" : sb.ToString().TrimEnd();
    }

    private static string ExtractExcel(Stream s)
    {
        using var doc = SpreadsheetDocument.Open(s, false);
        var wb = doc.WorkbookPart;
        if (wb is null) return "[Empty workbook.]";

        // Shared strings are stored once and referenced by index; without resolving them
        // every text cell reads as a meaningless number.
        var shared = wb.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];

        var sb = new StringBuilder();

        foreach (var sheet in wb.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } relId) continue;
            if (wb.GetPartById(relId) is not WorksheetPart part) continue;

            sb.AppendLine($"--- sheet: {sheet.Name} ---");

            foreach (var row in part.Worksheet?.Descendants<Row>() ?? [])
            {
                var cells = row.Elements<Cell>().Select(c =>
                {
                    var raw = c.CellValue?.InnerText ?? "";
                    return c.DataType?.Value == CellValues.SharedString
                           && int.TryParse(raw, out var i) && i < shared.Length
                        ? shared[i] : raw;
                });

                var line = string.Join("\t", cells).TrimEnd('\t');
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
            }
        }

        return sb.Length == 0 ? "[No data found.]" : sb.ToString().TrimEnd();
    }

    private static string ExtractPowerPoint(Stream s)
    {
        using var doc = PresentationDocument.Open(s, false);
        var parts = doc.PresentationPart?.SlideParts.ToList() ?? [];

        var sb = new StringBuilder();
        var n = 1;

        foreach (var slide in parts)
        {
            sb.AppendLine($"--- slide {n++} ---");

            foreach (var t in slide.Slide?.Descendants<DocumentFormat.OpenXml.Drawing.Text>() ?? [])
                if (!string.IsNullOrWhiteSpace(t.Text)) sb.AppendLine(t.Text);
        }

        return sb.Length == 0 ? "[No text found.]" : sb.ToString().TrimEnd();
    }
}
