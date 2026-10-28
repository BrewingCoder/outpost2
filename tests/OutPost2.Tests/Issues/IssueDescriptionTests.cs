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
            Summary: "Jurisdictions surface is 403 for the whole Indelible tier",
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

    [Fact]
    public void A_missing_reporter_does_not_lose_the_creation_date()
    {
        var text = IssueTools.Describe(Issue(reporter: null));

        Assert.Contains("Created", text);
        Assert.Contains("24 Oct 2026", text);
    }
}
