using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Code;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Connections;

namespace OutPost2.Web.Mcp;

/// <summary>
/// GitHub, scoped to the question actually being asked out loud: is anything waiting on
/// me. Deliberately not a general API wrapper — code review happens elsewhere.
/// </summary>
[McpServerToolType]
public sealed class CodeTools(
    IHttpContextAccessor http,
    IConnectionStore connections,
    IGitHubService github)
{
    private Guid UserId => http.HttpContext?.User.UserId()
        ?? throw new InvalidOperationException("No authenticated user on this MCP request.");

    private async Task<(Connection? Conn, string? Message)> ResolveAsync(
        string? connection, string capability, bool isWrite, CancellationToken ct)
    {
        // Filtered to this provider before resolving. Capability names are meant to be
        // provider-agnostic, so without this a connection for a different provider could
        // win a label match and then be called with an API it does not speak.
        var candidates = (await connections.ListAsync(UserId, ct))
            .Where(c => c.Provider == ProviderKind.GitHub)
            .ToList();

        var r = ConnectionResolver.Resolve(candidates, capability, connection, isWrite);

        if (r.Kind == ResolutionKind.Single) return (r.Connection, null);

        var message = r.Kind == ResolutionKind.Ambiguous
            ? r.SpokenSummary + " (Ask the user, then call again with the connection name.)"
            : r.SpokenSummary;

        return (null, message);
    }

    private const string ConnectionDoc =
        "GitHub connection name. Required unless a default has been set.";

    [McpServerTool(Name = "github_triage")]
    [Description("What on GitHub is waiting on me: pull requests to review, my open PRs, issues assigned to me.")]
    public async Task<string> Triage(
        [Description(ConnectionDoc)] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        var t = await github.TriageAsync(c, ct);

        if (t.AwaitingMyReview == 0 && t.MyOpenPullRequests == 0 && t.IssuesAssignedToMe == 0)
            return $"Nothing waiting on you in {c.Label}. No reviews requested, no open pull requests " +
                   $"of your own, no issues assigned.";

        var sb = new StringBuilder($"In {c.Label}, as {t.Login}:\n");

        sb.AppendLine(t.AwaitingMyReview switch
        {
            0 => "- No pull requests are waiting on your review.",
            1 => "- 1 pull request is waiting on your review.",
            _ => $"- {t.AwaitingMyReview} pull requests are waiting on your review.",
        });

        var drafts = t.MyDraftPullRequests > 0 ? $" ({t.MyDraftPullRequests} still draft)" : "";
        sb.AppendLine(t.MyOpenPullRequests switch
        {
            0 => "- You have no open pull requests of your own.",
            1 => $"- You have 1 open pull request{drafts}.",
            _ => $"- You have {t.MyOpenPullRequests} open pull requests{drafts}.",
        });

        sb.AppendLine(t.IssuesAssignedToMe switch
        {
            0 => "- No issues are assigned to you.",
            1 => "- 1 issue is assigned to you.",
            _ => $"- {t.IssuesAssignedToMe} issues are assigned to you.",
        });

        if (t.MentioningMe > 0)
            sb.AppendLine($"- {t.MentioningMe} open item{(t.MentioningMe == 1 ? "" : "s")} mention you.");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "github_pull_requests")]
    [Description("List open pull requests. Scope: awaiting-my-review, mine, mentioning, or all-open (needs a repo).")]
    public async Task<string> PullRequests(
        [Description("One of: awaiting-my-review, mine, mentioning, all-open.")] string scope = "awaiting-my-review",
        [Description("Limit to one repository, e.g. 'signalclaude/outpost'. Optional.")] string? repo = null,
        [Description(ConnectionDoc)] string? connection = null,
        [Description("How many to return.")] int count = 20,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        if (!TryParseScope(scope, out PullRequestScope parsed))
            return "Scope must be one of: awaiting-my-review, mine, mentioning, all-open.";

        try
        {
            var items = await github.ListPullRequestsAsync(c, parsed, repo, count, ct);
            return Render(items, $"pull requests ({Humanise(scope)})", c.Label, repo);
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "github_issues")]
    [Description("List open issues. Scope: assigned-to-me, mentioning, unassigned (needs a repo), or all-open (needs a repo).")]
    public async Task<string> Issues(
        [Description("One of: assigned-to-me, mentioning, unassigned, all-open.")] string scope = "assigned-to-me",
        [Description("Limit to one repository, e.g. 'signalclaude/outpost'. Optional.")] string? repo = null,
        [Description(ConnectionDoc)] string? connection = null,
        [Description("How many to return.")] int count = 20,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        if (!TryParseScope(scope, out IssueScope parsed))
            return "Scope must be one of: assigned-to-me, mentioning, unassigned, all-open.";

        try
        {
            var items = await github.ListIssuesAsync(c, parsed, repo, count, ct);
            return Render(items, $"issues ({Humanise(scope)})", c.Label, repo);
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "github_item")]
    [Description("Read one pull request or issue by repository and number, e.g. signalclaude/outpost 12.")]
    public async Task<string> Item(
        [Description("Repository as owner/name, e.g. 'signalclaude/outpost'.")] string repo,
        [Description("The PR or issue number.")] int number,
        [Description(ConnectionDoc)] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        try
        {
            var item = await github.GetAsync(c, repo, number, ct);
            if (item is null) return $"No {repo}#{number} on {c.Label}, or it isn't visible to that token.";

            var sb = new StringBuilder();
            sb.AppendLine($"{(item.IsPullRequest ? "Pull request" : "Issue")} {item.Reference}: {item.Title}");
            sb.AppendLine($"Opened by {item.Author}, updated {Ago(item.UpdatedAt)}.");
            if (item.IsDraft) sb.AppendLine("Still a draft.");
            if (item.Assignees.Count > 0) sb.AppendLine($"Assigned to {string.Join(", ", item.Assignees)}.");
            else sb.AppendLine("Nobody is assigned.");
            if (item.Labels.Count > 0) sb.AppendLine($"Labels: {string.Join(", ", item.Labels)}.");
            sb.AppendLine($"{item.Comments} comment{(item.Comments == 1 ? "" : "s")}.");
            if (item.Url is not null) sb.AppendLine(item.Url);
            return sb.ToString().TrimEnd();
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    private static string Render(IReadOnlyList<CodeItem> items, string what, string label, string? repo)
    {
        var where = repo is null ? label : $"{repo} on {label}";
        if (items.Count == 0) return $"No {what} in {where}.";

        var sb = new StringBuilder($"{items.Count} {what} in {where}:\n");
        foreach (var i in items)
        {
            sb.Append($"- {i.Reference}: {i.Title}");
            if (i.IsDraft) sb.Append(" [draft]");
            sb.Append($" — by {i.Author}, updated {Ago(i.UpdatedAt)}");
            if (i.Comments > 0) sb.Append($", {i.Comments} comment{(i.Comments == 1 ? "" : "s")}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Relative time, because "updated 2026-10-14T09:11Z" is unusable out loud.</summary>
    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} minutes ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} hour{((int)d.TotalHours == 1 ? "" : "s")} ago";
        if (d < TimeSpan.FromDays(30)) return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";
        return $"{(int)(d.TotalDays / 30)} month{((int)(d.TotalDays / 30) == 1 ? "" : "s")} ago";
    }

    private static string Humanise(string scope) => scope.Replace('-', ' ');

    private static bool TryParseScope(string scope, out PullRequestScope parsed)
    {
        parsed = scope.Trim().ToLowerInvariant() switch
        {
            "awaiting-my-review" or "review" or "awaiting" => PullRequestScope.AwaitingMyReview,
            "mine" or "author" or "my" => PullRequestScope.Mine,
            "mentioning" or "mentions" => PullRequestScope.Mentioning,
            "all-open" or "all" or "open" => PullRequestScope.AllOpen,
            _ => (PullRequestScope)(-1),
        };
        return Enum.IsDefined(parsed);
    }

    private static bool TryParseScope(string scope, out IssueScope parsed)
    {
        parsed = scope.Trim().ToLowerInvariant() switch
        {
            "assigned-to-me" or "assigned" or "mine" => IssueScope.AssignedToMe,
            "mentioning" or "mentions" => IssueScope.Mentioning,
            "unassigned" or "no-assignee" => IssueScope.Unassigned,
            "all-open" or "all" or "open" => IssueScope.AllOpen,
            _ => (IssueScope)(-1),
        };
        return Enum.IsDefined(parsed);
    }
}
