using OutPost2.Core.Mail;
using OutPost2.Infrastructure.Mail;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Mail;

/// <summary>
/// The pure parts of the mail action surface (OP2-69, OP2-71, OP2-76): where a reply's
/// text lands, what a spoken destination means, and how a colour is named.
/// </summary>
public sealed class MailActionTests
{
    // ── reply bodies (OP2-71) ────────────────────────────────────────────────

    [Fact]
    public void Reply_text_goes_inside_the_body_and_above_the_quoted_thread()
    {
        const string draft = "<html><head><style>p{}</style></head><body><div>quoted original</div></body></html>";
        var result = MailService.Prepend("<p>Sounds good.</p>", draft);

        Assert.Contains("<body><p>Sounds good.</p><div>quoted original</div>", result);

        // Above the quote, not after it — a reply that reads as an empty message with a
        // thread attached is the failure this guards.
        Assert.True(result.IndexOf("Sounds good", StringComparison.Ordinal)
                    < result.IndexOf("quoted original", StringComparison.Ordinal));
    }

    [Fact]
    public void Body_attributes_do_not_break_the_insertion_point()
    {
        const string draft = """<html><body dir="ltr" style="font-family:Calibri">quoted</body></html>""";
        var result = MailService.Prepend("<p>hi</p>", draft);

        Assert.Contains("""style="font-family:Calibri"><p>hi</p>quoted""", result);
        Assert.DoesNotContain("<p>hi</p><html>", result);
    }

    [Fact]
    public void A_draft_with_no_body_tag_still_gets_the_reply_first()
    {
        var result = MailService.Prepend("<p>hi</p>", "<div>quoted</div>");
        Assert.Equal("<p>hi</p><div>quoted</div>", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_draft_leaves_just_the_reply(string? draft)
    {
        Assert.Equal("<p>hi</p>", MailService.Prepend("<p>hi</p>", draft));
    }

    [Fact]
    public void A_malformed_body_tag_falls_back_rather_than_mangling_the_draft()
    {
        // No closing '>' to insert after. Concatenating is wrong-looking but harmless;
        // slicing on a -1 index would throw and lose the reply entirely.
        var result = MailService.Prepend("<p>hi</p>", "<body");
        Assert.Equal("<p>hi</p><body", result);
    }

    // ── destinations (OP2-69) ────────────────────────────────────────────────

    [Theory]
    [InlineData("archive", "archive")]
    [InlineData("Archive", "archive")]
    [InlineData("  ARCHIVED  ", "archive")]
    [InlineData("trash", "deleteditems")]
    [InlineData("bin", "deleteditems")]
    [InlineData("deleted items", "deleteditems")]
    [InlineData("delete", "deleteditems")]
    [InlineData("junk", "junkemail")]
    [InlineData("spam", "junkemail")]
    [InlineData("Junk Email", "junkemail")]
    [InlineData("inbox", "inbox")]
    [InlineData("sent", "sentitems")]
    [InlineData("drafts", "drafts")]
    public void Spoken_destinations_map_to_the_well_known_folder(string said, string expected)
    {
        Assert.Equal(expected, MailTools.WellKnown(said));
    }

    [Theory]
    [InlineData("Projects")]
    [InlineData("Clients/Acme")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_looked_up_as_a_real_folder(string? said)
    {
        // Null rather than a guess: resolving "Projects" to Archive because both are
        // filing would put mail somewhere the user cannot find it.
        Assert.Null(MailTools.WellKnown(said));
    }

    // ── categories (OP2-76) ──────────────────────────────────────────────────

    [Theory]
    [InlineData("preset0", "red")]
    [InlineData("preset7", "blue")]
    [InlineData("preset24", "dark cranberry")]
    [InlineData("none", "no colour")]
    [InlineData(null, "no colour")]
    public void Preset_codes_are_described_as_colours(string? preset, string expected)
    {
        Assert.Equal(expected, CategoryColours.Describe(preset));
    }

    [Fact]
    public void An_unknown_preset_is_reported_as_itself_rather_than_invented()
    {
        Assert.Equal("preset99", CategoryColours.Describe("preset99"));
    }

    [Theory]
    [InlineData("red", "preset0")]
    [InlineData("Blue", "preset7")]
    [InlineData("  dark green  ", "preset19")]
    [InlineData("preset3", "preset3")]
    [InlineData("none", "none")]
    public void Spoken_colours_map_back_to_preset_codes(string said, string expected)
    {
        Assert.Equal(expected, CategoryColours.Preset(said));
    }

    [Theory]
    [InlineData("magenta")]
    [InlineData("")]
    [InlineData(null)]
    public void A_colour_Outlook_does_not_have_is_refused(string? said)
    {
        Assert.Null(CategoryColours.Preset(said));
    }

    [Fact]
    public void Every_colour_name_round_trips()
    {
        foreach (var name in CategoryColours.All)
        {
            var preset = CategoryColours.Preset(name);
            Assert.NotNull(preset);
            Assert.Equal(name, CategoryColours.Describe(preset));
        }
    }
}
