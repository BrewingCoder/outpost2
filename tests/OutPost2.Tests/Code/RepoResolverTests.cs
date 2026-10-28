using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OutPost2.Core.Code;
using OutPost2.Core.Connections;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Code;
using OutPost2.Infrastructure.Data;

namespace OutPost2.Tests.Code;

/// <summary>
/// Resolving what someone says into an owner/name.
/// <para>
/// Asked for the last three commits on "RosettaCore", the only honest answer used to be
/// that it could not even tell whether the repository was visible — there was no listing
/// to check against and no way to turn a bare name into a path. Both halves of that are
/// what these cover.
/// </para>
/// </summary>
public sealed class RepoResolverTests : IAsyncLifetime
{
    private const string Admin = "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";
    private readonly string _dbName = "op2_rr_" + Guid.NewGuid().ToString("n")[..12];

    private OutPostDbContext _db = null!;
    private Connection _conn = null!;
    private bool _ready;
    private string _why = "";

    private sealed class FakeGitHub(List<RepoRef> repos) : IGitHubService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<RepoRef>> ListRepositoriesAsync(Connection c, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RepoRef>>(repos);
        }

        public Task<CodeTriage> TriageAsync(Connection c, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<CommitRef>> ListCommitsAsync(
            Connection c, string repo, string? branch, int count, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeItem>> ListPullRequestsAsync(
            Connection c, PullRequestScope scope, string? repo, int count, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<CodeItem>> ListIssuesAsync(
            Connection c, IssueScope scope, string? repo, int count, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<CodeItem?> GetAsync(Connection c, string repo, int number, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static RepoRef Repo(string fullName)
    {
        var parts = fullName.Split('/');
        return new RepoRef { FullName = fullName, Owner = parts[0], Name = parts[1] };
    }

    public async Task InitializeAsync()
    {
        try
        {
            await using (var a = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
                await a.Database.ExecuteSqlRawAsync($"CREATE DATABASE {_dbName}");

            _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
                .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);
            await _db.Database.MigrateAsync();

            var u = new User { GitHubUserId = 991, GitHubLogin = "tester" };
            _db.Users.Add(u);
            await _db.SaveChangesAsync();

            _conn = new Connection
            {
                UserId = u.Id,
                Label = "work",
                Provider = ProviderKind.GitHub,
                Instance = "https://api.github.com/",
                AuthKind = AuthKind.PersonalAccessToken,
                SealedCredential = "{}",
            };
            _db.Connections.Add(_conn);
            await _db.SaveChangesAsync();

            _ready = true;
        }
        catch (Exception ex)
        {
            _why = "Postgres unavailable: " + ex.Message;
        }
    }

    public async Task DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
        try
        {
            await using var a = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
            await a.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
        }
        catch { /* best effort */ }
    }

    private (RepoResolver Resolver, FakeGitHub Gh) Build(params string[] repos)
    {
        var gh = new FakeGitHub(repos.Select(Repo).ToList());
        return (new RepoResolver(_db, gh, new MemoryCache(new MemoryCacheOptions())), gh);
    }

    [SkippableFact]
    public async Task An_owner_slash_name_is_taken_literally()
    {
        Skip.IfNot(_ready, _why);

        var (r, gh) = Build();
        var result = await r.ResolveAsync(_conn, "BrewingCoder/RosettaCore");

        Assert.Equal(RepoResolutionKind.Resolved, result.Kind);
        Assert.Equal("BrewingCoder/RosettaCore", result.FullName);

        // Not looked up at all. The caller may be naming a public repository this token
        // has never listed, and refusing it because it is not in the listing would be wrong.
        Assert.Equal(0, gh.Calls);
    }

    [SkippableFact]
    public async Task A_bare_name_resolves_against_what_the_token_can_see()
    {
        Skip.IfNot(_ready, _why);

        var (r, _) = Build("BrewingCoder/RosettaCore", "BrewingCoder/outpost");
        var result = await r.ResolveAsync(_conn, "RosettaCore");

        Assert.Equal(RepoResolutionKind.Resolved, result.Kind);
        Assert.Equal("BrewingCoder/RosettaCore", result.FullName);
    }

    [SkippableTheory]
    [InlineData("rosettacore")]
    [InlineData("Rosetta-Core")]
    [InlineData("rosetta core")]
    [InlineData("ROSETTA_CORE")]
    public async Task Case_and_separators_do_not_matter(string spoken)
    {
        Skip.IfNot(_ready, _why);

        // Speech-to-text does not reliably reproduce a hyphen, and a lookup that needs
        // one fails on every spoken request while passing every typed test.
        var (r, _) = Build("BrewingCoder/RosettaCore");
        var result = await r.ResolveAsync(_conn, spoken);

        Assert.Equal(RepoResolutionKind.Resolved, result.Kind);
        Assert.Equal("BrewingCoder/RosettaCore", result.FullName);
    }

    [SkippableFact]
    public async Task The_same_name_in_two_organisations_is_asked_about_not_guessed()
    {
        Skip.IfNot(_ready, _why);

        var (r, _) = Build("BrewingCoder/tools", "contoso/tools");
        var result = await r.ResolveAsync(_conn, "tools");

        Assert.Equal(RepoResolutionKind.Ambiguous, result.Kind);
        Assert.Null(result.FullName);
        Assert.Contains("BrewingCoder/tools", result.Message);
        Assert.Contains("contoso/tools", result.Message);
    }

    [SkippableFact]
    public async Task An_alias_beats_the_listing()
    {
        Skip.IfNot(_ready, _why);

        // The entire point of configuring one: "tools" is ambiguous above, and an alias
        // settles it permanently instead of asking on every single request.
        _db.RepoAliases.Add(new RepoAlias
        {
            UserId = _conn.UserId,
            ConnectionId = _conn.Id,
            Alias = "tools",
            FullName = "contoso/tools",
        });
        await _db.SaveChangesAsync();

        var (r, _) = Build("BrewingCoder/tools", "contoso/tools");
        var result = await r.ResolveAsync(_conn, "tools");

        Assert.Equal(RepoResolutionKind.Resolved, result.Kind);
        Assert.Equal("contoso/tools", result.FullName);
    }

    [SkippableFact]
    public async Task An_alias_can_be_a_nickname_github_has_never_heard()
    {
        Skip.IfNot(_ready, _why);

        _db.RepoAliases.Add(new RepoAlias
        {
            UserId = _conn.UserId,
            ConnectionId = _conn.Id,
            Alias = "the core",
            FullName = "BrewingCoder/RosettaCore",
        });
        await _db.SaveChangesAsync();

        var (r, _) = Build("BrewingCoder/RosettaCore");

        Assert.Equal("BrewingCoder/RosettaCore", (await r.ResolveAsync(_conn, "the core")).FullName);
        Assert.Equal("BrewingCoder/RosettaCore", (await r.ResolveAsync(_conn, "TheCore")).FullName);
    }

    [SkippableFact]
    public async Task An_unknown_name_offers_near_misses_rather_than_a_flat_no()
    {
        Skip.IfNot(_ready, _why);

        // A wrong transcription is far likelier than a repository that does not exist,
        // so the reply has to give the listener something to correct against.
        var (r, _) = Build("BrewingCoder/RosettaCore", "BrewingCoder/outpost");
        var result = await r.ResolveAsync(_conn, "rosetta");

        Assert.Equal(RepoResolutionKind.NotFound, result.Kind);
        Assert.Contains("BrewingCoder/RosettaCore", result.Message);
    }

    [SkippableFact]
    public async Task A_name_matching_nothing_says_how_many_it_looked_through()
    {
        Skip.IfNot(_ready, _why);

        var (r, _) = Build("BrewingCoder/outpost");
        var result = await r.ResolveAsync(_conn, "zzzz");

        Assert.Equal(RepoResolutionKind.NotFound, result.Kind);

        // "I can't see it" and "I didn't look" are different answers, and the reply has
        // to be the first one rather than something that could be either.
        Assert.Contains("1", result.Message);
        Assert.Contains("work", result.Message);
    }

    [SkippableFact]
    public async Task The_listing_is_cached_across_calls()
    {
        Skip.IfNot(_ready, _why);

        var (r, gh) = Build("BrewingCoder/RosettaCore");

        await r.ResolveAsync(_conn, "RosettaCore");
        await r.ResolveAsync(_conn, "RosettaCore");
        await r.KnownAsync(_conn);

        // A conversation asks about repositories several times in a row, and re-listing
        // on each one burns the API budget for no new information.
        Assert.Equal(1, gh.Calls);
    }

    [SkippableFact]
    public async Task Forgetting_the_cache_forces_a_fresh_listing()
    {
        Skip.IfNot(_ready, _why);

        var (r, gh) = Build("BrewingCoder/RosettaCore");

        await r.KnownAsync(_conn);
        r.Forget(_conn.Id);
        await r.KnownAsync(_conn);

        Assert.Equal(2, gh.Calls);
    }

    [SkippableFact]
    public async Task Configured_names_can_be_read_back()
    {
        Skip.IfNot(_ready, _why);

        _db.RepoAliases.Add(new RepoAlias
        {
            UserId = _conn.UserId,
            ConnectionId = _conn.Id,
            Alias = "the core",
            FullName = "BrewingCoder/RosettaCore",
        });
        await _db.SaveChangesAsync();

        var (r, _) = Build("BrewingCoder/outpost");

        // Reading them back is what makes them exist as far as the conversation is
        // concerned. Without this the tools report, correctly from what they can see,
        // that there is no alias concept at all — while resolution is quietly using one.
        var names = await r.AliasesAsync(_conn);

        Assert.Single(names);
        Assert.Equal("the core", names[0].Alias);
        Assert.Equal("BrewingCoder/RosettaCore", names[0].FullName);
    }

    [SkippableFact]
    public async Task A_failed_lookup_mentions_the_names_that_are_configured()
    {
        Skip.IfNot(_ready, _why);

        _db.RepoAliases.Add(new RepoAlias
        {
            UserId = _conn.UserId,
            ConnectionId = _conn.Id,
            Alias = "the core",
            FullName = "BrewingCoder/RosettaCore",
        });
        await _db.SaveChangesAsync();

        var (r, _) = Build("BrewingCoder/outpost");
        var result = await r.ResolveAsync(_conn, "zzzz");

        Assert.Equal(RepoResolutionKind.NotFound, result.Kind);

        // Otherwise the reply states a fact about the listing and leaves the listener
        // wondering whether the thing they configured is consulted at all.
        Assert.Contains("the core", result.Message);
        Assert.Contains("BrewingCoder/RosettaCore", result.Message);
    }

    [SkippableFact]
    public async Task Aliases_do_not_leak_between_connections()
    {
        Skip.IfNot(_ready, _why);

        var other = new Connection
        {
            UserId = _conn.UserId,
            Label = "enterprise",
            Provider = ProviderKind.GitHub,
            Instance = "https://ghe.example.com/api/v3/",
            AuthKind = AuthKind.PersonalAccessToken,
            SealedCredential = "{}",
        };
        _db.Connections.Add(other);
        _db.RepoAliases.Add(new RepoAlias
        {
            UserId = _conn.UserId,
            ConnectionId = other.Id,
            Alias = "the core",
            FullName = "internal/core",
        });
        await _db.SaveChangesAsync();

        // The same words can mean different repositories on different hosts, and a token
        // that cannot reach one should not even be told its name.
        var (r, _) = Build("BrewingCoder/RosettaCore");
        var result = await r.ResolveAsync(_conn, "the core");

        Assert.NotEqual(RepoResolutionKind.Resolved, result.Kind);
    }
}
