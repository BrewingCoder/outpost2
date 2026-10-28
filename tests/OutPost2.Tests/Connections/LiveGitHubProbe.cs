using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Code;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Exercises the GitHub triage path against the real API. Read-only.
/// <para>
/// The thing most worth proving live is the search query itself: <c>@me</c> only resolves
/// for a token that belongs to a person, so the whole feature silently returns zeroes if
/// the credential is ever swapped for an app installation token. A unit test with a
/// recorded response cannot catch that.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveGitHubProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Triage_and_listings_answer_from_the_real_api()
    {
        OutPostDbContext db;
        GitHubService code;
        GitHubConnectionService connectService;

        try
        {
            db = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Dev).Options);
            _disposables.Add(db);

            var vault = new VaultTransitClient(Options.Create(new VaultOptions
            {
                Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
            }), NullLogger<VaultTransitClient>.Instance);
            _disposables.Add(vault);

            Skip.IfNot(await vault.HealthyAsync(), "dev Vault unreachable");

            var store = new ConnectionStore(db, new CredentialProtector(vault));
            var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
            _disposables.Add(sp);
            var factory = sp.GetRequiredService<IHttpClientFactory>();

            code = new GitHubService(factory, store, NullLogger<GitHubService>.Instance);
            connectService = new GitHubConnectionService(
                factory, store, NullLogger<GitHubConnectionService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        var c = await db.Connections.FirstOrDefaultAsync(x => x.Provider == ProviderKind.GitHub);
        Skip.If(c is null, "no GitHub connection stored");

        output.WriteLine($"=== '{c!.Label}' -> {c.Instance} as {c.AccountIdentifier} ===");
        output.WriteLine("  capabilities: " + string.Join(", ",
            c.Capabilities.Select(x => $"{x.Name}={(x.Usable ? "on" : "off")}")));

        Assert.False(string.IsNullOrWhiteSpace(c.AccountIdentifier));
        Assert.True(await connectService.CheckHealthAsync(c), "GitHub rejected the stored token");

        // Writing must be off until deliberately enabled: commenting or closing is
        // visible to other people and cannot be taken back.
        Assert.False(c.Capabilities.Single(x => x.Name == Capabilities.CodeWrite).Usable);
        Assert.True(c.Capabilities.Single(x => x.Name == Capabilities.CodeRead).Usable);

        var t = await code.TriageAsync(c);
        output.WriteLine("");
        output.WriteLine($"  awaiting my review : {t.AwaitingMyReview}");
        output.WriteLine($"  my open PRs        : {t.MyOpenPullRequests} ({t.MyDraftPullRequests} draft)");
        output.WriteLine($"  issues assigned    : {t.IssuesAssignedToMe}");
        output.WriteLine($"  mentioning me      : {t.MentioningMe}");

        // Counts are facts about a live account, so none can be asserted to a value. That
        // they are non-negative and that the call succeeded is the real claim: a broken
        // query returns HTTP 422, not a plausible zero.
        Assert.True(t.AwaitingMyReview >= 0);
        Assert.Equal(c.AccountIdentifier, t.Login);

        var mine = await code.ListPullRequestsAsync(c, PullRequestScope.Mine, null, 10);
        output.WriteLine("");
        output.WriteLine($"  my open pull requests ({mine.Count}):");
        foreach (var i in mine)
            output.WriteLine($"    {i.Reference,-34} {(i.IsDraft ? "draft " : "      ")}{Trim(i.Title, 52)}");

        // Scope and shape must actually hold, or the prose built from them lies.
        Assert.All(mine, i => Assert.True(i.IsPullRequest));
        Assert.All(mine, i => Assert.NotEqual("unknown", i.Repo));
        Assert.All(mine, i => Assert.True(i.Number > 0));

        // The count the digest quotes and the list it would read out must agree, or the
        // user is told "3 open" and then hears two.
        if (t.MyOpenPullRequests <= 10)
            Assert.Equal(t.MyOpenPullRequests, mine.Count);

        var assigned = await code.ListIssuesAsync(c, IssueScope.AssignedToMe, null, 10);
        output.WriteLine($"  issues assigned to me ({assigned.Count}):");
        foreach (var i in assigned)
            output.WriteLine($"    {i.Reference,-34} {Trim(i.Title, 52)}");
        Assert.All(assigned, i => Assert.False(i.IsPullRequest));

        // A repo-scoped read, and then the single-item lookup that has to serve both
        // kinds through the issues endpoint.
        if (mine.Count > 0)
        {
            var one = mine[0];
            var detail = await code.GetAsync(c, one.Repo, one.Number);
            Assert.NotNull(detail);
            Assert.Equal(one.Number, detail!.Number);
            Assert.True(detail.IsPullRequest, "a pull request read back as an issue");
            output.WriteLine("");
            output.WriteLine($"  read {detail.Reference}: {Trim(detail.Title, 60)}");
            output.WriteLine($"    by {detail.Author}, updated {detail.UpdatedAt:yyyy-MM-dd}, " +
                             $"{detail.Comments} comment(s), labels [{string.Join(", ", detail.Labels)}]");
        }

        // Unscoped sweeps are refused rather than quietly searching all of GitHub.
        var blown = await Assert.ThrowsAsync<GitHubException>(
            () => code.ListIssuesAsync(c, IssueScope.Unassigned, null, 5));
        output.WriteLine("");
        output.WriteLine($"  unscoped 'unassigned' refused: {blown.Message}");
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
