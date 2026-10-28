using OutPost2.Infrastructure.Issues;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Issues;

/// <summary>
/// The formatter, on its own. This is the step that lost the reporter: the service
/// fetched it, the record carried it, and the text handed to the conversation never
/// mentioned it — so "who filed this?" came back as a limitation of the tracker rather
/// than as the answer already sitting in the response.
/// <para>
/// Nothing at the service layer can catch that, which is why this is a separate file
/// rather than another assertion in the live probe.
/// </para>
/// </summary>
public sealed class IssueDescriptionTests
{
    private const string Summary = "Jurisdictions surface is 403 for the whole Indelible tier";

    private static IssueDetail Issue(
        string? reporter = "Justin Mora",
        IReadOnlyList<KeyValuePair<string, string>>? fields = null,
        IReadOnlyList<KeyValuePair<string, string>>? links = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<IssueComment>? comments = null,
        int? commentCount = null) =>
        new(
            Id: "2-1848",
            Key: "STRATA-1848",
            Summary: Summary,
            Description: "Every call to /jurisdictions returns 403.",
            State: "Submitted",
            Assignee: null,
            Project: "STRATA",
            Reporter: reporter,
            Created: new DateTimeOffset(2026, 10, 24, 9, 14, 0, TimeSpan.Zero),
            Updated: new DateTimeOffset(2026, 10, 27, 16, 2, 0, TimeSpan.Zero),
            Resolved: null,
            Tags: tags ?? [],
            Fields: fields ?? [new("State", "Submitted"), new("Priority", "Critical")],
            Links: links ?? [],
            CommentCount: commentCount ?? comments?.Count ?? 0,
            Comments: comments ?? [],
            Url: "https://yt.example.com/issue/STRATA-1848");

    [Fact]
    public void The_reporter_is_named()
    {
        var text = IssueTools.Describe(Issue());
        Assert.Contains("Justin Mora", text);
    }

    [Fact]
    public void Custom_fields_beyond_state_and_assignee_are_shown()
    {
        var text = IssueTools.Describe(Issue(fields:
        [
            new("State", "Submitted"),
            new("Priority", "Critical"),
            new("Type", "Bug"),
            new("Subsystem", "Jurisdictions"),
        ]));

        // A project-defined field is invisible to the conversation unless it is printed,
        // and its absence gives no sign that anything was left out.
        Assert.Contains("Priority: Critical", text);
        Assert.Contains("Type: Bug", text);
        Assert.Contains("Subsystem: Jurisdictions", text);
    }

    [Fact]
    public void State_and_assignee_are_not_repeated_in_the_field_list()
    {
        var text = IssueTools.Describe(Issue());

        // Already on the heading line. Saying it twice costs the listener attention for
        // no new information, which matters more when it is being read out loud.
        Assert.Contains("state Submitted", text);
        Assert.DoesNotContain("State: Submitted", text);
    }

    [Fact]
    public void Dates_are_absolute_rather_than_relative()
    {
        var text = IssueTools.Describe(Issue());

        Assert.Contains("24 Oct 2026", text);
        Assert.Contains("Last updated", text);

        // Unset must stay unsaid: an unresolved issue reporting a resolution date is
        // worse than one reporting none.
        Assert.DoesNotContain("Resolved", text);
    }

    [Fact]
    public void Links_to_other_issues_are_shown()
    {
        var text = IssueTools.Describe(Issue(links:
        [
            new("subtask of", "STRATA-1800"),
            new("relates to", "STRATA-1703, STRATA-1711"),
        ]));

        Assert.Contains("subtask of: STRATA-1800", text);
        Assert.Contains("relates to: STRATA-1703, STRATA-1711", text);
    }

    [Fact]
    public void Tags_are_shown()
    {
        var text = IssueTools.Describe(Issue(tags: ["customer-reported", "security"]));
        Assert.Contains("customer-reported", text);
        Assert.Contains("security", text);
    }

    [Fact]
    public void A_truncated_comment_list_says_how_many_there_really_are()
    {
        var many = Enumerable.Range(1, 25)
            .Select(n => new IssueComment($"Author {n}", $"Comment {n}", DateTimeOffset.UtcNow))
            .ToList();

        var text = IssueTools.Describe(Issue(comments: many));

        // Showing a subset without saying so lets the newest ten read as the whole
        // conversation, which is how a decision gets made on half the thread.
        Assert.Contains("25 comments", text);
        Assert.Contains("most recent 10", text);
        Assert.Contains("Comment 25", text);
        Assert.DoesNotContain("Comment 1 ", text);
    }

    [Fact]
    public void Comment_authors_and_times_are_kept()
    {
        var text = IssueTools.Describe(Issue(comments:
        [
            new("Jordan Lee", "Reproduced on staging.",
                new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero)),
        ]));

        Assert.Contains("Jordan Lee", text);
        Assert.Contains("Reproduced on staging.", text);
        Assert.Contains("26 Oct 2026", text);
    }

    [Fact]
    public void The_issue_url_is_included()
    {
        var text = IssueTools.Describe(Issue());

        // The one thing that turns a spoken answer into something actionable later.
        Assert.Contains("https://yt.example.com/issue/STRATA-1848", text);
    }

    // ── long descriptions ───────────────────────────────────────────────────────
    //
    // Reported from a real read: "the last acceptance criterion got cut off mid-sentence."
    // A 4,000-character cut marked with a bare ellipsis is small enough to land inside an
    // ordinary acceptance-criteria list, and a half-criterion reads as a whole one.

    private static IssueDetail WithDescription(string d) => Issue() with { Description = d };

    private static string Criteria(int n) => string.Join("\n\n",
        Enumerable.Range(1, n).Select(x =>
            $"AC{x}. The system shall reject any request whose jurisdiction claim is absent, " +
            $"empty, or not present in the caller's entitlement set, returning 403 with " +
            $"code JUR_{x:D3} and no body."));

    [Fact]
    public void A_description_that_fits_is_returned_whole()
    {
        var d = Criteria(20);
        var text = IssueTools.Describe(WithDescription(d));

        Assert.Contains("AC20.", text);
        Assert.DoesNotContain("more characters", text);
    }

    [Fact]
    public void A_description_that_does_not_fit_says_how_much_is_left_and_how_to_get_it()
    {
        var text = IssueTools.Describe(WithDescription(Criteria(400)));

        Assert.Contains("more characters of this description", text);
        Assert.Contains("descriptionFrom:", text);
    }

    [Fact]
    public void A_cut_description_does_not_end_mid_sentence()
    {
        var text = IssueTools.Describe(WithDescription(Criteria(400)));

        var body = text[..text.IndexOf("[", StringComparison.Ordinal)].TrimEnd();
        var lastLine = body.Split('\n').Last(l => !string.IsNullOrWhiteSpace(l)).TrimEnd();

        // The whole point of the report: the break has to be visible as a break.
        Assert.EndsWith(".", lastLine);
    }

    [Fact]
    public void Resuming_continues_where_it_stopped_and_loses_nothing()
    {
        var d = Criteria(400);
        var issue = WithDescription(d);

        var first = IssueTools.Describe(issue);
        var offset = int.Parse(first
            .Split("descriptionFrom: ")[1]
            .Split(' ')[0]
            .TrimEnd(']', '.'));

        // Walk every part the way a model would, following each offset it is handed.
        var parts = new List<string>();
        var at = 0;
        for (var guard = 0; guard < 50; guard++)
        {
            var page = IssueTools.Describe(issue, at);
            var start = page.IndexOf(Summary, StringComparison.Ordinal);
            var bodyStart = page.IndexOf("AC", start, StringComparison.Ordinal);
            var end = page.IndexOf("[", bodyStart, StringComparison.Ordinal);

            parts.Add(end < 0 ? page[bodyStart..].Split("\nhttps://")[0] : page[bodyStart..end]);
            if (end < 0) break;

            at = int.Parse(page.Split("descriptionFrom: ")[1].Split(' ')[0].TrimEnd(']', '.'));
        }

        Assert.True(parts.Count > 1, "a 400-criterion description should not fit in one part");

        // Reassembled, the parts must be the original description. Nothing dropped at a
        // boundary, nothing duplicated across one — a criterion silently lost between two
        // pages is the same failure as the one that was reported, just harder to notice.
        var rebuilt = string.Concat(parts.Select(p => p.Trim()));
        Assert.Equal(
            string.Concat(d.Where(ch => !char.IsWhiteSpace(ch))),
            string.Concat(rebuilt.Where(ch => !char.IsWhiteSpace(ch))));

        Assert.Contains("AC400.", string.Concat(parts));
    }

    [Fact]
    public void Reading_past_the_end_of_a_description_is_harmless()
    {
        var d = Criteria(5);
        var text = IssueTools.Describe(WithDescription(d), d.Length + 5_000);

        // A model that resumes from a stale offset gets an empty tail, not an exception.
        Assert.Contains("STRATA-1848", text);
        Assert.DoesNotContain("more characters of this description", text);
    }

    [Fact]
    public void A_long_comment_says_how_much_was_left_out()
    {
        var text = IssueTools.Describe(Issue(comments:
        [
            new("Jordan Lee", Criteria(200), DateTimeOffset.UtcNow),
        ]));

        Assert.Contains("more characters]", text);
    }

    [Fact]
    public void A_missing_reporter_does_not_lose_the_creation_date()
    {
        var text = IssueTools.Describe(Issue(reporter: null));

        Assert.Contains("Created", text);
        Assert.Contains("24 Oct 2026", text);
    }
}
