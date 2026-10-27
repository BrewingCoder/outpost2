using OutPost2.Infrastructure.Mail;

namespace OutPost2.Tests.Mail;

public class MarkdownToHtmlTests
{
    // ── the security property: this HTML goes out over the user's own identity ──

    [Fact]
    public void Script_tags_in_model_output_cannot_become_markup()
    {
        var html = MarkdownToHtml.Render("Hello <script>alert('xss')</script> world");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Raw_html_is_escaped_not_passed_through()
    {
        var html = MarkdownToHtml.Render("<img src=x onerror=alert(1)>");

        // The payload survives as ESCAPED TEXT, which is the correct outcome - it renders
        // as literal characters, not as an element. Asserting the word "onerror" is absent
        // would test the wrong property; what matters is that no tag was created.
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
        Assert.DoesNotContain("<img src=x", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](data:text/html,<script>alert(1)</script>)")]
    [InlineData("[click](vbscript:msgbox)")]
    public void Only_http_links_become_anchors(string markdown)
    {
        // The allow-list pattern simply will not match these schemes, so there is no
        // sanitiser bypass to get wrong.
        var html = MarkdownToHtml.Render(markdown);

        // Same reasoning: "javascript:" appearing as escaped body text is inert. The
        // property under test is that no anchor was produced for a non-http scheme.
        Assert.DoesNotContain("<a href", html);
    }

    [Fact]
    public void Https_links_render_as_anchors()
    {
        var html = MarkdownToHtml.Render("See [the docs](https://example.com/docs).");

        Assert.Contains("""<a href="https://example.com/docs">the docs</a>""", html);
    }

    // ── formatting ───────────────────────────────────────────────────────────

    [Fact]
    public void Renders_bold_italic_and_code()
    {
        var html = MarkdownToHtml.Render("**bold** and *italic* and `code`");

        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<em>italic</em>", html);
        Assert.Contains("<code>code</code>", html);
    }

    [Fact]
    public void Renders_bullet_lists()
    {
        var html = MarkdownToHtml.Render("- first\n- second");

        Assert.Contains("<ul>", html);
        Assert.Contains("<li>first</li>", html);
        Assert.Contains("<li>second</li>", html);
        Assert.Contains("</ul>", html);
    }

    [Fact]
    public void Closes_a_list_when_prose_resumes()
    {
        var html = MarkdownToHtml.Render("- item\n\nAfterwards.");

        var listEnd = html.IndexOf("</ul>", StringComparison.Ordinal);
        var prose = html.IndexOf("Afterwards", StringComparison.Ordinal);

        Assert.True(listEnd >= 0 && listEnd < prose, "list must close before following prose");
    }

    [Fact]
    public void Ampersands_survive_as_entities_not_broken_markup()
    {
        var html = MarkdownToHtml.Render("Tom & Jerry");
        Assert.Contains("Tom &amp; Jerry", html);
    }

    [Fact]
    public void Produces_a_complete_document()
    {
        var html = MarkdownToHtml.Render("hi");

        Assert.StartsWith("<html>", html);
        Assert.EndsWith("</html>", html);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Handles_empty_input(string input) => Assert.Equal("<p></p>", MarkdownToHtml.Render(input));

    [Fact]
    public void Round_trips_through_the_reader_without_losing_content()
    {
        // What we send should be readable back as approximately what the model wrote —
        // the two halves of OP2-8 must agree.
        const string original = "Here are the **three** things:\n- alpha\n- beta\n\nSee [docs](https://example.com).";

        var text = HtmlToText.Convert(MarkdownToHtml.Render(original));

        Assert.Contains("three", text);
        Assert.Contains("- alpha", text);
        Assert.Contains("- beta", text);
        Assert.Contains("docs (https://example.com)", text);
    }
}
