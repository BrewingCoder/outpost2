using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

public class HtmlToTextTests
{
    [Fact]
    public void Strips_script_style_and_head()
    {
        var html = "<html><head><title>x</title></head><body><style>p{color:red}</style>" +
                   "<script>alert(1)</script><p>Real content</p></body></html>";

        var text = HtmlToText.Convert(html);

        Assert.Equal("Real content", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color:red", text);
    }

    [Fact]
    public void Keeps_link_destinations()
    {
        // "Click here" with the URL stripped is actively misleading, and a model asked
        // "what did they link to?" would have nothing to answer with.
        var text = HtmlToText.Convert("""<p>See <a href="https://example.com/invoice">the invoice</a>.</p>""");

        Assert.Contains("the invoice (https://example.com/invoice)", text);
    }

    [Fact]
    public void Does_not_duplicate_a_link_whose_text_is_the_url()
    {
        var text = HtmlToText.Convert("""<a href="https://example.com">https://example.com</a>""");
        Assert.Equal("https://example.com", text);
    }

    [Fact]
    public void Drops_tracking_pixels()
    {
        var text = HtmlToText.Convert(
            """<p>Hello</p><img src="https://track.example/p.gif" width="1" height="1">""");

        Assert.Equal("Hello", text);
        Assert.DoesNotContain("track.example", text);
    }

    [Fact]
    public void Removes_zero_width_spacing_characters()
    {
        // Bulk senders pad with these; they survive naive stripping as invisible tokens
        // that cost context budget and show up as mojibake in a spoken summary.
        var text = HtmlToText.Convert("<p>Hi​‌‍﻿ there</p>");
        Assert.Equal("Hi there", text);
    }

    [Fact]
    public void Decodes_entities()
    {
        Assert.Equal("Tom & Jerry — \"quoted\"",
            HtmlToText.Convert("<p>Tom &amp; Jerry &mdash; &quot;quoted&quot;</p>"));
    }

    [Fact]
    public void Renders_lists_as_bullets()
    {
        var text = HtmlToText.Convert("<ul><li>first</li><li>second</li></ul>");

        Assert.Contains("- first", text);
        Assert.Contains("- second", text);
    }

    [Fact]
    public void Collapses_excessive_blank_lines()
    {
        var text = HtmlToText.Convert("<p>a</p><br><br><br><br><p>b</p>");
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Handles_empty_input(string? input) => Assert.Equal("", HtmlToText.Convert(input));

    // ── signature trimming: deliberately conservative ────────────────────────

    [Fact]
    public void Trims_a_trailing_disclaimer()
    {
        var text = string.Join("\n",
            "Hi there,", "", "Here is the thing you asked for.", "", "Thanks,", "Dave", "",
            "This email and any attachments are confidential and intended solely for the addressee.");

        Assert.DoesNotContain("confidential", HtmlToText.TrimSignature(text));
        Assert.Contains("Here is the thing", HtmlToText.TrimSignature(text));
    }

    [Fact]
    public void Does_not_trim_a_marker_that_appears_early_in_the_message()
    {
        // Only the last third is eligible. Deleting real content is far worse than
        // leaving boilerplate in: the user can ignore a disclaimer but cannot recover
        // a sentence we removed.
        var text = string.Join("\n",
            "Confidentiality notice: please read the following carefully.",
            "", "Actually important content here.", "", "And more content.", "",
            "Still going.", "Final line.");

        Assert.Contains("Actually important content", HtmlToText.TrimSignature(text));
        Assert.Contains("Final line", HtmlToText.TrimSignature(text));
    }

    [Fact]
    public void Does_not_trim_when_a_lot_follows_the_marker()
    {
        var tail = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"paragraph {i}"));
        var text = "Opening line.\n\nBody.\n\nmore\nmore\nmore\n-- \n" + tail;

        Assert.Contains("paragraph 20", HtmlToText.TrimSignature(text));
    }

    [Fact]
    public void Leaves_short_messages_alone()
    {
        const string text = "-- \njust a stub";
        Assert.Equal(text, HtmlToText.TrimSignature(text));
    }

    // ── clamping ─────────────────────────────────────────────────────────────

    [Fact]
    public void Clamp_announces_that_it_truncated()
    {
        // Silent truncation would let a model confidently summarise half a message.
        var text = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}"));
        var clamped = HtmlToText.Clamp(text, 200);

        Assert.True(clamped.Length < text.Length);
        Assert.Contains("truncated", clamped);
        Assert.Contains("ask for the full message", clamped);
    }

    [Fact]
    public void Clamp_leaves_short_text_untouched()
    {
        Assert.Equal("short", HtmlToText.Clamp("short", 1000));
    }

    [Fact]
    public void Real_world_newsletter_shrinks_dramatically()
    {
        // The actual payoff: markup is paid for twice, in context budget and voice latency.
        var html = """
            <html><head><style>.x{font-family:Arial}</style></head><body>
            <table width="100%"><tr><td style="padding:20px">
            <img src="https://cdn.example/logo.png" width="200">
            <h1 style="color:#333">Weekly Update</h1>
            <p style="line-height:1.6">Three things happened this week.</p>
            <ul><li>We shipped the thing</li><li>We fixed the other thing</li></ul>
            <p><a href="https://example.com/read">Read more</a></p>
            <img src="https://track.example/open.gif" width="1" height="1">
            </td></tr></table></body></html>
            """;

        var text = HtmlToText.Convert(html);

        Assert.True(text.Length < html.Length / 3,
            $"expected a big reduction, got {text.Length} from {html.Length}");
        Assert.Contains("Weekly Update", text);
        Assert.Contains("- We shipped the thing", text);
        Assert.Contains("Read more (https://example.com/read)", text);
        Assert.DoesNotContain("track.example", text);
        Assert.DoesNotContain("font-family", text);
    }
}
