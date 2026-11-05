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
/// Bitbucket Cloud behind the code tools (OP2-293), over <c>api.bitbucket.org/2.0</c>, one
/// workspace per connection.
/// <para>
/// GitHub-shaped in places and not wire-compatible anywhere, and the differences are the
/// whole of this class:
/// </para>
/// <list type="bullet">
/// <item>There is no issue tracker. Atlassian retired Bitbucket Issues in August 2026 and
/// removed its API, so issue questions are answered with that sentence, not a 404.</item>
/// <item>There is no cross-repository search. "Mine" has a workspace endpoint; "awaiting my
/// review" does not, so it is assembled from the most recently updated repositories, and
/// the answer says how many it covered.</item>
/// <item>There is no "review requested". Reviewers are <c>participants[]</c> with
/// <c>role: REVIEWER</c>, so "awaiting my review" is mapped onto "a reviewer who has neither
/// approved nor requested changes", and the mapping is said with the answer.</item>
/// <item>Pages are <c>{values, next, size?}</c> with <c>next</c> a full URL, followed only
/// while it stays on the API host — the URL comes from the response, and the token goes
/// wherever it points.</item>
/// <item>Files are raw bytes from <c>src/{commit}/{path}</c>, with <c>?format=meta</c> for
/// what kind of entry a path is; a branch name with a slash cannot sit in that path and is
/// resolved to its commit first.</item>
/// <item>"No branch named" means the repository's <c>mainbranch</c>, looked up: the plain
/// commits listing walks every branch, not the default one.</item>
/// </list>
/// <para>
/// Written against Atlassian's published OpenAPI description of the 2.0 API, not against
/// a live account. Failures throw <see cref="GitHubException"/>, the one type the code
/// tools report in a sentence, and every one of them names Bitbucket.
/// </para>
/// </summary>
public sealed class BitbucketService(
    IHttpClientFactory httpFactory,
    IConnectionStore connections,
    ILogger<BitbucketService> log) : ICodeProvider
{
    public ProviderKind Provider => ProviderKind.Bitbucket;

    /// <summary>The pull-request endpoints' maximum <c>pagelen</c>.</summary>
    private const int PullPage = 50;

    /// <summary>The repository and source endpoints' maximum <c>pagelen</c>.</summary>
    private const int ListPage = 100;

    /// <summary>
    /// How many repositories a review question without a repository looks in. Bitbucket
    /// has no search across them, so it is one request (or more) per repository; the most
    /// recently updated are where open reviews are, and the answer says this is a subset.
    /// </summary>
    public const int ReviewSweep = 25;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> ClientAsync(Connection c, CancellationToken ct)
    {
        var credential = await connections.GetCredentialAsync(c, ct)
            ?? throw new GitHubException($"{c.Label} has no stored Bitbucket credential. Reconnect it.");

        var http = BitbucketConnectionService.Configure(httpFactory.CreateClient("bitbucket"), credential);
        await connections.TouchAsync(c.Id, ct);
        return http;
    }

    private static string Workspace(Connection c) => BitbucketConnectionService.WorkspaceOf(c);

    /// <summary>The account's uuid, recorded when it was connected; reviewer and author checks match on it.</summary>
    private static string Me(Connection c) =>
        c.HomeAccountId is { Length: > 0 } uuid
            ? uuid
            : throw new GitHubException(
                $"{c.Label} doesn't record which Bitbucket account it is. Replace its token on the connections page.");

    private static bool SameAccount(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(a.Trim().Trim('{', '}'), b.Trim().Trim('{', '}'), StringComparison.OrdinalIgnoreCase);

    // ── addressing ──────────────────────────────────────────────────────────

    private static (string Workspace, string Slug) Split(Connection c, string repo)
    {
        var parts = repo.Trim().Trim('/').Split('/', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (Workspace(c), parts[0]);
    }

    private static string RepoPath(Connection c, string repo)
    {
        var (ws, slug) = Split(c, repo);
        return $"repositories/{Uri.EscapeDataString(ws)}/{Uri.EscapeDataString(slug)}";
    }

    private static string FullName(Connection c, string repo)
    {
        var (ws, slug) = Split(c, repo);
        return $"{ws}/{slug}";
    }

    private static string EncodePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? ""
            : string.Join('/', path.Trim().Trim('/').Split('/')
                .Where(seg => seg.Length > 0)
                .Select(Uri.EscapeDataString));

    /// <summary>
    /// The next page, or null. <c>next</c> is a full URL taken from the response; it is
    /// followed only on the API's own host, so a page that points elsewhere can't collect
    /// the token.
    /// </summary>
    private static Uri? Next(string? next) =>
        Uri.TryCreate(next, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, "api.bitbucket.org", StringComparison.OrdinalIgnoreCase)
            ? u
            : null;

    // ── requests ────────────────────────────────────────────────────────────

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : class, new()
    {
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        try { return await JsonSerializer.DeserializeAsync<T>(body, Json, ct) ?? new T(); }
        catch (JsonException) { throw new GitHubException("Bitbucket returned a response Outpost couldn't read."); }
    }

    private static string Failure(Connection c, HttpStatusCode status, string what) => status switch
    {
        HttpStatusCode.Unauthorized =>
            $"Bitbucket rejected the token for {c.Label}. Replace it on the connections page.",
        HttpStatusCode.Forbidden =>
            $"Bitbucket refused {what}: the token for {c.Label} lacks the scope for it, or the account can't see it.",
        HttpStatusCode.TooManyRequests =>
            $"Bitbucket is rate-limiting {c.Label} right now. Try again in a minute.",
        _ => $"Bitbucket returned HTTP {(int)status} for {what}.",
    };

    /// <summary>A GET that succeeds or throws the sentence for its failure.</summary>
    private static async Task<HttpResponseMessage> GetOkAsync(
        HttpClient http, Connection c, string url, string what, string? notFound, CancellationToken ct)
    {
        var resp = await http.GetAsync(url, ct);
        if (resp.IsSuccessStatusCode) return resp;

        var status = resp.StatusCode;
        resp.Dispose();

        throw new GitHubException(status == HttpStatusCode.NotFound && notFound is not null
            ? notFound
            : Failure(c, status, what));
    }

    private static Task<HttpResponseMessage> GetOkAsync(
        HttpClient http, Connection c, Uri url, string what, string? notFound, CancellationToken ct) =>
        GetOkAsync(http, c, url.ToString(), what, notFound, ct);

    /// <summary>Pages of a listing, until <paramref name="enough"/> says stop or the pages run out.</summary>
    private static async Task<(List<T> Items, int? Size, bool More)> CollectAsync<T>(
        HttpClient http, Connection c, string firstUrl, string what, string? notFound,
        int maxPages, Func<List<T>, bool> enough, CancellationToken ct)
    {
        var items = new List<T>();
        int? size = null;
        string? next = firstUrl;

        for (var page = 0; page < maxPages && next is not null; page++)
        {
            using var resp = await GetOkAsync(http, c, next, what, notFound, ct);
            var body = await ReadAsync<Page<T>>(resp, ct);

            items.AddRange(body.Values ?? []);
            size ??= body.Size;
            next = Next(body.Next)?.ToString();

            if (enough(items)) break;
        }

        return (items, size, next is not null);
    }

    private async Task<string?> MainBranchAsync(HttpClient http, Connection c, string repo, CancellationToken ct)
    {
        using var resp = await GetOkAsync(http, c, RepoPath(c, repo) + "?fields=mainbranch.name", $"{FullName(c, repo)}",
            $"No repository {FullName(c, repo)} on Bitbucket, or this token cannot see it.", ct);
        return (await ReadAsync<Repository>(resp, ct)).MainBranch?.Name;
    }

    /// <summary>
    /// The revision to put in a <c>src/{commit}/</c> path. A branch name with a slash in it
    /// ("feature/login") would read as part of the file path, so it is resolved to its
    /// commit; a plain name goes as it is.
    /// </summary>
    private async Task<(string Rev, string Shown)> RevisionAsync(
        HttpClient http, Connection c, string repo, string? gitRef, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(gitRef)
            ? await MainBranchAsync(http, c, repo, ct)
              ?? throw new GitHubException($"{FullName(c, repo)} on Bitbucket is empty: it has no branches yet.")
            : gitRef.Trim();

        if (!name.Contains('/')) return (Uri.EscapeDataString(name), name);

        foreach (var kind in new[] { "branches", "tags" })
        {
            using var resp = await http.GetAsync($"{RepoPath(c, repo)}/refs/{kind}/{Uri.EscapeDataString(name)}", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) continue;
            if (!resp.IsSuccessStatusCode)
                throw new GitHubException(Failure(c, resp.StatusCode, $"the {name} reference on {FullName(c, repo)}"));

            var hash = (await ReadAsync<Ref>(resp, ct)).Target?.Hash;
            if (!string.IsNullOrWhiteSpace(hash)) return (hash, name);
        }

        throw new GitHubException($"No branch or tag '{name}' on {FullName(c, repo)} on Bitbucket.");
    }

    // ── pull requests ───────────────────────────────────────────────────────

    /// <summary>Open pull requests in one repository, with their participants.</summary>
    private static string RepoPullsUrl(Connection c, string repo) =>
        $"{RepoPath(c, repo)}/pullrequests?state=OPEN&pagelen={PullPage}&sort=-updated_on"
        + "&fields=%2Bvalues.participants";

    /// <summary>Open pull requests the account authored, anywhere in a workspace.</summary>
    private static string MinePullsUrl(string workspace, string me) =>
        $"workspaces/{Uri.EscapeDataString(workspace)}/pullrequests/{Uri.EscapeDataString(me)}"
        + $"?state=OPEN&pagelen={PullPage}&sort=-updated_on";

    /// <summary>
    /// The mapping for "awaiting my review": listed as a reviewer, and neither approved
    /// nor requested changes yet. Bitbucket has no separate "review requested" state.
    /// </summary>
    private static bool AwaitsReviewFrom(Pull p, string me) =>
        !SameAccount(p.Author?.Uuid, me)
        && (p.Participants ?? []).Any(x =>
            string.Equals(x.Role, "REVIEWER", StringComparison.OrdinalIgnoreCase)
            && SameAccount(x.User?.Uuid, me)
            && !x.Approved
            && x.State is not ("approved" or "changes_requested"));

    public string? ScopeNote(Connection c, PullRequestScope scope) => scope switch
    {
        PullRequestScope.AwaitingMyReview =>
            "On Bitbucket, \"awaiting your review\" means open pull requests that list you as a reviewer and "
            + "that you haven't yet approved or requested changes on: Bitbucket has no \"review requested\" state. "
            + $"Without a repository named, the {ReviewSweep} most recently updated repositories in the "
            + $"{Workspace(c)} workspace are checked.",
        _ => null,
    };

    public async Task<IReadOnlyList<CodeItem>> ListPullRequestsAsync(
        Connection c, PullRequestScope scope, string? repo, int count, CancellationToken ct = default)
    {
        var wanted = Math.Max(count, 1);
        var target = string.IsNullOrWhiteSpace(repo) ? null : repo.Trim().Trim('/');

        if (scope == PullRequestScope.Mentioning)
            throw new GitHubException(
                "Bitbucket has no search for pull requests that mention you, so Outpost can't list those. "
                + "Ask for ones awaiting your review, your own, or all open in a repository.");

        if (scope == PullRequestScope.AllOpen && target is null)
            throw new GitHubException("Name a repository, or ask for yours or ones awaiting your review.");

        using var http = await ClientAsync(c, ct);

        switch (scope)
        {
            case PullRequestScope.AllOpen:
            {
                var (pulls, _, _) = await CollectAsync<Pull>(http, c, RepoPullsUrl(c, target!),
                    $"pull requests on {FullName(c, target!)}", NoRepo(c, target!), 5, l => l.Count >= wanted, ct);
                return pulls.Select(p => Map(p, FullName(c, target!))).Take(wanted).ToList();
            }

            case PullRequestScope.Mine:
            {
                var me = Me(c);
                var ws = target is null ? Workspace(c) : Split(c, target).Workspace;
                var full = target is null ? null : FullName(c, target);

                var (pulls, _, _) = await CollectAsync<Pull>(http, c, MinePullsUrl(ws, me),
                    "your pull requests", $"Bitbucket has no workspace {ws}, or this token cannot see it.", 5,
                    l => l.Count(p => full is null || Matches(p, full)) >= wanted, ct);

                return pulls.Where(p => full is null || Matches(p, full))
                    .Select(p => Map(p, full)).Take(wanted).ToList();
            }

            default: // AwaitingMyReview
            {
                var me = Me(c);
                if (target is not null)
                {
                    var (pulls, _, _) = await CollectAsync<Pull>(http, c, RepoPullsUrl(c, target),
                        $"pull requests on {FullName(c, target)}", NoRepo(c, target), 5,
                        l => l.Count(p => AwaitsReviewFrom(p, me)) >= wanted, ct);
                    return pulls.Where(p => AwaitsReviewFrom(p, me))
                        .Select(p => Map(p, FullName(c, target))).Take(wanted).ToList();
                }

                var sweep = await SweepForReviewsAsync(http, c, me, ct);
                return sweep.Items.Take(wanted).ToList();
            }
        }
    }

    private static string NoRepo(Connection c, string repo) =>
        $"No repository {FullName(c, repo)} on Bitbucket, or this token cannot see it.";

    private static bool Matches(Pull p, string fullName) =>
        string.Equals(p.Destination?.Repository?.FullName, fullName, StringComparison.OrdinalIgnoreCase);

    private sealed record Sweep(List<CodeItem> Items, int Checked, int? Total, int Failed);

    /// <summary>
    /// "Awaiting my review" across a workspace, assembled one repository at a time from the
    /// most recently updated, because Bitbucket can't be asked it in one request. Four at a
    /// time; a repository that fails is counted and said, not allowed to sink the rest.
    /// </summary>
    private async Task<Sweep> SweepForReviewsAsync(HttpClient http, Connection c, string me, CancellationToken ct)
    {
        var ws = Workspace(c);
        List<Repository> repos;
        int? total;
        using (var resp = await GetOkAsync(http, c,
                   $"repositories/{Uri.EscapeDataString(ws)}?pagelen={ReviewSweep}&sort=-updated_on",
                   $"repositories in {ws}", $"Bitbucket has no workspace {ws}, or this token cannot see it.", ct))
        {
            var page = await ReadAsync<Page<Repository>>(resp, ct);
            repos = page.Values ?? [];
            total = page.Size;
        }

        var gate = new SemaphoreSlim(4);
        var failed = 0;
        var found = await Task.WhenAll(repos.Where(r => r.FullName is { Length: > 0 }).Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var (pulls, _, _) = await CollectAsync<Pull>(http, c, RepoPullsUrl(c, r.FullName!),
                    $"pull requests on {r.FullName}", null, 2, _ => false, ct);
                return pulls.Where(p => AwaitsReviewFrom(p, me)).Select(p => Map(p, r.FullName)).ToList();
            }
            catch (GitHubException ex)
            {
                Interlocked.Increment(ref failed);
                log.LogWarning("Bitbucket review sweep skipped {Repo} for {Label}: {Why}", r.FullName, c.Label, ex.Message);
                return [];
            }
            finally { gate.Release(); }
        }));

        return new Sweep(
            found.SelectMany(x => x).OrderByDescending(i => i.UpdatedAt).ToList(),
            repos.Count, total, failed);
    }

    public async Task<CodeTriage> TriageAsync(Connection c, CancellationToken ct = default)
    {
        var me = Me(c);
        var ws = Workspace(c);
        using var http = await ClientAsync(c, ct);

        var sweep = await SweepForReviewsAsync(http, c, me, ct);

        var (mine, size, more) = await CollectAsync<Pull>(http, c, MinePullsUrl(ws, me),
            "your pull requests", $"Bitbucket has no workspace {ws}, or this token cannot see it.", 5, _ => false, ct);

        // size is optional on Bitbucket pages; without it, what was paged through is the count.
        var myCount = size ?? mine.Count;
        var drafts = mine.Count(p => p.Draft);

        var notes = new List<string>
        {
            "\"Waiting on your review\" means open pull requests listing you as a reviewer that you haven't "
            + "approved or requested changes on — Bitbucket has no \"review requested\" state.",
            sweep.Total is { } t && t > sweep.Checked
                ? $"Reviews were checked in the {sweep.Checked} most recently updated of the {t} repositories in {ws}; "
                  + "name a repository with github_pull_requests to look in one of the others."
                : sweep.Total is null && sweep.Checked >= ReviewSweep
                    ? $"Reviews were checked in the {sweep.Checked} most recently updated repositories in {ws}; there may be more."
                    : $"Reviews were checked in all {sweep.Checked} repositories in {ws}.",
        };
        if (sweep.Failed > 0)
            notes.Add($"{sweep.Failed} of those repositories couldn't be read, so the review count may be short.");
        if (size is null && more)
            notes.Add($"You have more open pull requests than the {mine.Count} counted.");
        notes.Add("Bitbucket retired its issue tracker in 2026 and has no mention search, so neither is counted.");

        log.LogInformation("Bitbucket triage for {Label}: {Awaiting} to review in {Checked} repos, {Mine} mine.",
            c.Label, sweep.Items.Count, sweep.Checked, myCount);

        return new CodeTriage
        {
            AwaitingMyReview = sweep.Items.Count,
            MyOpenPullRequests = myCount,
            MyDraftPullRequests = drafts,
            IssuesAssignedToMe = 0,
            MentioningMe = 0,
            CountsIssues = false,
            Notes = notes,
            Login = c.AccountIdentifier is { Length: > 0 } login ? login : "you",
        };
    }

    // ── issues: there is no tracker ─────────────────────────────────────────

    public Task<IReadOnlyList<CodeItem>> ListIssuesAsync(
        Connection c, IssueScope scope, string? repo, int count, CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(repo) ? $"the {Workspace(c)} workspace" : FullName(c, repo);
        return Task.FromException<IReadOnlyList<CodeItem>>(new GitHubException(
            $"Bitbucket has no issue tracker, so {where} has no issues to list: Atlassian retired Bitbucket Issues "
            + "in August 2026. A Bitbucket team's issues are usually in Jira. Pull requests are still here — "
            + "try github_pull_requests."));
    }

    // ── repositories and commits ────────────────────────────────────────────

    public async Task<IReadOnlyList<RepoRef>> ListRepositoriesAsync(Connection c, CancellationToken ct = default)
    {
        var ws = Workspace(c);
        using var http = await ClientAsync(c, ct);

        // Five pages of a hundred: the same five-hundred ceiling as GitHub and Gitea.
        var (repos, _, _) = await CollectAsync<Repository>(http, c,
            $"repositories/{Uri.EscapeDataString(ws)}?pagelen={ListPage}&sort=-updated_on",
            $"repositories in {ws}", $"Bitbucket has no workspace {ws}, or this token cannot see it.",
            5, _ => false, ct);

        log.LogInformation("Listed {Count} Bitbucket repositories for {Label}.", repos.Count, c.Label);

        return repos
            .Where(r => r.FullName is { Length: > 0 })
            .Select(r =>
            {
                var slash = r.FullName!.IndexOf('/');
                return new RepoRef
                {
                    FullName = r.FullName!,
                    // The slug, not the display name: it is what the URL and full_name
                    // use, and what a bare name is matched against.
                    Name = r.Slug ?? (slash >= 0 ? r.FullName![(slash + 1)..] : r.FullName!),
                    Owner = slash >= 0 ? r.FullName![..slash] : ws,
                    Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description,
                    Private = r.IsPrivate,
                    Fork = r.Parent is not null,
                    // Bitbucket has no archived flag.
                    Archived = false,
                    DefaultBranch = r.MainBranch?.Name,
                    // No pushed_at either; updated_on moves on a push.
                    PushedAt = r.UpdatedOn,
                    Url = r.Links?.Html?.Href,
                };
            })
            .OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public async Task<IReadOnlyList<CommitRef>> ListCommitsAsync(
        Connection c, string repo, string? branch, int count, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var full = FullName(c, repo);

        // "commits" with no revision walks every branch; the default branch has to be named.
        var rev = string.IsNullOrWhiteSpace(branch) ? await MainBranchAsync(http, c, repo, ct) : branch.Trim();
        if (rev is null) return []; // an empty repository has no main branch yet

        var wanted = Math.Max(count, 1);
        var (commits, _, _) = await CollectAsync<Commit>(http, c,
            $"{RepoPath(c, repo)}/commits/{Uri.EscapeDataString(rev)}?pagelen={Math.Clamp(wanted, 1, ListPage)}",
            $"commits on {full}",
            string.IsNullOrWhiteSpace(branch)
                ? $"No repository {full} on Bitbucket, or this token cannot see it."
                : $"No branch '{branch}' on {full} on Bitbucket.",
            3, l => l.Count >= wanted, ct);

        return commits.Take(wanted).Select(x => new CommitRef
        {
            Sha = x.Hash ?? "",
            Message = string.IsNullOrWhiteSpace(x.Message) ? "(no message)" : x.Message!,
            Author = AuthorOf(x.Author),
            At = x.Date,
            Url = x.Links?.Html?.Href,
        }).ToList();
    }

    /// <summary>
    /// The mapped account when the commit email belongs to one; otherwise only the raw
    /// "Name &lt;email&gt;" string exists, and the name part is what gets said.
    /// </summary>
    private static string AuthorOf(CommitAuthor? a)
    {
        if (a?.User is { } u && (u.DisplayName ?? u.Nickname) is { Length: > 0 } name) return name;

        var raw = a?.Raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return "unknown";

        var lt = raw.IndexOf('<');
        return lt > 0 ? raw[..lt].Trim() : raw.Trim('<', '>');
    }

    public async Task<CodeItem?> GetAsync(Connection c, string repo, int number, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);

        // Only pull requests: there are no issues, and a pull request's number is its own
        // sequence per repository, not shared with anything.
        using var resp = await http.GetAsync($"{RepoPath(c, repo)}/pullrequests/{number}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
            throw new GitHubException(Failure(c, resp.StatusCode, $"pull request {FullName(c, repo)}#{number}"));

        return Map(await ReadAsync<Pull>(resp, ct), FullName(c, repo));
    }

    // ── contents ────────────────────────────────────────────────────────────

    private static string Where(string full, string? path, string? gitRef) =>
        (string.IsNullOrWhiteSpace(path) ? full : $"{path} in {full}")
        + (string.IsNullOrWhiteSpace(gitRef) ? "" : $" on {gitRef}");

    /// <summary>
    /// What kind of entry a path is, from <c>?format=meta</c>. A directory may answer with
    /// its own description or with its listing; both are told apart from a file.
    /// </summary>
    private static async Task<(bool IsDirectory, JsonElement Meta)> MetaAsync(
        HttpClient http, Connection c, string repo, string rev, string path, string? gitRef, CancellationToken ct)
    {
        var full = FullName(c, repo);
        using var resp = await GetOkAsync(http, c,
            $"{RepoPath(c, repo)}/src/{rev}/{EncodePath(path)}?format=meta", $"{path} in {full}",
            $"Nothing at {Where(full, path, gitRef)} on Bitbucket. Either the path is wrong, the branch is wrong, "
            + "or this token cannot see the repository.", ct);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement.Clone();

        var isDir = root.ValueKind == JsonValueKind.Object
                    && (root.TryGetProperty("values", out _)
                        || (root.TryGetProperty("type", out var t) && t.GetString() == "commit_directory"));
        return (isDir, root);
    }

    public async Task<IReadOnlyList<RepoEntry>?> ListPathAsync(
        Connection c, string repo, string? path, string? gitRef, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var full = FullName(c, repo);
        var (rev, _) = await RevisionAsync(http, c, repo, gitRef, ct);

        if (!string.IsNullOrWhiteSpace(path))
        {
            var (isDir, _) = await MetaAsync(http, c, repo, rev, path, gitRef, ct);
            if (!isDir) return null;
        }

        // A directory listing is the same route with a trailing slash.
        var encoded = EncodePath(path);
        var (entries, _, _) = await CollectAsync<TreeEntry>(http, c,
            $"{RepoPath(c, repo)}/src/{rev}/{(encoded.Length == 0 ? "" : encoded + "/")}?pagelen={ListPage}",
            $"the contents of {Where(full, path, null)}",
            $"Nothing at {Where(full, path, gitRef)} on Bitbucket. Either the path is wrong, the branch is wrong, "
            + "or this token cannot see the repository.",
            10, _ => false, ct);

        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Select(e => new RepoEntry
            {
                Name = e.Path!.TrimEnd('/').Split('/')[^1],
                Path = e.Path!.TrimEnd('/'),
                IsDirectory = e.Type == "commit_directory",
                Size = e.Type == "commit_file" ? e.Size ?? 0 : 0,
            })
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<RepoFile?> ReadFileAsync(
        Connection c, string repo, string path, string? gitRef, int maxChars, CancellationToken ct = default)
    {
        using var http = await ClientAsync(c, ct);
        var full = FullName(c, repo);
        var (rev, shown) = await RevisionAsync(http, c, repo, gitRef, ct);

        var (isDir, meta) = await MetaAsync(http, c, repo, rev, path, gitRef, ct);
        if (isDir) return null;

        var attributes = meta.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString()).ToHashSet()
            : [];
        var size = meta.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;

        if (attributes.Contains("subrepository"))
            throw new GitHubException($"{path} is a submodule — its contents live in another repository.");
        if (attributes.Contains("link"))
            throw new GitHubException($"{path} is a symlink, not a file.");
        if (attributes.Contains("lfs"))
            throw new GitHubException(
                $"{path} is stored in Git LFS ({ByteSize.Describe(size)}); Bitbucket serves it from separate storage, "
                + "so there is no text to read here.");
        if (attributes.Contains("binary"))
            throw new GitHubException($"{path} is a binary file ({ByteSize.Describe(size)}), so there is no text to read.");

        // The raw bytes, which is what this route serves for a file. Accept anything: its
        // content type follows the file's extension.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{RepoPath(c, repo)}/src/{rev}/{EncodePath(path)}");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GitHubException(Failure(c, resp.StatusCode, $"{path} in {full}"));

        var raw = await resp.Content.ReadAsByteArrayAsync(ct);

        if (raw.AsSpan(0, Math.Min(raw.Length, 8000)).IndexOf((byte)0) >= 0)
            throw new GitHubException(
                $"{path} is a binary file ({ByteSize.Describe(size > 0 ? size : raw.Length)}), so there is no text to read.");

        var text = Encoding.UTF8.GetString(raw);
        var truncated = text.Length > maxChars;

        return new RepoFile
        {
            Path = path,
            Text = truncated ? text[..maxChars] : text,
            Size = size > 0 ? size : raw.Length,
            Truncated = truncated,
            Url = $"{BitbucketConnectionService.SiteBase}{full}/src/{Uri.EscapeDataString(shown)}/{EncodePath(path)}",
        };
    }

    // ── mapping ─────────────────────────────────────────────────────────────

    private static CodeItem Map(Pull p, string? repo) => new()
    {
        Repo = p.Destination?.Repository?.FullName is { Length: > 0 } full ? full : repo ?? "unknown",
        Number = p.Id,
        Title = p.Title ?? "(no title)",
        Author = p.Author?.DisplayName ?? p.Author?.Nickname ?? "unknown",
        UpdatedAt = p.UpdatedOn,
        IsPullRequest = true,
        IsDraft = p.Draft,
        Comments = p.CommentCount,

        // Bitbucket pull requests have neither labels nor assignees; reviewers are a
        // different thing and are not passed off as either.
        Labels = [],
        Assignees = [],
        Url = p.Links?.Html?.Href,
    };

    // ── wire shapes ─────────────────────────────────────────────────────────

    private sealed record Page<T>
    {
        [JsonPropertyName("values")] public List<T>? Values { get; init; }
        [JsonPropertyName("next")] public string? Next { get; init; }

        /// <summary>Optional: Bitbucket leaves it out where counting would be expensive.</summary>
        [JsonPropertyName("size")] public int? Size { get; init; }
    }

    private sealed record Account
    {
        [JsonPropertyName("uuid")] public string? Uuid { get; init; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
    }

    private sealed record Participant
    {
        [JsonPropertyName("user")] public Account? User { get; init; }
        [JsonPropertyName("role")] public string? Role { get; init; }
        [JsonPropertyName("approved")] public bool Approved { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
    }

    private sealed record Link([property: JsonPropertyName("href")] string? Href);

    private sealed record Links([property: JsonPropertyName("html")] Link? Html);

    private sealed record RepoMeta([property: JsonPropertyName("full_name")] string? FullName);

    private sealed record PullEndpoint([property: JsonPropertyName("repository")] RepoMeta? Repository);

    private sealed record Pull
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("author")] public Account? Author { get; init; }
        [JsonPropertyName("comment_count")] public int CommentCount { get; init; }
        [JsonPropertyName("updated_on")] public DateTimeOffset UpdatedOn { get; init; }
        [JsonPropertyName("links")] public Links? Links { get; init; }
        [JsonPropertyName("participants")] public List<Participant>? Participants { get; init; }
        [JsonPropertyName("destination")] public PullEndpoint? Destination { get; init; }
    }

    private sealed record Branch([property: JsonPropertyName("name")] string? Name);

    private sealed record Repository
    {
        [JsonPropertyName("full_name")] public string? FullName { get; init; }
        [JsonPropertyName("slug")] public string? Slug { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("is_private")] public bool IsPrivate { get; init; }
        [JsonPropertyName("parent")] public JsonElement? Parent { get; init; }
        [JsonPropertyName("updated_on")] public DateTimeOffset? UpdatedOn { get; init; }
        [JsonPropertyName("mainbranch")] public Branch? MainBranch { get; init; }
        [JsonPropertyName("links")] public Links? Links { get; init; }
    }

    private sealed record CommitAuthor
    {
        /// <summary>"Name &lt;email&gt;", always present.</summary>
        [JsonPropertyName("raw")] public string? Raw { get; init; }

        /// <summary>Absent when the commit's email belongs to no Bitbucket account.</summary>
        [JsonPropertyName("user")] public Account? User { get; init; }
    }

    private sealed record Commit
    {
        [JsonPropertyName("hash")] public string? Hash { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("date")] public DateTimeOffset? Date { get; init; }
        [JsonPropertyName("author")] public CommitAuthor? Author { get; init; }
        [JsonPropertyName("links")] public Links? Links { get; init; }
    }

    private sealed record RefTarget([property: JsonPropertyName("hash")] string? Hash);

    private sealed record Ref([property: JsonPropertyName("target")] RefTarget? Target);

    private sealed record TreeEntry
    {
        /// <summary><c>commit_file</c> or <c>commit_directory</c>.</summary>
        [JsonPropertyName("type")] public string? Type { get; init; }

        /// <summary>From the repository root, not the directory listed.</summary>
        [JsonPropertyName("path")] public string? Path { get; init; }

        [JsonPropertyName("size")] public long? Size { get; init; }
    }
}
