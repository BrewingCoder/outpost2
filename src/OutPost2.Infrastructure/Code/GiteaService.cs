using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;

namespace OutPost2.Infrastructure.Code;

/// <summary>
/// Gitea and Forgejo behind the code tools (OP2-84), over <c>/api/v1</c>.
/// <para>
/// GitHub-like, not GitHub-compatible, and the differences are the whole of this class:
/// </para>
/// <list type="bullet">
/// <item>There is no search language. "Awaiting my review", "mine", "assigned to me" and
/// "mentioning me" are boolean flags on <c>repos/issues/search</c> (<c>review_requested</c>
/// needs Gitea 1.20 or Forgejo 1.20+), and that endpoint cannot be limited to one
/// repository — only to an owner — so a per-repository question is asked by owner and
/// filtered by name here.</item>
/// <item>Counts come from the <c>X-Total-Count</c> header, not a <c>total_count</c> field.</item>
/// <item>A draft is <c>pull_request.draft</c> on newer servers, and a <c>WIP:</c> title
/// prefix on every server — Gitea's original convention, still honoured by its UI.</item>
/// <item>Pages are <c>limit</c>, not <c>per_page</c>, capped by the server (50 by default).</item>
/// <item>Raw file bytes are <c>repos/{repo}/raw/{path}</c>, not a media type.</item>
/// </list>
/// <para>
/// Written against Gitea's published API (swagger, 1.20+) and Forgejo's, which serves the
/// same routes; not against a live server. Failures throw <see cref="GitHubException"/>,
/// the one type the code tools report in a sentence.
/// </para>
/// </summary>
public sealed class GiteaService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<GiteaService> log) : ICodeProvider
{
    public ProviderKind Provider => ProviderKind.Gitea;

    /// <summary>Gitea's default MAX_RESPONSE_ITEMS. Asking for more is silently clamped to it.</summary>
    private const int PageLimit = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct)
    {
        var token = await connections.GetCredentialAsync(c, ct)
            ?? throw new GitHubException($"{c.Label} has no stored credential. Reconnect it.");

        var http = GiteaConnectionService.Configure(httpFactory.CreateClient("gitea"), c.Instance, token);
        await connections.TouchAsync(c.Id, ct);
        return http;
    }

    /// <summary>The account the token belongs to, recorded when it was connected.</summary>
    private static string Me(Connection c) => c.AccountIdentifier ?? c.HomeAccountId ?? "";

    private static string Host(Connection c) =>
        Uri.TryCreate(c.Instance, UriKind.Absolute, out var u) ? u.Host : c.Label;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : class, new()
    {
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(body, Json, ct) ?? new T();
    }

    private async Task<HttpResponseMessage> GetOkAsync(
        HttpClient http, Connection c, string url, string what, CancellationToken ct)
    {
        var resp = await http.GetAsync(url, ct);
        if (resp.IsSuccessStatusCode) return resp;

        var status = (int)resp.StatusCode;
        resp.Dispose();

        throw new GitHubException(resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => $"{Host(c)} rejected the token for {c.Label}. Replace it on the connections page.",
            HttpStatusCode.Forbidden => $"{Host(c)} refused {what}: the token for {c.Label} lacks the scope for it.",
            _ => $"{Host(c)} returned HTTP {status} for {what}.",
        });
    }

    // ── search: the flags that stand in for GitHub's query language ─────────

    private static string SearchUrl(string type, string flag, int limit, string? owner = null, int page = 1) =>
        $"repos/issues/search?state=open&type={type}&{flag}=true&limit={Math.Clamp(limit, 1, PageLimit)}&page={page}"
        + (owner is null ? "" : $"&owner={Uri.EscapeDataString(owner)}");

    private async Task<int> CountAsync(HttpClient http, Connection c, string type, string flag, CancellationToken ct)
    {
        using var resp = await GetOkAsync(http, c, SearchUrl(type, flag, 1), "a search", ct);

        // The count is a header. A server or proxy that drops it leaves the page itself,
        // which with limit=1 can only say "none" or "at least one" — said as 0 or 1 rather
        // than guessed.
        if (resp.Headers.TryGetValues("X-Total-Count", out var v) && int.TryParse(v.FirstOrDefault(), out var n))
            return n;

        return (await ReadAsync<List<Issue>>(resp, ct)).Count;
    }

    public async Task<CodeTriage> TriageAsync(Connection c, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        var awaiting = await CountAsync(http, c, "pulls", "review_requested", ct);
        var assigned = await CountAsync(http, c, "issues", "assigned", ct);
        var mentions = await CountAsync(http, c, "pulls", "mentioned", ct)
                     + await CountAsync(http, c, "issues", "mentioned", ct);

        // Drafts are not a search filter, so "mine" is fetched rather than counted and the
        // drafts are told apart here. One page; beyond fifty open PRs the total still comes
        // from the header, and only the draft count is a floor.
        int mine, drafts;
        using (var resp = await GetOkAsync(http, c, SearchUrl("pulls", "created", PageLimit), "your pull requests", ct))
        {
            var page = await ReadAsync<List<Issue>>(resp, ct);
            drafts = page.Count(IsDraft);
            mine = resp.Headers.TryGetValues("X-Total-Count", out var v) && int.TryParse(v.FirstOrDefault(), out var n)
                ? n : page.Count;
        }

        log.LogInformation("Gitea triage for {Label}: {Awaiting} to review, {Mine} mine, {Assigned} issues.",
            c.Label, awaiting, mine, assigned);

        return new CodeTriage
        {
            AwaitingMyReview = awaiting,
            MyOpenPullRequests = mine,
            MyDraftPullRequests = drafts,
            IssuesAssignedToMe = assigned,
            MentioningMe = mentions,
            Login = Me(c) is { Length: > 0 } login ? login : "you",
        };
    }

    public async Task<IReadOnlyList<CodeItem>> ListPullRequestsAsync(
        Connection c, PullRequestScope scope, string? repo, int count, CancellationToken ct = default)
    {
        if (scope == PullRequestScope.AllOpen && string.IsNullOrWhiteSpace(repo))
            throw new GitHubException("Name a repository, or ask for yours or ones awaiting your review.");

        using var http = await ClientAsync(c, ct);

        if (scope == PullRequestScope.AllOpen)
            return await RepoIssuesAsync(http, c, repo!, "pulls", count, unassignedOnly: false, ct);

        var flag = scope switch
        {
            PullRequestScope.AwaitingMyReview => "review_requested",
            PullRequestScope.Mine => "created",
            _ => "mentioned",
        };

        return await SearchItemsAsync(http, c, "pulls", flag, repo, count, ct);
    }

    public async Task<IReadOnlyList<CodeItem>> ListIssuesAsync(
        Connection c, IssueScope scope, string? repo, int count, CancellationToken ct = default)
    {
        if (scope is IssueScope.Unassigned or IssueScope.AllOpen && string.IsNullOrWhiteSpace(repo))
            throw new GitHubException(
                "Name a repository for that — unassigned and all-open searches aren't limited to you.");

        using var http = await ClientAsync(c, ct);

        return scope switch
        {
            IssueScope.AssignedToMe => await SearchItemsAsync(http, c, "issues", "assigned", repo, count, ct),
            IssueScope.Mentioning => await SearchItemsAsync(http, c, "issues", "mentioned", repo, count, ct),
            _ => await RepoIssuesAsync(http, c, repo!, "issues",
                count, unassignedOnly: scope == IssueScope.Unassigned, ct),
        };
    }

    /// <summary>
    /// A personal search ("mine", "awaiting my review"), optionally for one repository.
    /// The endpoint filters by owner but not by repository, so a repository is asked for
    /// by its owner and matched by full name here — paging on until enough are found or
    /// the owner's results run out, rather than returning a short page that reads as "that
    /// is all of them".
    /// </summary>
    private async Task<IReadOnlyList<CodeItem>> SearchItemsAsync(
        HttpClient http, Connection c, string type, string flag, string? repo, int count, CancellationToken ct)
    {
        var wanted = Math.Max(count, 1);
        var target = string.IsNullOrWhiteSpace(repo) ? null : repo.Trim().Trim('/');
        var owner = target?.Split('/')[0];

        var found = new List<CodeItem>();
        for (var page = 1; page <= 5 && found.Count < wanted; page++)
        {
            using var resp = await GetOkAsync(http, c,
                SearchUrl(type, flag, target is null ? wanted : PageLimit, owner, page), "a search", ct);
            var batch = await ReadAsync<List<Issue>>(resp, ct);

            found.AddRange(batch.Select(i => Map(i, null))
                .Where(i => target is null || string.Equals(i.Repo, target, StringComparison.OrdinalIgnoreCase)));

            if (target is null || batch.Count < PageLimit) break;
        }

        return found.Take(wanted).ToList();
    }

    /// <summary>Every open issue or pull request in one repository, newest activity first.</summary>
    private async Task<IReadOnlyList<CodeItem>> RepoIssuesAsync(
        HttpClient http, Connection c, string repo, string type, int count, bool unassignedOnly, CancellationToken ct)
    {
        var name = repo.Trim().Trim('/');
        var wanted = Math.Max(count, 1);
        var found = new List<CodeItem>();

        for (var page = 1; page <= 5 && found.Count < wanted; page++)
        {
            using var resp = await http.GetAsync(
                $"repos/{name}/issues?state=open&type={type}&limit={PageLimit}&page={page}", ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new GitHubException($"No repository {name} on {Host(c)}, or this token cannot see it.");
            if (!resp.IsSuccessStatusCode)
                throw new GitHubException($"{Host(c)} returned HTTP {(int)resp.StatusCode} listing {type} on {name}.");

            var batch = await ReadAsync<List<Issue>>(resp, ct);
            found.AddRange(batch
                .Where(i => !unassignedOnly || (i.Assignees is null or { Count: 0 } && i.Assignee is null))
                .Select(i => Map(i, name)));

            if (batch.Count < PageLimit) break;
        }

        return found.OrderByDescending(i => i.UpdatedAt).Take(wanted).ToList();
    }

    // ── repositories and commits ────────────────────────────────────────────

    public async Task<IReadOnlyList<RepoRef>> ListRepositoriesAsync(Connection c, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var all = new List<RepoRef>();

        // The token's own repositories, its collaborations and its organisations'. Ten
        // pages of fifty is the same five-hundred ceiling the GitHub side uses.
        for (var page = 1; page <= 10; page++)
        {
            using var resp = await GetOkAsync(http, c, $"user/repos?limit={PageLimit}&page={page}", "your repositories", ct);
            var batch = await ReadAsync<List<Repository>>(resp, ct);

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

                // Gitea reports no separate push time; updated_at moves on a push.
                PushedAt = r.UpdatedAt,
                Url = r.HtmlUrl,
            }));

            if (batch.Count < PageLimit) break;
        }

        log.LogInformation("Listed {Count} repositories for {Label}.", all.Count, c.Label);
        return all.Where(r => r.FullName.Length > 0)
            .OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public async Task<IReadOnlyList<CommitRef>> ListCommitsAsync(
        Connection c, string repo, string? branch, int count, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var name = repo.Trim('/');

        // stat, verification and files are each an extra cost per commit on the server
        // and nothing here reads them.
        var url = $"repos/{name}/commits?limit={Math.Clamp(count, 1, PageLimit)}&stat=false&verification=false&files=false"
                  + (string.IsNullOrWhiteSpace(branch) ? "" : $"&sha={Uri.EscapeDataString(branch.Trim())}");

        using var resp = await http.GetAsync(url, ct);

        // An empty repository is 409, as on GitHub: a real answer, not a failure.
        if (resp.StatusCode == HttpStatusCode.Conflict) return [];

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubException(string.IsNullOrWhiteSpace(branch)
                ? $"No repository {name} on {Host(c)}, or this token cannot see it."
                : $"No branch '{branch}' on {name}.");

        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"{Host(c)} returned HTTP {(int)resp.StatusCode} for commits on {name}.");

        var commits = await ReadAsync<List<CommitNode>>(resp, ct);
        return commits.Select(x => new CommitRef
        {
            Sha = x.Sha ?? "",
            Message = x.Commit?.Message ?? "(no message)",
            Author = x.Author?.Login is { Length: > 0 } login ? login : x.Commit?.Author?.Name ?? "unknown",
            At = x.Commit?.Author?.Date,
            Url = x.HtmlUrl,
        }).ToList();
    }

    public async Task<CodeItem?> GetAsync(Connection c, string repo, int number, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var name = repo.Trim('/');

        // As on GitHub, the issues endpoint answers for pull requests too.
        using var resp = await http.GetAsync($"repos/{name}/issues/{number}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"{Host(c)} returned HTTP {(int)resp.StatusCode} for {name}#{number}.");

        return Map(await ReadAsync<Issue>(resp, ct), name);
    }

    // ── contents ────────────────────────────────────────────────────────────

    private static string EncodePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? ""
            : string.Join('/', path.Trim().Trim('/').Split('/')
                .Where(seg => seg.Length > 0)
                .Select(Uri.EscapeDataString));

    private static string RefQuery(string? gitRef) =>
        string.IsNullOrWhiteSpace(gitRef) ? "" : $"?ref={Uri.EscapeDataString(gitRef.Trim())}";

    private static async Task<JsonElement> ContentsAsync(
        HttpClient http, Connection c, string repo, string? path, string? gitRef, CancellationToken ct)
    {
        var encoded = EncodePath(path);

        // The root is "contents" with nothing after it; "contents/" is a different route
        // that some versions answer with 404.
        var url = $"repos/{repo.Trim('/')}/contents" + (encoded.Length == 0 ? "" : "/" + encoded) + RefQuery(gitRef);

        using var resp = await http.GetAsync(url, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            var where = string.IsNullOrWhiteSpace(path) ? repo : $"{path} in {repo}";
            throw new GitHubException(
                $"Nothing at {where}"
                + (string.IsNullOrWhiteSpace(gitRef) ? "" : $" on {gitRef}")
                + ". Either the path is wrong, the branch is wrong, or this token cannot see the repository.");
        }

        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"{Host(c)} returned HTTP {(int)resp.StatusCode} for contents of {repo}.");

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public async Task<IReadOnlyList<RepoEntry>?> ListPathAsync(
        Connection c, string repo, string? path, string? gitRef, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var root = await ContentsAsync(http, c, repo, path, gitRef, ct);

        if (root.ValueKind != JsonValueKind.Array) return null;

        return root.EnumerateArray().Select(e => new RepoEntry
            {
                Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Path = e.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                IsDirectory = e.TryGetProperty("type", out var t) && t.GetString() == "dir",
                Size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0,
            })
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<RepoFile?> ReadFileAsync(
        Connection c, string repo, string path, string? gitRef, int maxChars, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var root = await ContentsAsync(http, c, repo, path, gitRef, ct);

        if (root.ValueKind == JsonValueKind.Array) return null;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is "dir") return null;
        if (type is "submodule")
            throw new GitHubException($"{path} is a submodule — its contents live in another repository.");
        if (type is "symlink")
            throw new GitHubException($"{path} is a symlink, not a file.");

        var size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
        var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
        var encoded = root.TryGetProperty("content", out var ce) && ce.ValueKind == JsonValueKind.String
            ? ce.GetString() : null;

        // Gitea inlines base64 up to its API content limit and sends null above it; the
        // raw route serves the bytes either way.
        var raw = string.IsNullOrWhiteSpace(encoded)
            ? await RawAsync(http, c, repo, path, gitRef, ct)
            : Convert.FromBase64String(encoded.Replace("\n", "").Replace("\r", ""));

        if (raw.AsSpan(0, Math.Min(raw.Length, 8000)).IndexOf((byte)0) >= 0)
            throw new GitHubException(
                $"{path} is a binary file ({ByteSize.Describe(size)}), so there is no text to read.");

        var text = Encoding.UTF8.GetString(raw);
        var truncated = text.Length > maxChars;

        return new RepoFile
        {
            Path = path,
            Text = truncated ? text[..maxChars] : text,
            Size = size,
            Truncated = truncated,
            Url = url,
        };
    }

    private static async Task<byte[]> RawAsync(
        HttpClient http, Connection c, string repo, string path, string? gitRef, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{repo.Trim('/')}/raw/{EncodePath(path)}" + RefQuery(gitRef));
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"{Host(c)} returned HTTP {(int)resp.StatusCode} reading {path} in {repo}.");

        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // ── mapping ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A pull request is a draft by its flag where the server has one, and by Gitea's own
    /// "WIP:" title convention everywhere — which servers before the flag used exclusively.
    /// </summary>
    private static bool IsDraft(Issue i) =>
        i.PullRequest is not null
        && (i.PullRequest.Draft
            || (i.Title ?? "").TrimStart().StartsWith("WIP:", StringComparison.OrdinalIgnoreCase)
            || (i.Title ?? "").TrimStart().StartsWith("[WIP]", StringComparison.OrdinalIgnoreCase));

    private static CodeItem Map(Issue i, string? repo) => new()
    {
        Repo = i.Repository?.FullName is { Length: > 0 } full ? full : repo ?? "unknown",
        Number = i.Number,
        Title = i.Title ?? "(no title)",
        Author = i.User?.Login ?? "unknown",
        UpdatedAt = i.UpdatedAt,
        IsPullRequest = i.PullRequest is not null,
        IsDraft = IsDraft(i),
        Comments = i.Comments,
        Labels = i.Labels?.Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList() ?? [],
        Assignees = (i.Assignees ?? (i.Assignee is null ? [] : [i.Assignee]))
            .Select(a => a.Login).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList(),
        Url = i.HtmlUrl,
    };

    // ── wire shapes ─────────────────────────────────────────────────────────

    private sealed record Issue
    {
        [JsonPropertyName("number")] public int Number { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("user")] public Actor? User { get; init; }
        [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
        [JsonPropertyName("comments")] public int Comments { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("labels")] public List<Label>? Labels { get; init; }
        [JsonPropertyName("assignee")] public Actor? Assignee { get; init; }
        [JsonPropertyName("assignees")] public List<Actor>? Assignees { get; init; }
        [JsonPropertyName("repository")] public RepoMeta? Repository { get; init; }

        /// <summary>Present only on pull requests; its absence is how an issue is told apart.</summary>
        [JsonPropertyName("pull_request")] public PullMeta? PullRequest { get; init; }
    }

    private sealed record PullMeta
    {
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("merged")] public bool Merged { get; init; }
    }

    private sealed record RepoMeta([property: JsonPropertyName("full_name")] string? FullName);
    private sealed record Actor([property: JsonPropertyName("login")] string? Login);
    private sealed record Label([property: JsonPropertyName("name")] string? Name);

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
        [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    }

    private sealed record CommitNode
    {
        [JsonPropertyName("sha")] public string? Sha { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("commit")] public CommitBody? Commit { get; init; }

        /// <summary>Null when the commit's email matches no account on the server.</summary>
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
