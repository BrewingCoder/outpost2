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
    IGitHubService github,
    IRepoResolver repos)
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

    private const string RepoDoc =
        "Repository. A bare name like 'RosettaCore' is resolved against the repositories " +
        "this connection can see; owner/name is taken as given.";

    /// <summary>
    /// Turns what was said into an owner/name, or returns the sentence to say back.
    /// <para>
    /// Every repository argument goes through here. Passing a spoken name straight to the
    /// API produced a 404 that read as "no such repository" when the truth was "I was
    /// never told which owner", and there was no way to tell those apart from outside.
    /// </para>
    /// </summary>
    private async Task<(string? Repo, string? Message)> RepoAsync(
        Connection c, string? spoken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return (null, null);

        var r = await repos.ResolveAsync(c, spoken, ct);
        return r.Kind == RepoResolutionKind.Resolved ? (r.FullName, null) : (null, r.Message);
    }

    [McpServerTool(Name = "github_triage", ReadOnly = true, OpenWorld = true)]
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

    [McpServerTool(Name = "github_pull_requests", ReadOnly = true, OpenWorld = true)]
    [Description("List open pull requests. Scope: awaiting-my-review, mine, mentioning, or all-open (needs a repo).")]
    public async Task<string> PullRequests(
        [Description("One of: awaiting-my-review, mine, mentioning, all-open.")] string scope = "awaiting-my-review",
        [Description(RepoDoc + " Optional.")] string? repo = null,
        [Description(ConnectionDoc)] string? connection = null,
        [Description("How many to return.")] int count = 20,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        if (!TryParseScope(scope, out PullRequestScope parsed))
            return "Scope must be one of: awaiting-my-review, mine, mentioning, all-open.";

        var (full, repoMsg) = await RepoAsync(c, repo, ct);
        if (repoMsg is not null) return repoMsg;

        try
        {
            var items = await github.ListPullRequestsAsync(c, parsed, full, count, ct);
            return Render(items, $"pull requests ({Humanise(scope)})", c.Label, full);
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "github_issues", ReadOnly = true, OpenWorld = true)]
    [Description("List open issues. Scope: assigned-to-me, mentioning, unassigned (needs a repo), or all-open (needs a repo).")]
    public async Task<string> Issues(
        [Description("One of: assigned-to-me, mentioning, unassigned, all-open.")] string scope = "assigned-to-me",
        [Description(RepoDoc + " Optional.")] string? repo = null,
        [Description(ConnectionDoc)] string? connection = null,
        [Description("How many to return.")] int count = 20,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        if (!TryParseScope(scope, out IssueScope parsed))
            return "Scope must be one of: assigned-to-me, mentioning, unassigned, all-open.";

        var (full, repoMsg) = await RepoAsync(c, repo, ct);
        if (repoMsg is not null) return repoMsg;

        try
        {
            var items = await github.ListIssuesAsync(c, parsed, full, count, ct);
            return Render(items, $"issues ({Humanise(scope)})", c.Label, full);
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "github_item", ReadOnly = true, OpenWorld = true)]
    [Description("Read one pull request or issue by repository and number, e.g. signalclaude/outpost 12.")]
    public async Task<string> Item(
        [Description(RepoDoc)] string repo,
        [Description("The PR or issue number.")] int number,
        [Description(ConnectionDoc)] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        var (full, repoMsg) = await RepoAsync(c, repo, ct);
        if (repoMsg is not null) return repoMsg;

        try
        {
            var item = await github.GetAsync(c, full!, number, ct);
            if (item is null) return $"No {full}#{number} on {c.Label}, or it isn't visible to that token.";

            var sb = new StringBuilder();
            sb.AppendLine($"{(item.IsPullRequest ? "Pull request" : "Issue")} {item.Reference}: {Untrusted.Scrub(item.Title)}");
            sb.AppendLine($"Opened by {Untrusted.Scrub(item.Author)}, updated {Ago(item.UpdatedAt)}.");
            if (item.IsDraft) sb.AppendLine("Still a draft.");
            if (item.Assignees.Count > 0) sb.AppendLine($"Assigned to {string.Join(", ", item.Assignees.Select(Untrusted.Scrub))}.");
            else sb.AppendLine("Nobody is assigned.");
            if (item.Labels.Count > 0) sb.AppendLine($"Labels: {string.Join(", ", item.Labels.Select(Untrusted.Scrub))}.");
            sb.AppendLine($"{item.Comments} comment{(item.Comments == 1 ? "" : "s")}.");
            if (item.Url is not null) sb.AppendLine(item.Url);
            return sb.ToString().TrimEnd();
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "github_repositories", ReadOnly = true, OpenWorld = true)]
    [Description("List the repositories this GitHub connection can see, and the names configured for it. Use it to answer whether a repository is reachable at all, to find its owner when only the name is known, or to see what nicknames resolve to what.")]
    public async Task<string> Repositories(
        [Description("Optional filter on name, owner or description.")] string? match = null,
        [Description(ConnectionDoc)] string? connection = null,
        [Description("How many to return.")] int count = 40,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        try
        {
            var all = await repos.KnownAsync(c, ct);
            var aliases = await repos.AliasesAsync(c, ct);

            var hits = string.IsNullOrWhiteSpace(match)
                ? all
                : all.Where(r =>
                    r.FullName.Contains(match, StringComparison.OrdinalIgnoreCase)
                    || (r.Description ?? "").Contains(match, StringComparison.OrdinalIgnoreCase)).ToList();

            // Aliases can point at a repository that is not in the listing at all — a
            // public repo this token never lists, for instance — so they are reported
            // whether or not anything in the listing matched.
            var namedHits = string.IsNullOrWhiteSpace(match)
                ? aliases
                : aliases.Where(a =>
                    a.Alias.Contains(match, StringComparison.OrdinalIgnoreCase)
                    || a.FullName.Contains(match, StringComparison.OrdinalIgnoreCase)).ToList();

            if (hits.Count == 0 && namedHits.Count == 0)
            {
                return string.IsNullOrWhiteSpace(match)
                    ? $"{c.Label} can see no repositories at all. The token may lack the repo scope."
                    : $"Nothing matching '{match}' among the {all.Count} repositories {c.Label} can see, " +
                      $"and no configured name matches either.";
            }

            if (hits.Count == 0)
            {
                var only = new StringBuilder(
                    $"Nothing in the {all.Count} repositories {c.Label} can see matches '{match}', " +
                    "but these names are configured:\n");
                foreach (var a in namedHits) only.AppendLine($"- \"{a.Alias}\" means {a.FullName}");
                return only.ToString().TrimEnd();
            }

            var shown = hits.Take(Math.Clamp(count, 1, 100)).ToList();
            var sb = new StringBuilder(shown.Count < hits.Count
                ? $"{hits.Count} repositories on {c.Label}, first {shown.Count} by most recent activity:\n"
                : $"{hits.Count} repositor{(hits.Count == 1 ? "y" : "ies")} on {c.Label}:\n");

            // The shape of what is visible, per owner. A fine-grained token can be granted
            // public access only to an account, and the listing then looks complete while
            // every private repository under that owner is missing from it. A flat count
            // cannot show that; "33 under BrewingCoder, all public" can.
            if (string.IsNullOrWhiteSpace(match) && all.Count > 0)
            {
                var byOwner = all
                    .GroupBy(r => r.Owner)
                    .OrderByDescending(g => g.Count())
                    .Select(g =>
                    {
                        var priv = g.Count(r => r.Private);
                        var shapeOf = priv == 0 ? "all public"
                            : priv == g.Count() ? "all private"
                            : $"{priv} private";
                        return $"{g.Key} {g.Count()} ({shapeOf})";
                    });

                sb.AppendLine($"By owner: {string.Join(", ", byOwner)}.");
                sb.AppendLine();
            }

            foreach (var r in shown)
            {
                sb.Append($"- {r.FullName}");
                if (r.Private) sb.Append(" [private]");
                if (r.Archived) sb.Append(" [archived]");
                if (r.Fork) sb.Append(" [fork]");
                if (r.PushedAt is { } p) sb.Append($" — last push {Ago(p)}");
                sb.AppendLine();
            }

            if (namedHits.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Names configured for this connection:");
                foreach (var a in namedHits) sb.AppendLine($"- \"{a.Alias}\" means {a.FullName}");
            }

            return sb.ToString().TrimEnd();
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "github_repo_names", ReadOnly = true, OpenWorld = true)]
    [Description("List the repository names (aliases) configured for a GitHub connection — the nicknames that resolve to a specific owner/name. Call this when asked what names, aliases or shortcuts are set up.")]
    public async Task<string> RepoNames(
        [Description(ConnectionDoc)] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        // A tool of its own, even though github_repositories already reports these. A
        // model asked about aliases looks for a tool named for them, and finding none
        // concludes the product has no such concept — which is what happened, while the
        // feature was configured, stored and in use by the resolver on every lookup.
        var names = await repos.AliasesAsync(c, ct);

        if (names.Count == 0)
        {
            return $"No repository names are configured for {c.Label}. " +
                   "None are needed unless a name is ambiguous or is a nickname GitHub does not know — " +
                   "a bare repository name resolves against the repositories this connection can see. " +
                   "They are added on the Repository names page.";
        }

        var sb = new StringBuilder(
            $"{names.Count} repository name{(names.Count == 1 ? "" : "s")} configured for {c.Label}:\n");
        foreach (var a in names) sb.AppendLine($"- \"{a.Alias}\" means {a.FullName}");

        sb.AppendLine();
        sb.AppendLine("These override the search, so saying one of these names always reaches that repository.");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "github_commits", ReadOnly = true, OpenWorld = true)]
    [Description("Recent commits on a repository, newest first. Answers 'what were the last few commits on X'.")]
    public async Task<string> Commits(
        [Description(RepoDoc)] string repo,
        [Description("Branch or tag. Defaults to the repository's default branch.")] string? branch = null,
        [Description(ConnectionDoc)] string? connection = null,
        [Description("How many to return.")] int count = 10,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        var (full, repoMsg) = await RepoAsync(c, repo, ct);
        if (repoMsg is not null) return repoMsg;

        try
        {
            var commits = await github.ListCommitsAsync(c, full!, branch, count, ct);
            if (commits.Count == 0)
                return $"{full} has no commits{(branch is null ? "" : $" on {branch}")}.";

            var where = branch is null ? full : $"{full} ({branch})";
            var sb = new StringBuilder($"Last {commits.Count} commit{(commits.Count == 1 ? "" : "s")} on {where}:\n");

            foreach (var x in commits)
            {
                // Subject only. A commit body runs to paragraphs and "the last three
                // commits" wants three subjects, not three essays.
                sb.AppendLine($"- {x.ShortSha} {x.Subject} — {x.Author}" +
                              (x.At is { } at ? $", {Ago(at)}" : ""));
            }

            return sb.ToString().TrimEnd();
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    private const string RefDoc =
        "Branch, tag or commit. Defaults to the repository's default branch.";

    /// <summary>
    /// How much of a file comes back. Generous, because the question behind reading a
    /// file is usually about the whole of it, and a caller that gets half a config file
    /// draws conclusions from the half it was given. Still a cap: a 2 MB generated file
    /// would otherwise spend the turn and answer nothing.
    /// </summary>
    private const int MaxFileChars = 60_000;

    [McpServerTool(Name = "github_files", ReadOnly = true, OpenWorld = true)]
    [Description("List what is at a path in a repository — the files and folders. Omit 'path' for the top level. Answers 'does this repo have a docs folder', 'what is in src', 'is there a README'.")]
    public async Task<string> Files(
        [Description(RepoDoc)] string repo,
        [Description("Folder path within the repository, such as 'docs' or 'src/api'. Omit for the top level.")] string? path = null,
        [Description(RefDoc)] string? branch = null,
        [Description(ConnectionDoc)] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        var (full, repoMsg) = await RepoAsync(c, repo, ct);
        if (repoMsg is not null) return repoMsg;

        try
        {
            var entries = await github.ListPathAsync(c, full!, path, branch, ct);

            // Null means the path is a file. Saying so and naming the tool that reads it
            // beats an empty listing, which reads as "the folder is empty" — a wrong
            // answer that looks like a right one.
            if (entries is null)
                return $"{path} is a file, not a folder. Use github_file to read it.";

            var where = string.IsNullOrWhiteSpace(path) ? full : $"{path} in {full}";
            if (branch is not null) where += $" on {branch}";

            if (entries.Count == 0) return $"{where} is empty.";

            var sb = new StringBuilder(
                $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} in {where}:\n");

            foreach (var e in entries)
            {
                // Scrubbed: a path in somebody else's repository is text we did not write,
                // and a zero-width character in a filename is a cheap way to make two
                // entries look like one (OP2-125).
                sb.AppendLine(e.IsDirectory
                    ? $"- {Untrusted.Scrub(e.Name)}/"
                    : $"- {Untrusted.Scrub(e.Name)} ({ByteSize.Describe(e.Size)})");
            }

            return sb.ToString().TrimEnd();
        }
        catch (GitHubException ex) { return ex.Message; }
    }

    [McpServerTool(Name = "github_file", ReadOnly = true, OpenWorld = true)]
    [Description("Read a file's contents from a repository. Answers 'what does the README say', 'show me the workflow file'. Use github_files first if the exact path is not known.")]
    public async Task<string> File(
        [Description(RepoDoc)] string repo,
        [Description("Path to the file within the repository, such as 'README.md' or 'src/Program.cs'.")] string path,
        [Description(RefDoc)] string? branch = null,
        [Description(ConnectionDoc)] string? connection = null,
        CancellationToken ct = default)
    {
        var (c, msg) = await ResolveAsync(connection, Capabilities.CodeRead, false, ct);
        if (c is null) return msg!;

        if (string.IsNullOrWhiteSpace(path))
            return "Which file? Pass a path such as 'README.md', or call github_files to see what is there.";

        var (full, repoMsg) = await RepoAsync(c, repo, ct);
        if (repoMsg is not null) return repoMsg;

        try
        {
            var file = await github.ReadFileAsync(c, full!, path, branch, MaxFileChars, ct);

            if (file is null)
                return $"{path} is a folder, not a file. Use github_files to list it.";

            var header = $"{file.Path} in {full}"
                         + (branch is null ? "" : $" on {branch}")
                         + $" — {ByteSize.Describe(file.Size)}"
                         + (file.Truncated ? $", showing the first {MaxFileChars:N0} characters" : "");

            // Fenced, not pasted. A file in a repository is written by whoever opened the
            // pull request, and it arrives at the model through the same channel our own
            // instructions use — the exact case OP2-125 built the fence for.
            return header + "\n" + Untrusted.Fence("file contents", file.Text);
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
            sb.Append($"- {i.Reference}: {Untrusted.Scrub(i.Title)}");
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
