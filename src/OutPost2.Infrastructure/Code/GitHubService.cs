using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Code;

/// <summary>One pull request or issue, flattened to what a spoken answer needs.</summary>
public sealed record CodeItem
{
    public required string Repo { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required bool IsPullRequest { get; init; }
    public required bool IsDraft { get; init; }
    public required int Comments { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public string? Url { get; init; }

    public string Reference => $"{Repo}#{Number}";
}

/// <summary>Counts for "is there anything waiting on me", answered in one breath.</summary>
public sealed record CodeTriage
{
    public required int AwaitingMyReview { get; init; }
    public required int MyOpenPullRequests { get; init; }
    public required int MyDraftPullRequests { get; init; }
    public required int IssuesAssignedToMe { get; init; }
    public required int MentioningMe { get; init; }
    public required string Login { get; init; }
}

/// <summary>A repository this token can see, named the way a person would say it.</summary>
public sealed record RepoRef
{
    public required string FullName { get; init; }
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public string? Description { get; init; }
    public bool Private { get; init; }
    public bool Fork { get; init; }
    public bool Archived { get; init; }
    public string? DefaultBranch { get; init; }
    public DateTimeOffset? PushedAt { get; init; }
    public string? Url { get; init; }
}

/// <summary>One commit, flattened to what gets read out.</summary>
public sealed record CommitRef
{
    public required string Sha { get; init; }
    public required string Message { get; init; }
    public required string Author { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Url { get; init; }

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    /// <summary>
    /// The first line only. A commit body can run to paragraphs, and "the last three
    /// commits" wants three subjects, not three essays.
    /// </summary>
    public string Subject
    {
        get
        {
            var i = Message.IndexOf('\n');
            return (i < 0 ? Message : Message[..i]).Trim();
        }
    }
}

public enum PullRequestScope { AwaitingMyReview, Mine, Mentioning, AllOpen }

public enum IssueScope { AssignedToMe, Mentioning, Unassigned, AllOpen }

public interface IGitHubService
{
    Task<CodeTriage> TriageAsync(Connection c, CancellationToken ct = default);

    Task<IReadOnlyList<RepoRef>> ListRepositoriesAsync(Connection c, CancellationToken ct = default);

    Task<IReadOnlyList<CommitRef>> ListCommitsAsync(
        Connection c, string repo, string? branch, int count, CancellationToken ct = default);

    Task<IReadOnlyList<CodeItem>> ListPullRequestsAsync(
        Connection c, PullRequestScope scope, string? repo, int count, CancellationToken ct = default);

    Task<IReadOnlyList<CodeItem>> ListIssuesAsync(
        Connection c, IssueScope scope, string? repo, int count, CancellationToken ct = default);

    Task<CodeItem?> GetAsync(Connection c, string repo, int number, CancellationToken ct = default);
}

public sealed class GitHubService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<GitHubService> log) : IGitHubService
{
    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct)
    {
        var token = await connections.GetCredentialAsync(c, ct)
            ?? throw new GitHubException($"{c.Label} has no stored credential. Reconnect it.");

        var http = httpFactory.CreateClient("github");
        http.BaseAddress = new Uri(c.Instance.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OutPost2/1.0");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        // Recorded here rather than per-operation: everything that talks to the service
        // goes through this method, so one call covers them all and none can be missed.
        // Without it "Last used" stays blank forever and the UI cannot tell a live
        // connection from one nobody has touched since the day it was added.
        await connections.TouchAsync(c.Id, ct);

        return http;
    }

    /// <summary>
    /// Builds a /search/issues call. <c>advanced_search=true</c> is sent always: GitHub is
    /// retiring the legacy parser, and pinning to the new one now means the query syntax
    /// cannot change meaning under a server-side default flip.
    /// </summary>
    private static string SearchUrl(string query, int perPage) =>
        "search/issues?advanced_search=true&sort=updated&order=desc" +
        $"&per_page={Math.Clamp(perPage, 1, 100)}&q={Uri.EscapeDataString(query)}";

    private async Task<SearchResponse> SearchAsync(HttpClient http, string query, int perPage, CancellationToken ct)
    {
        using var resp = await http.GetAsync(SearchUrl(query, perPage), ct);

        // Search has its own, much tighter rate limit than the rest of the API, and
        // hitting it mid-sentence should say so rather than surface as "0 results".
        if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            throw new GitHubException("GitHub is rate-limiting search right now. Try again shortly.");

        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"GitHub refused that search: HTTP {(int)resp.StatusCode}.");

        return await resp.Content.ReadFromJsonAsync<SearchResponse>(ct) ?? new SearchResponse(0, []);
    }

    private async Task<int> CountAsync(HttpClient http, string query, CancellationToken ct) =>
        (await SearchAsync(http, query, 1, ct)).TotalCount;

    public async Task<CodeTriage> TriageAsync(Connection c, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        // Sequential on purpose. Search is rate-limited per minute and firing five
        // parallel requests is the quickest way to get all five throttled at once.
        var awaiting = await CountAsync(http, "is:pr is:open archived:false review-requested:@me", ct);
        var mine = await CountAsync(http, "is:pr is:open archived:false author:@me", ct);
        var drafts = await CountAsync(http, "is:pr is:open archived:false author:@me draft:true", ct);
        var assigned = await CountAsync(http, "is:issue is:open archived:false assignee:@me", ct);
        // Counted as two queries, not one. Advanced search rejects a query that names
        // neither is:issue nor is:pr outright with a 422, so "anything mentioning me"
        // has to be asked twice and added up.
        var mentions = await CountAsync(http, "is:pr is:open archived:false mentions:@me", ct)
                     + await CountAsync(http, "is:issue is:open archived:false mentions:@me", ct);

        log.LogInformation("GitHub triage for {Label}: {Awaiting} to review, {Mine} mine, {Assigned} issues.",
            c.Label, awaiting, mine, assigned);

        return new CodeTriage
        {
            AwaitingMyReview = awaiting,
            MyOpenPullRequests = mine,
            MyDraftPullRequests = drafts,
            IssuesAssignedToMe = assigned,
            MentioningMe = mentions,
            Login = c.AccountIdentifier ?? c.HomeAccountId ?? "you",
        };
    }

    public async Task<IReadOnlyList<CodeItem>> ListPullRequestsAsync(
        Connection c, PullRequestScope scope, string? repo, int count, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        var q = "is:pr is:open archived:false" + RepoClause(repo) + scope switch
        {
            PullRequestScope.AwaitingMyReview => " review-requested:@me",
            PullRequestScope.Mine => " author:@me",
            PullRequestScope.Mentioning => " mentions:@me",
            _ => "",
        };

        // "Every open PR on GitHub" is not a question anyone means to ask, and search
        // refuses an unscoped query anyway. Say which of the two fixes applies.
        if (scope == PullRequestScope.AllOpen && string.IsNullOrWhiteSpace(repo))
            throw new GitHubException("Name a repository, or ask for yours or ones awaiting your review.");

        return (await SearchAsync(http, q, count, ct)).Items.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<CodeItem>> ListIssuesAsync(
        Connection c, IssueScope scope, string? repo, int count, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        var q = "is:issue is:open archived:false" + RepoClause(repo) + scope switch
        {
            IssueScope.AssignedToMe => " assignee:@me",
            IssueScope.Mentioning => " mentions:@me",
            IssueScope.Unassigned => " no:assignee",
            _ => "",
        };

        // Unassigned and AllOpen have no personal anchor, so without a repository they
        // would sweep all of GitHub.
        if (scope is IssueScope.Unassigned or IssueScope.AllOpen && string.IsNullOrWhiteSpace(repo))
            throw new GitHubException(
                "Name a repository for that — unassigned and all-open searches aren't limited to you.");

        return (await SearchAsync(http, q, count, ct)).Items.Select(Map).ToList();
    }

    /// <summary>
    /// Every repository the token can reach, newest activity first.
    /// <para>
    /// There was no way to ask this, and its absence was invisible: asked whether it could
    /// see a repository at all, the only honest answer was "I cannot tell". A product that
    /// takes a repository name by voice has to be able to say which names it knows.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RepoRef>> ListRepositoriesAsync(
        Connection c, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        var all = new List<RepoRef>();

        // Paged to a ceiling rather than exhaustively. Five hundred covers any account a
        // person resolves names against out loud, and an account in hundreds of org repos
        // should not pay an unbounded walk on a question asked while driving.
        for (var page = 1; page <= 5; page++)
        {
            using var resp = await http.GetAsync(
                "user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member" +
                $"&page={page}", ct);

            if (!resp.IsSuccessStatusCode)
                throw new GitHubException($"GitHub returned HTTP {(int)resp.StatusCode} listing repositories.");

            var batch = await resp.Content.ReadFromJsonAsync<List<Repository>>(ct) ?? [];
            all.AddRange(batch.Select(r => new RepoRef
            {
                FullName = r.FullName ?? "",
                Name = r.Name ?? "",
                Owner = r.Owner?.Login ?? "",
                Description = r.Description,
                Private = r.Private,
                Fork = r.Fork,
                Archived = r.Archived,
                DefaultBranch = r.DefaultBranch,
                PushedAt = r.PushedAt,
                Url = r.HtmlUrl,
            }));

            if (batch.Count < 100) break;
        }

        log.LogInformation("Listed {Count} repositories for {Label}.", all.Count, c.Label);
        return all.Where(r => r.FullName.Length > 0).ToList();
    }

    public async Task<IReadOnlyList<CommitRef>> ListCommitsAsync(
        Connection c, string repo, string? branch, int count, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        var url = $"repos/{repo.Trim('/')}/commits?per_page={Math.Clamp(count, 1, 100)}" +
                  (string.IsNullOrWhiteSpace(branch) ? "" : $"&sha={Uri.EscapeDataString(branch.Trim())}");

        using var resp = await http.GetAsync(url, ct);

        // 409 is an empty repository, which is a real answer rather than a failure.
        if ((int)resp.StatusCode == 409) return [];

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new GitHubException(string.IsNullOrWhiteSpace(branch)
                ? $"No repository {repo}, or this token cannot see it."
                : $"No branch '{branch}' on {repo}.");

        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"GitHub returned HTTP {(int)resp.StatusCode} for commits on {repo}.");

        var commits = await resp.Content.ReadFromJsonAsync<List<CommitNode>>(ct) ?? [];

        return commits.Select(x => new CommitRef
        {
            Sha = x.Sha ?? "",
            Message = x.Commit?.Message ?? "(no message)",
            // The commit author is who wrote it; the linked GitHub account may not exist
            // at all for a commit made under an address with no account attached.
            Author = x.Author?.Login ?? x.Commit?.Author?.Name ?? "unknown",
            At = x.Commit?.Author?.Date,
            Url = x.HtmlUrl,
        }).ToList();
    }

    public async Task<CodeItem?> GetAsync(Connection c, string repo, int number, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        // The issues endpoint serves pull requests too, and is the only one of the two
        // that works for both — so one lookup answers "tell me about owner/repo#12"
        // without first knowing which kind it is.
        using var resp = await http.GetAsync($"repos/{repo.Trim('/')}/issues/{number}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"GitHub returned HTTP {(int)resp.StatusCode} for {repo}#{number}.");

        var item = await resp.Content.ReadFromJsonAsync<SearchItem>(ct);
        return item is null ? null : Map(item with { RepositoryUrl = $"x/repos/{repo}" });
    }

    private static string RepoClause(string? repo) =>
        string.IsNullOrWhiteSpace(repo) ? "" : $" repo:{repo.Trim().Trim('/')}";

    private static CodeItem Map(SearchItem i) => new()
    {
        // Search returns the API URL of the repo rather than its name; everything after
        // /repos/ is the owner/name pair a person would actually say.
        Repo = i.RepositoryUrl is { Length: > 0 } u && u.Contains("/repos/")
            ? u[(u.IndexOf("/repos/", StringComparison.Ordinal) + 7)..]
            : "unknown",
        Number = i.Number,
        Title = i.Title ?? "(no title)",
        Author = i.User?.Login ?? "unknown",
        UpdatedAt = i.UpdatedAt,
        IsPullRequest = i.PullRequest is not null,
        IsDraft = i.Draft,
        Comments = i.Comments,
        Labels = i.Labels?.Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [],
        Assignees = i.Assignees?.Select(a => a.Login).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [],
        Url = i.HtmlUrl,
    };

    private sealed record SearchResponse(
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("items")] List<SearchItem> Items);

    private sealed record SearchItem
    {
        [JsonPropertyName("repository_url")] public string? RepositoryUrl { get; init; }
        [JsonPropertyName("number")] public int Number { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("user")] public Actor? User { get; init; }
        [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("comments")] public int Comments { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("labels")] public List<Label>? Labels { get; init; }
        [JsonPropertyName("assignees")] public List<Actor>? Assignees { get; init; }

        /// <summary>Present only on pull requests; its absence is how an issue is told apart.</summary>
        [JsonPropertyName("pull_request")] public JsonElement? PullRequest { get; init; }
    }

    private sealed record Actor([property: JsonPropertyName("login")] string Login);
    private sealed record Label([property: JsonPropertyName("name")] string Name);

    private sealed record Repository
    {
        [JsonPropertyName("full_name")] public string? FullName { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("owner")] public Actor? Owner { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("private")] public bool Private { get; init; }
        [JsonPropertyName("fork")] public bool Fork { get; init; }
        [JsonPropertyName("archived")] public bool Archived { get; init; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }
        [JsonPropertyName("pushed_at")] public DateTimeOffset? PushedAt { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    }

    private sealed record CommitNode
    {
        [JsonPropertyName("sha")] public string? Sha { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("commit")] public CommitBody? Commit { get; init; }

        /// <summary>Null when the commit's email matches no GitHub account.</summary>
        [JsonPropertyName("author")] public Actor? Author { get; init; }
    }

    private sealed record CommitBody
    {
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("author")] public CommitSignature? Author { get; init; }
    }

    private sealed record CommitSignature
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("date")] public DateTimeOffset? Date { get; init; }
    }
}

public sealed class GitHubException(string message) : Exception(message);
