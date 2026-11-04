using System.Net;
using System.Security.Claims;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Code;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Code;

/// <summary>
/// Gitea and Forgejo (OP2-84) against mocked HTTP: the connection probe, every code
/// operation's wire format, the router, and the code tools choosing a Gitea connection.
/// </summary>
public sealed class GiteaTests
{
    private const string Api = "https://git.example.com/api/v1";

    // ── plumbing ────────────────────────────────────────────────────────────

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r);
            return Task.FromResult(respond(r));
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, int? total = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (total is { } t) r.Headers.Add("X-Total-Count", t.ToString());
        return r;
    }

    private static Connection Conn(string login = "alice") => new()
    {
        UserId = Guid.Empty, Label = "forge", Provider = ProviderKind.Gitea, Instance = Api,
        AuthKind = AuthKind.PersonalAccessToken, SealedCredential = "sealed", State = ConnectionState.Active,
        AccountIdentifier = login,
    };

    private static (GiteaService Service, Handler Http, OneClient Factory) Service(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var (store, _) = Stub<IConnectionStore>.Create(new()
        {
            ["GetCredentialAsync"] = _ => Task.FromResult<string?>("gt-token"),
            ["TouchAsync"] = _ => Task.CompletedTask,
        });
        var http = new Handler(respond);
        var factory = new OneClient(http);
        return (new GiteaService(factory, store, NullLogger<GiteaService>.Instance), http, factory);
    }

    private static string Path(HttpRequestMessage r) => r.RequestUri!.AbsolutePath;
    private static System.Collections.Specialized.NameValueCollection Query(HttpRequestMessage r) =>
        HttpUtility.ParseQueryString(r.RequestUri!.Query);

    private static string Issue(int number, string repo = "alice/app", string title = "Fix it", bool pull = false,
        bool draft = false, string assignees = "[]", string updated = "2026-11-01T10:00:00Z") =>
        $$"""
        {"number":{{number}},"title":"{{title}}","user":{"login":"bob"},"updated_at":"{{updated}}",
         "comments":3,"html_url":"https://git.example.com/{{repo}}/issues/{{number}}",
         "labels":[{"name":"bug"}],"assignees":{{assignees}},
         "repository":{"full_name":"{{repo}}"}{{(pull ? $",\"pull_request\":{{\"draft\":{(draft ? "true" : "false")},\"merged\":false}}" : "")}}}
        """;

    // ── the server address ──────────────────────────────────────────────────

    [Theory]
    [InlineData("git.example.com", "https://git.example.com/api/v1/")]
    [InlineData("https://git.example.com/", "https://git.example.com/api/v1/")]
    [InlineData("https://git.example.com/api/v1", "https://git.example.com/api/v1/")]
    [InlineData("https://git.example.com/api/v1/user", "https://git.example.com/api/v1/")]
    [InlineData("https://example.com/git", "https://example.com/git/api/v1/")]
    [InlineData("http://192.168.1.5:3000", "http://192.168.1.5:3000/api/v1/")]
    [InlineData("  codeberg.org  ", "https://codeberg.org/api/v1/")]
    public void The_server_is_taken_as_a_person_types_it(string typed, string api)
    {
        Assert.Equal(api, GiteaConnectionService.Normalise(typed));
    }

    [Fact]
    public void A_blank_server_is_refused_because_there_is_no_public_default()
    {
        Assert.Throws<InvalidOperationException>(() => GiteaConnectionService.Normalise("  "));
    }

    // ── connecting ──────────────────────────────────────────────────────────

    private static (GiteaConnectionService Service, Handler Http, Stub<IConnectionStore> Store) Connector(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var (store, stub) = Stub<IConnectionStore>.Create(new()
        {
            ["UpsertAsync"] = a => Task.FromResult(new Connection
            {
                UserId = (Guid)a[0]!, Provider = (ProviderKind)a[1]!, Instance = (string)a[2]!,
                HomeAccountId = (string?)a[3], Label = (string)a[4]!, AuthKind = (AuthKind)a[5]!,
                SealedCredential = "sealed", Capabilities = [.. (IEnumerable<Capability>)a[7]!],
            }),
            ["SetAccountIdentityAsync"] = _ => Task.CompletedTask,
            ["SetCredentialExpiryAsync"] = _ => Task.CompletedTask,
        });
        var http = new Handler(respond);
        return (new GiteaConnectionService(new OneClient(http), store, NullLogger<GiteaConnectionService>.Instance), http, stub);
    }

    [Fact]
    public async Task Connecting_probes_the_account_with_a_token_header_and_stores_a_gitea_connection()
    {
        var (svc, http, store) = Connector(r => Path(r) switch
        {
            "/api/v1/user" => Json("""{"login":"alice","full_name":"Alice A"}"""),
            "/api/v1/version" => Json("""{"version":"9.0.1+gitea-1.22.0"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var c = await svc.ConnectAsync(Guid.CreateVersion7(), "git.example.com", "gt-token", "forge");

        Assert.Equal(ProviderKind.Gitea, c.Provider);
        Assert.Equal(Api, c.Instance);
        Assert.Equal("alice", c.HomeAccountId);
        Assert.Equal("token gt-token", http.Requests[0].Headers.Authorization!.ToString());

        // Read on, write off, as for GitHub.
        Assert.Contains(c.Capabilities, x => x.Name == Capabilities.CodeRead && x.Enabled);
        Assert.Contains(c.Capabilities, x => x.Name == Capabilities.CodeWrite && !x.Enabled);
        Assert.Contains(store.Calls, x => x.Name == "SetAccountIdentityAsync" && (string?)x.Args[1] == "alice");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "read:user")]
    [InlineData(HttpStatusCode.NotFound, "No Gitea or Forgejo API")]
    [InlineData(HttpStatusCode.BadGateway, "HTTP 502")]
    public async Task A_failed_probe_says_what_to_fix(HttpStatusCode status, string says)
    {
        var (svc, _, _) = Connector(_ => new HttpResponseMessage(status));

        var probe = await svc.ProbeAsync("git.example.com", "t");

        Assert.False(probe.Ok);
        Assert.Contains(says, probe.Error);
    }

    [Fact]
    public async Task A_web_page_where_the_api_should_be_is_named_as_such()
    {
        var (svc, _, _) = Connector(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html"),
        });

        var probe = await svc.ProbeAsync("git.example.com", "t");

        Assert.False(probe.Ok);
        Assert.Contains("isn't a Gitea or Forgejo API", probe.Error);
    }

    [Fact]
    public async Task A_failed_connect_writes_nothing()
    {
        var (svc, _, store) = Connector(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ConnectAsync(Guid.CreateVersion7(), "git.example.com", "bad", "forge"));
        Assert.DoesNotContain(store.Calls, x => x.Name == "UpsertAsync");
    }

    // ── triage and searches ─────────────────────────────────────────────────

    [Fact]
    public async Task Triage_counts_from_the_total_header_and_tells_drafts_apart()
    {
        var (svc, http, factory) = Service(r =>
        {
            var q = Query(r);
            if (q["review_requested"] == "true") return Json("[]", total: 4);
            if (q["assigned"] == "true") return Json("[]", total: 7);
            if (q["mentioned"] == "true") return Json("[]", total: q["type"] == "pulls" ? 1 : 2);
            if (q["created"] == "true")
                return Json($"[{Issue(1, pull: true, draft: true)},{Issue(2, pull: true, title: "WIP: later")},{Issue(3, pull: true)}]", total: 3);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var t = await svc.TriageAsync(Conn());

        Assert.Equal(4, t.AwaitingMyReview);
        Assert.Equal(3, t.MyOpenPullRequests);
        Assert.Equal(2, t.MyDraftPullRequests);
        Assert.Equal(7, t.IssuesAssignedToMe);
        Assert.Equal(3, t.MentioningMe);
        Assert.Equal("alice", t.Login);

        Assert.All(http.Requests, r => Assert.StartsWith(Api + "/repos/issues/search?", r.RequestUri!.AbsoluteUri));
        Assert.All(http.Requests, r => Assert.Equal("open", Query(r)["state"]));
        Assert.All(factory.Names, n => Assert.Equal("gitea", n));
    }

    [Fact]
    public async Task Awaiting_my_review_is_the_review_requested_flag_and_maps_the_item()
    {
        var (svc, http, _) = Service(_ => Json($"[{Issue(12, pull: true, draft: true, assignees: """[{"login":"carol"}]""")}]"));

        var items = await svc.ListPullRequestsAsync(Conn(), PullRequestScope.AwaitingMyReview, null, 10);

        var q = Query(Assert.Single(http.Requests));
        Assert.Equal("pulls", q["type"]);
        Assert.Equal("true", q["review_requested"]);

        var i = Assert.Single(items);
        Assert.Equal("alice/app", i.Repo);
        Assert.Equal(12, i.Number);
        Assert.Equal("bob", i.Author);
        Assert.True(i.IsPullRequest);
        Assert.True(i.IsDraft);
        Assert.Equal(["bug"], i.Labels);
        Assert.Equal(["carol"], i.Assignees);
        Assert.Equal("alice/app#12", i.Reference);
    }

    [Fact]
    public async Task A_personal_search_for_one_repository_asks_by_owner_and_keeps_only_that_repository()
    {
        var (svc, http, _) = Service(_ => Json($"[{Issue(1, repo: "alice/app", pull: true)},{Issue(2, repo: "alice/other", pull: true)}]"));

        var items = await svc.ListPullRequestsAsync(Conn(), PullRequestScope.Mine, "alice/app", 10);

        Assert.Equal("alice", Query(http.Requests[0])["owner"]);
        Assert.Equal("true", Query(http.Requests[0])["created"]);
        Assert.Equal([1], items.Select(i => i.Number));
    }

    [Fact]
    public async Task Unassigned_issues_in_a_repository_are_listed_there_and_the_assigned_ones_dropped()
    {
        var (svc, http, _) = Service(_ => Json(
            $"[{Issue(1)},{Issue(2, assignees: """[{"login":"carol"}]""")},{Issue(3, updated: "2026-11-02T10:00:00Z")}]"));

        var items = await svc.ListIssuesAsync(Conn(), IssueScope.Unassigned, "alice/app", 10);

        var r = Assert.Single(http.Requests);
        Assert.Equal("/api/v1/repos/alice/app/issues", Path(r));
        Assert.Equal("issues", Query(r)["type"]);
        Assert.Equal([3, 1], items.Select(i => i.Number));
    }

    [Fact]
    public async Task Assigned_to_me_is_the_assigned_flag()
    {
        var (svc, http, _) = Service(_ => Json($"[{Issue(5)}]"));

        var items = await svc.ListIssuesAsync(Conn(), IssueScope.AssignedToMe, null, 10);

        Assert.Equal("true", Query(Assert.Single(http.Requests))["assigned"]);
        Assert.False(Assert.Single(items).IsPullRequest);
    }

    [Fact]
    public async Task An_unscoped_all_open_question_is_refused_rather_than_sweeping_the_server()
    {
        var (svc, http, _) = Service(_ => Json("[]"));

        await Assert.ThrowsAsync<GitHubException>(() => svc.ListPullRequestsAsync(Conn(), PullRequestScope.AllOpen, null, 10));
        await Assert.ThrowsAsync<GitHubException>(() => svc.ListIssuesAsync(Conn(), IssueScope.Unassigned, null, 10));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_missing_repository_is_said_plainly()
    {
        var (svc, _, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<GitHubException>(
            () => svc.ListPullRequestsAsync(Conn(), PullRequestScope.AllOpen, "alice/nope", 10));
        Assert.Contains("No repository alice/nope on git.example.com", ex.Message);
    }

    [Fact]
    public async Task A_rejected_token_names_the_fix()
    {
        var (svc, _, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<GitHubException>(() => svc.TriageAsync(Conn()));
        Assert.Contains("Replace it on the connections page", ex.Message);
    }

    // ── repositories, commits, one item ─────────────────────────────────────

    [Fact]
    public async Task Repositories_page_until_a_short_page_and_come_back_newest_first()
    {
        var page1 = string.Join(",", Enumerable.Range(0, 50).Select(n =>
            $$"""{"full_name":"alice/r{{n}}","name":"r{{n}}","owner":{"login":"alice"},"updated_at":"2026-10-{{(n % 28) + 1:00}}T00:00:00Z"}"""));
        var (svc, http, _) = Service(r => Query(r)["page"] == "1"
            ? Json($"[{page1}]")
            : Json("""[{"full_name":"org/new","name":"new","owner":{"login":"org"},"private":true,"default_branch":"main","updated_at":"2026-11-03T00:00:00Z","html_url":"https://git.example.com/org/new"}]"""));

        var repos = await svc.ListRepositoriesAsync(Conn());

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(51, repos.Count);
        Assert.Equal("org/new", repos[0].FullName);
        Assert.True(repos[0].Private);
        Assert.Equal("main", repos[0].DefaultBranch);
        Assert.All(http.Requests, r => Assert.Equal("/api/v1/user/repos", Path(r)));
    }

    [Fact]
    public async Task Commits_map_the_account_or_fall_back_to_the_git_author()
    {
        var (svc, http, _) = Service(_ => Json("""
            [{"sha":"abcdef1234567","html_url":"u1","commit":{"message":"Fix the thing\n\nLonger body","author":{"name":"Alice","date":"2026-11-01T00:00:00Z"}},"author":{"login":"alice"}},
             {"sha":"1234567abcdef","commit":{"message":"Unlinked","author":{"name":"Someone Else"}},"author":null}]
            """));

        var commits = await svc.ListCommitsAsync(Conn(), "alice/app", "dev", 5);

        var q = Query(Assert.Single(http.Requests));
        Assert.Equal("dev", q["sha"]);
        Assert.Equal("5", q["limit"]);
        Assert.Equal("false", q["stat"]);
        Assert.Equal("Fix the thing", commits[0].Subject);
        Assert.Equal("abcdef1", commits[0].ShortSha);
        Assert.Equal("alice", commits[0].Author);
        Assert.Equal("Someone Else", commits[1].Author);
    }

    [Fact]
    public async Task An_empty_repository_has_no_commits_rather_than_an_error()
    {
        var (svc, _, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        Assert.Empty(await svc.ListCommitsAsync(Conn(), "alice/empty", null, 5));
    }

    [Fact]
    public async Task One_item_is_read_through_the_issues_endpoint_for_issues_and_pull_requests_alike()
    {
        var (svc, http, _) = Service(_ => Json(Issue(9, pull: true)));

        var item = await svc.GetAsync(Conn(), "alice/app", 9);

        Assert.Equal("/api/v1/repos/alice/app/issues/9", Path(Assert.Single(http.Requests)));
        Assert.True(item!.IsPullRequest);
    }

    [Fact]
    public async Task A_missing_item_is_null()
    {
        var (svc, _, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await svc.GetAsync(Conn(), "alice/app", 404));
    }

    // ── contents ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_root_is_listed_from_contents_with_nothing_after_it_directories_first()
    {
        var (svc, http, _) = Service(_ => Json("""
            [{"name":"README.md","path":"README.md","type":"file","size":120},
             {"name":"src","path":"src","type":"dir","size":0}]
            """));

        var entries = await svc.ListPathAsync(Conn(), "alice/app", null, "main");

        var r = Assert.Single(http.Requests);
        Assert.Equal("/api/v1/repos/alice/app/contents", Path(r));
        Assert.Equal("main", Query(r)["ref"]);
        Assert.Equal(["src", "README.md"], entries!.Select(e => e.Name));
        Assert.True(entries![0].IsDirectory);
    }

    [Fact]
    public async Task A_file_is_decoded_from_base64_and_a_path_is_encoded_segment_by_segment()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello\nworld"));
        var (svc, http, _) = Service(_ => Json($$"""{"type":"file","size":11,"content":"{{b64}}","html_url":"https://git.example.com/x"}"""));

        var file = await svc.ReadFileAsync(Conn(), "alice/app", "docs/a b.md", null, 1000);

        Assert.Equal("/api/v1/repos/alice/app/contents/docs/a%20b.md", Assert.Single(http.Requests).RequestUri!.AbsolutePath);
        Assert.Equal("hello\nworld", file!.Text);
        Assert.False(file.Truncated);
    }

    [Fact]
    public async Task A_file_the_api_does_not_inline_is_fetched_from_the_raw_route()
    {
        var (svc, http, _) = Service(r => Path(r).Contains("/raw/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("big file") }
            : Json("""{"type":"file","size":5000000,"content":null}"""));

        var file = await svc.ReadFileAsync(Conn(), "alice/app", "big.txt", "dev", 1000);

        Assert.Equal("big file", file!.Text);
        Assert.Equal("/api/v1/repos/alice/app/raw/big.txt", Path(http.Requests[1]));
        Assert.Equal("dev", Query(http.Requests[1])["ref"]);
    }

    [Fact]
    public async Task A_binary_file_and_a_submodule_are_refused_in_a_sentence()
    {
        var bin = Convert.ToBase64String([0x89, 0x50, 0x00, 0x01]);
        var (svc, _, _) = Service(r => Path(r).EndsWith("logo.png")
            ? Json($$"""{"type":"file","size":4,"content":"{{bin}}"}""")
            : Json("""{"type":"submodule","size":0}"""));

        Assert.Contains("binary", (await Assert.ThrowsAsync<GitHubException>(
            () => svc.ReadFileAsync(Conn(), "alice/app", "logo.png", null, 1000))).Message);
        Assert.Contains("submodule", (await Assert.ThrowsAsync<GitHubException>(
            () => svc.ReadFileAsync(Conn(), "alice/app", "vendor/lib", null, 1000))).Message);
    }

    [Fact]
    public async Task A_directory_asked_for_as_a_file_is_null()
    {
        var (svc, _, _) = Service(_ => Json("[]"));

        Assert.Null(await svc.ReadFileAsync(Conn(), "alice/app", "src", null, 1000));
    }

    // ── the router and the tools ────────────────────────────────────────────

    [Fact]
    public async Task The_router_sends_each_connection_to_its_own_provider()
    {
        var (gitea, http, _) = Service(_ => Json("[]", total: 0));
        var (github, githubStub) = Stub<ICodeProvider>.Create(new()
        {
            ["get_Provider"] = _ => ProviderKind.GitHub,
        });
        var router = new CodeServiceRouter([github, gitea]);

        await router.TriageAsync(Conn());

        Assert.NotEmpty(http.Requests);
        Assert.DoesNotContain(githubStub.Calls, c => c.Name == "TriageAsync");
    }

    [Fact]
    public async Task A_provider_with_no_code_implementation_is_a_sentence_not_a_call()
    {
        var router = new CodeServiceRouter([]);

        var ex = await Assert.ThrowsAsync<GitHubException>(() => router.TriageAsync(Conn()));
        Assert.Equal("forge is a Gitea and Forgejo pull requests and issues connection, and Outpost can't read code on it.",
            ex.Message);
    }

    [Fact]
    public void Two_implementations_for_one_provider_is_a_startup_error()
    {
        var (a, _, _) = Service(_ => Json("[]"));
        var (b, _, _) = Service(_ => Json("[]"));

        Assert.Throws<InvalidOperationException>(() => new CodeServiceRouter([a, b]));
    }

    [Fact]
    public async Task The_code_tools_pick_a_gitea_connection_and_answer_from_it()
    {
        var forge = Conn();
        forge.IsDefaultForProvider = true;
        foreach (var name in CapabilityCatalog.DeclaredFor(ProviderKind.Gitea))
            forge.Capabilities.Add(new Capability { Name = name, Granted = true, Enabled = true });

        var (store, _) = Stub<IConnectionStore>.Create(new()
        {
            ["ListAsync"] = _ => Task.FromResult<IReadOnlyList<Connection>>([forge]),
            ["GetCredentialAsync"] = _ => Task.FromResult<string?>("gt-token"),
            ["TouchAsync"] = _ => Task.CompletedTask,
        });
        var http = new Handler(r => Query(r)["created"] == "true" ? Json("[]", total: 2) : Json("[]", total: 1));
        var gitea = new GiteaService(new OneClient(http), store, NullLogger<GiteaService>.Instance);
        var (repos, _) = Stub<IRepoResolver>.Create([]);

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("op2:uid", Guid.Empty.ToString())], "test")),
            },
        };
        var tools = new CodeTools(accessor, store, new CodeServiceRouter([gitea]), repos);

        var said = await tools.Triage();

        Assert.StartsWith("In forge, as alice:", said);
        Assert.Contains("pull request", said);
        Assert.NotEmpty(http.Requests);
    }

    [Fact]
    public void A_gitea_connection_declares_code_and_nothing_else()
    {
        Assert.Equal([Capabilities.CodeRead, Capabilities.CodeWrite], CapabilityCatalog.DeclaredFor(ProviderKind.Gitea));
        Assert.False(ConnectionResolver.Resolve([Conn()], Capabilities.IssuesRead, "forge", isWrite: false).IsActionable);
    }
}
