using OutPost2.Infrastructure.Feedback;

namespace OutPost2.Tests.Feedback;

/// <summary>
/// Shaping a friction report before it leaves the reporter's session (OP2-74).
/// <para>
/// Two of these matter more than the rest. Redaction, because these reports cross from one
/// person's mailbox into a tracker other people read. And deduplication, because the
/// signal the tracker exists to carry — several people hitting the same edge — is exactly
/// what six copies of the same issue destroy.
/// </para>
/// </summary>
public sealed class FrictionReportTests
{
    // ── redaction ────────────────────────────────────────────────────────────

    [Fact]
    public void Email_addresses_are_removed()
    {
        var scrubbed = FrictionService.Scrub(
            "list_mail showed nothing from jordan.lee@example.com even though it was there");

        Assert.DoesNotContain("@example.com", scrubbed);
        Assert.DoesNotContain("jordan.lee", scrubbed);
        Assert.Contains("[address]", scrubbed);

        // The sentence around it has to survive, or the report says nothing.
        Assert.Contains("list_mail showed nothing from", scrubbed);
    }

    [Theory]
    [InlineData("a.b+tag@example.co.uk")]
    [InlineData("FIRST.LAST@EXAMPLE.COM")]
    [InlineData("x@y.io")]
    public void Address_shapes_are_all_caught(string address)
    {
        var scrubbed = FrictionService.Scrub($"failed for {address} twice");
        Assert.DoesNotContain(address, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[address]", scrubbed);
    }

    // Every fixture below is SYNTHETIC and must stay that way. A redaction test needs a
    // string shaped like the real thing, which makes it the easiest place in the codebase
    // to park a working credential by accident — and the test still passes when you do.
    [Theory]
    [InlineData("ghp_EXAMPLEONLYnotarealtoken000000000000")]
    [InlineData("perm:ZXhhbXBsZQ==.MDAtMA==.EXAMPLEONLYnotarealtoken00000")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r")]
    public void Credentials_are_removed(string secret)
    {
        var scrubbed = FrictionService.Scrub($"the call failed with {secret} in the header");
        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("[redacted]", scrubbed);
    }

    [Fact]
    public void A_pasted_graph_message_id_is_treated_as_a_secret_rather_than_prose()
    {
        // Not a credential, but not something a report needs either — and a model that
        // pastes one has pasted an identifier for a specific message in a real mailbox.
        var id = new string('A', 60) + "=";
        Assert.DoesNotContain(id, FrictionService.Scrub($"read_mail failed on {id} every time"));
    }

    [Fact]
    public void Ordinary_prose_is_left_alone()
    {
        const string said = "read_mail cut the acceptance criteria off mid-sentence at about 8000 characters";
        Assert.Equal(said, FrictionService.Scrub(said));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_scrubs_to_nothing(string said) => Assert.Equal("", FrictionService.Scrub(said));

    // ── summaries ────────────────────────────────────────────────────────────

    [Fact]
    public void The_summary_is_the_tool_and_the_first_line()
    {
        var summary = FrictionService.Summarise("read_mail",
            "The body was truncated.\nI could not tell how much was missing.");

        Assert.Equal("read_mail: The body was truncated.", summary);
    }

    [Fact]
    public void A_long_problem_is_cut_at_a_word_boundary()
    {
        var problem = string.Join(" ", Enumerable.Repeat("truncation", 40));
        var summary = FrictionService.Summarise(null, problem);

        Assert.True(summary.Length <= 112, $"summary was {summary.Length} characters");
        Assert.EndsWith("…", summary);
        Assert.DoesNotContain("truncati…", summary);
    }

    [Fact]
    public void A_summary_with_no_tool_is_just_the_problem()
    {
        Assert.Equal("Connections are hard to name", FrictionService.Summarise(null, "Connections are hard to name"));
        Assert.Equal("Connections are hard to name", FrictionService.Summarise("  ", "Connections are hard to name"));
    }

    [Fact]
    public void Wrapped_whitespace_is_collapsed_so_the_title_is_one_line()
    {
        var summary = FrictionService.Summarise("list_mail", "  too   many\t spaces  ");
        Assert.Equal("list_mail: too many spaces", summary);
    }

    // ── deduplication ────────────────────────────────────────────────────────

    [Fact]
    public void The_same_complaint_in_different_words_is_recognised()
    {
        var a = "read_mail: the message body is truncated without saying how much is missing";
        var b = "read_mail: truncated message body, nothing says how much content is missing";

        Assert.True(FrictionService.Similarity(a, b) >= 0.6,
            $"similarity was {FrictionService.Similarity(a, b):F2}");
    }

    [Fact]
    public void Identical_summaries_match_exactly()
    {
        const string s = "list_mail: results do not say which mailbox they came from";
        Assert.Equal(1.0, FrictionService.Similarity(s, s), 3);
    }

    [Fact]
    public void Different_complaints_about_the_same_tool_are_kept_apart()
    {
        var a = "read_mail: the message body is truncated without saying how much is missing";
        var b = "read_mail: attachments on forwarded invoices cannot be opened at all";

        Assert.True(FrictionService.Similarity(a, b) < 0.6,
            $"similarity was {FrictionService.Similarity(a, b):F2}");
    }

    /// <summary>
    /// Short titles share stopwords and little else. Without a floor on the absolute
    /// overlap, "mail is slow" and "mail is wrong" look like the same report.
    /// </summary>
    [Fact]
    public void Two_short_titles_sharing_a_word_are_not_a_duplicate()
    {
        Assert.Equal(0, FrictionService.Similarity("mail is slow", "mail is wrong"));
    }

    [Theory]
    [InlineData("", "anything at all here")]
    [InlineData("anything at all here", "")]
    [InlineData("the a an of", "the a an of")]
    public void Nothing_meaningful_means_no_match(string a, string b) =>
        Assert.Equal(0, FrictionService.Similarity(a, b));

    // ── the filed description ────────────────────────────────────────────────

    [Fact]
    public void The_description_records_who_hit_it_and_what_kind_of_problem_it_is()
    {
        var body = FrictionService.Describe(
            FrictionKind.MissingFeature, "move_mail",
            "clear out a phishing message during a drive",
            "there is no way to report it as junk, only to delete it",
            "BrewingCoder");

        Assert.Contains("missing feature", body);
        Assert.Contains("`move_mail`", body);
        Assert.Contains("BrewingCoder", body);
        Assert.Contains("clear out a phishing message", body);
        Assert.Contains("no way to report it as junk", body);
    }

    [Fact]
    public void A_report_about_no_particular_tool_says_so_rather_than_leaving_a_blank()
    {
        var body = FrictionService.Describe(
            FrictionKind.AwkwardDesign, null, "", "naming connections out loud is awkward", "someone");

        Assert.Contains("_not tool-specific_", body);
        Assert.Contains("_not stated_", body);
        Assert.Contains("awkward design", body);
    }
}
