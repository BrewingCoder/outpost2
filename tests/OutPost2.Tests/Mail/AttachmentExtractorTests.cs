using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

public class AttachmentExtractorTests
{
    private readonly AttachmentExtractor _x = new(NullLogger<AttachmentExtractor>.Instance);

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("notes.docx", true)]
    [InlineData("budget.xlsx", true)]
    [InlineData("deck.pptx", true)]
    [InlineData("data.csv", true)]
    [InlineData("readme.md", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("archive.zip", false)]
    [InlineData("installer.exe", false)]
    public void Knows_what_it_can_read(string name, bool expected) =>
        Assert.Equal(expected, _x.CanExtract(name, null));

    [Fact]
    public void Accepts_anything_with_a_text_content_type()
    {
        // An unknown extension with text/* is still readable, and refusing it would be
        // needlessly strict.
        Assert.True(_x.CanExtract("weird.dat", "text/plain"));
    }

    [Fact]
    public async Task Reads_plain_text()
    {
        var text = await _x.ExtractAsync("notes.txt",
            new MemoryStream(Encoding.UTF8.GetBytes("line one\nline two")));

        Assert.Contains("line one", text);
        Assert.Contains("line two", text);
    }

    [Fact]
    public async Task Converts_html_rather_than_returning_markup()
    {
        var text = await _x.ExtractAsync("page.html",
            new MemoryStream(Encoding.UTF8.GetBytes("<html><body><p>Hello</p><script>x()</script></body></html>")));

        Assert.Equal("Hello", text);
        Assert.DoesNotContain("script", text);
    }

    [Fact]
    public async Task Reads_a_real_docx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Invoice 4471"))),
                new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Total due: 1,240.00")))));
        }

        ms.Position = 0;
        var text = await _x.ExtractAsync("invoice.docx", ms);

        Assert.Contains("Invoice 4471", text);
        Assert.Contains("1,240.00", text);
    }

    [Fact]
    public async Task Reads_a_real_xlsx_including_shared_strings()
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();

            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            wsPart.Worksheet = new Worksheet(data);

            // Inline strings exercise the non-shared path; shared strings are the one
            // that silently yields numbers if the table is not resolved.
            var sharedPart = wbPart.AddNewPart<SharedStringTablePart>();
            sharedPart.SharedStringTable = new SharedStringTable(
                new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text("Widgets")));

            data.Append(new Row(
                new Cell { CellValue = new CellValue("0"), DataType = CellValues.SharedString },
                new Cell { CellValue = new CellValue("42"), DataType = CellValues.Number }));

            wbPart.Workbook.AppendChild(new Sheets()).AppendChild(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Q1",
            });
        }

        ms.Position = 0;
        var text = await _x.ExtractAsync("budget.xlsx", ms);

        Assert.Contains("Q1", text);
        Assert.Contains("Widgets", text);   // resolved from the shared string table
        Assert.Contains("42", text);
    }

    [Fact]
    public async Task A_corrupt_file_explains_itself_instead_of_throwing()
    {
        // Receiving a damaged or password-protected attachment is normal. Failing the
        // whole conversation turn over one would not be.
        var text = await _x.ExtractAsync("broken.docx",
            new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip archive")));

        Assert.Contains("Could not read", text);
        Assert.Contains("broken.docx", text);
    }

    [Fact]
    public async Task One_unreadable_sheet_does_not_cost_the_whole_workbook()
    {
        // OP2-145. A worksheet part whose XML never got written reads back with a null
        // Worksheet. The old code dereferenced it, the catch-all in ExtractAsync caught
        // the NullReferenceException, and the user was told the file was "damaged or
        // password-protected" — losing the sheets that were perfectly readable and
        // blaming the sender for a null reference in our own code.
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var sheets = wbPart.Workbook.AppendChild(new Sheets());

            var good = wbPart.AddNewPart<WorksheetPart>();
            good.Worksheet = new Worksheet(new SheetData(new Row(
                new Cell { CellValue = new CellValue("1870"), DataType = CellValues.Number })));
            sheets.AppendChild(new Sheet
            {
                Id = wbPart.GetIdOfPart(good), SheetId = 1, Name = "Readable",
            });

            // Added and referenced, but never given a Worksheet — so the part exists in
            // the package with no root element.
            var empty = wbPart.AddNewPart<WorksheetPart>();
            sheets.AppendChild(new Sheet
            {
                Id = wbPart.GetIdOfPart(empty), SheetId = 2, Name = "Blank",
            });
        }

        ms.Position = 0;
        var text = await _x.ExtractAsync("mixed.xlsx", ms);

        Assert.DoesNotContain("Could not read", text);
        Assert.Contains("Readable", text);
        Assert.Contains("1870", text);
    }

    [Fact]
    public async Task An_empty_pdf_says_so_rather_than_returning_nothing()
    {
        // A scanned PDF is all images. Returning empty would let a model conclude the
        // document was blank, which is worse than saying it cannot be read.
        var text = await _x.ExtractAsync("scan.pdf", new MemoryStream([0x25, 0x50, 0x44, 0x46]));

        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
