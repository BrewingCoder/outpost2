using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Files;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Files;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Files;

/// <summary>
/// A staged file's filename is what recipients see (OP2-277). Following stage_file's own
/// example — "quarterly report" — produced a PDF attachment with no extension, which looks
/// corrupt to the recipient, and nothing said so at stage time or send time.
/// </summary>
public sealed class StagedFileNameTests
{
    private static MemoryStream Bytes(byte[] b) => new(b);
    private static MemoryStream Text(string s) => Bytes(Encoding.ASCII.GetBytes(s));

    private static MemoryStream Ooxml(string mainPart)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry(mainPart);
        }
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream PlainZip()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("notes.txt");
        ms.Position = 0;
        return ms;
    }

    // ── recognising the bytes ────────────────────────────────────────────────

    public static TheoryData<string, Func<MemoryStream>, string> Signatures => new()
    {
        { "pdf", () => Text("%PDF-1.7\n..."), ".pdf" },
        { "png", () => Bytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]), ".png" },
        { "jpeg", () => Bytes([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0]), ".jpg" },
        { "gif", () => Text("GIF89a......"), ".gif" },
        { "docx", () => Ooxml("word/document.xml"), ".docx" },
        { "xlsx", () => Ooxml("xl/workbook.xml"), ".xlsx" },
        { "pptx", () => Ooxml("ppt/presentation.xml"), ".pptx" },
        { "zip", PlainZip, ".zip" },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void The_common_types_are_recognised_from_their_bytes(string _, Func<MemoryStream> make, string ext)
    {
        using var s = make();
        Assert.Equal(ext, FileTypes.Sniff(s)?.Extension);
        Assert.Equal(0, s.Position);   // left where it was found, so the upload reads it all
    }

    [Fact]
    public void Unrecognised_bytes_are_not_guessed_at()
    {
        using var s = Text("just some text");
        Assert.Null(FileTypes.Sniff(s));

        using var empty = Bytes([]);
        Assert.Null(FileTypes.Sniff(empty));

        // Starts like a ZIP and is not one: a ZIP, not a crash.
        using var torn = Bytes([0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        Assert.Equal(".zip", FileTypes.Sniff(torn)?.Extension);
    }

    // ── completing the name ──────────────────────────────────────────────────

    [Fact]
    public void The_reported_case_gets_its_extension_and_its_real_type()
    {
        // curl --data-binary's default content type, which is what the example sends.
        using var pdf = Text("%PDF-1.4");
        var (name, type) = FileTypes.Complete("quarterly report", "application/x-www-form-urlencoded", pdf);

        Assert.Equal("quarterly report.pdf", name);
        Assert.Equal("application/pdf", type);
    }

    [Fact]
    public void A_name_that_already_has_an_extension_is_kept_as_given()
    {
        using var pdf = Text("%PDF-1.4");
        Assert.Equal("report.final", FileTypes.Complete("report.final", "application/octet-stream", pdf).FileName);
    }

    [Fact]
    public void A_specific_declared_type_is_kept_and_supplies_the_extension_when_the_bytes_cannot()
    {
        using var csv = Text("a,b\n1,2\n");
        var (name, type) = FileTypes.Complete("figures", "text/csv; charset=utf-8", csv);
        Assert.Equal("figures.csv", name);
        Assert.Equal("text/csv; charset=utf-8", type);
    }

    [Fact]
    public void Nothing_to_go_on_leaves_the_name_alone()
    {
        using var s = Text("mystery");
        var (name, type) = FileTypes.Complete("README", "application/octet-stream", s);
        Assert.Equal("README", name);
        Assert.Equal("application/octet-stream", type);
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("Q3.docx", true)]
    [InlineData("archive.7z", true)]
    [InlineData("track.mp3", true)]
    [InlineData("quarterly report", false)]
    [InlineData("report v1.2", false)]          // a version number, not a type
    [InlineData("Mr. Smith notes", false)]      // a dot in the prose
    [InlineData("notes.", false)]
    [InlineData("weird.p d f", false)]
    public void An_extension_is_a_real_one_not_just_a_dot(string name, bool expected)
    {
        Assert.Equal(expected, FileTypes.HasExtension(name));
    }

    [Fact]
    public void A_versioned_name_still_gets_its_type_added()
    {
        using var pdf = Text("%PDF-1.4");
        Assert.Equal("report v1.2.pdf",
            FileTypes.Complete("report v1.2", "application/x-www-form-urlencoded", pdf).FileName);
    }

    // ── what the tools say ───────────────────────────────────────────────────

    [Fact]
    public void Stage_file_says_what_recipients_will_see()
    {
        Assert.Contains("Recipients will see it as \"Q3 report.pdf\"", FileAndJobTools.FileNameNote("Q3 report.pdf"));

        var bare = FileAndJobTools.FileNameNote("quarterly report");
        Assert.Contains("has no extension", bare);
        Assert.Contains("PDF", bare);
        Assert.Contains("fileName", bare);
    }

    [Theory]
    [InlineData("reports/q3.pdf")]
    [InlineData("..\\q3.pdf")]
    [InlineData("q3\n.pdf")]
    [InlineData("   ")]
    public void A_filename_that_is_not_just_a_filename_is_refused(string fileName)
    {
        Assert.NotNull(FileAndJobTools.FileNameProblem(fileName));
    }

    [Fact]
    public void An_ordinary_filename_is_accepted()
    {
        Assert.Null(FileAndJobTools.FileNameProblem("Q3 report (final) — Acme's copy.pdf"));
        Assert.Null(FileAndJobTools.FileNameProblem(null));
    }

    [Fact]
    public void The_send_says_when_an_attachment_has_no_extension()
    {
        StagedFile F(string fileName) => new()
        {
            UserId = Guid.Empty, Name = fileName, FileName = fileName, ContentType = "x",
            SizeBytes = 1, StorageKey = "k", ExpiresAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("", FileAndJobTools.NoExtensionNote([F("a.pdf")]));
        Assert.Contains("\"README\" has no file extension", FileAndJobTools.NoExtensionNote([F("a.pdf"), F("README")]));
    }
}

/// <summary>
/// The same, end to end through the staging service and the upload ticket — against
/// Postgres, with an in-memory object store so it needs no Garage.
/// </summary>
[Collection("database")]
public sealed class StagedFileNameStorageTests : IAsyncLifetime
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private sealed class MemoryStore : IObjectStore
    {
        public Dictionary<string, (byte[] Bytes, string Type)> Objects { get; } = [];

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Objects[key] = (ms.ToArray(), contentType);
        }

        public Task<Stream> GetRangeAsync(string key, long from, long toInclusive, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Objects[key].Bytes[(int)from..(int)(toInclusive + 1)]));
        public Task<Stream> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Objects[key].Bytes));
        public Task DeleteAsync(string key, CancellationToken ct = default) { Objects.Remove(key); return Task.CompletedTask; }
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(Objects.ContainsKey(key));
    }

    private OutPostDbContext _db = null!;
    private readonly MemoryStore _store = new();
    private StagingService _staging = null!;
    private UploadTicketService _tickets = null!;
    private readonly Guid _user = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
        await _db.Database.MigrateAsync();
        _staging = new StagingService(_db, _store,
            Options.Create(new StorageOptions { PerUserQuotaBytes = 1024 * 1024 }),
            NullLogger<StagingService>.Instance);
        _tickets = new UploadTicketService(_db, NullLogger<UploadTicketService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.StagedFiles.Where(f => f.UserId == _user).ExecuteDeleteAsync();
        await _db.UploadTickets.Where(t => t.UserId == _user).ExecuteDeleteAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_pdf_staged_without_an_extension_goes_out_as_a_pdf()
    {
        await using var pdf = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF"));

        var staged = await _staging.UploadAsync(
            _user, "quarterly report", "quarterly report", "application/x-www-form-urlencoded", pdf);

        Assert.Equal("quarterly report", staged.Name);           // the handle is unchanged
        Assert.Equal("quarterly report.pdf", staged.FileName);   // the attachment is not
        Assert.Equal("application/pdf", staged.ContentType);
        Assert.Equal("application/pdf", _store.Objects[staged.StorageKey].Type);

        // And every byte still made it: sniffing must not eat the start of the file.
        Assert.Equal("%PDF-1.7\n%%EOF", Encoding.ASCII.GetString(_store.Objects[staged.StorageKey].Bytes));
    }

    [Fact]
    public async Task A_ticket_carries_a_filename_apart_from_its_handle()
    {
        var (ticket, token) = await _tickets.MintAsync(_user, "q3", 1024, "Q3 report.pdf");
        Assert.Equal("Q3 report.pdf", ticket.FileName);

        var redeemed = await _tickets.RedeemAsync(token, null);
        Assert.Equal("q3", redeemed!.Name);
        Assert.Equal("Q3 report.pdf", redeemed.FileName);
    }

    [Fact]
    public async Task A_ticket_without_a_filename_falls_back_to_the_handle_as_before()
    {
        var (ticket, _) = await _tickets.MintAsync(_user, "report.pdf", 1024);
        Assert.Null(ticket.FileName);
    }
}
