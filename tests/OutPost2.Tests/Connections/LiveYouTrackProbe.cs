using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Issues;
using OutPost2.Infrastructure.Security;
using Xunit.Abstractions;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Exercises every stored YouTrack connection against its real server, and — now that more
/// than one exists — the resolver behaviour that only two connections can demonstrate.
/// <para>
/// Read-only against the trackers. The one thing it asserts about writes is that an
/// unlabelled write refuses to pick for you.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveYouTrackProbe(ITestOutputHelper output) : IDisposable
{
    private const string Dev =
        "Host=localhost;Port=5435;Database=outpost2;Username=outpost2;Password=outpost2-dev";

    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    [SkippableFact]
    public async Task Every_stored_tracker_answers_and_the_resolver_handles_more_than_one()
    {
        OutPostDbContext db;
        YouTrackService issues;
        YouTrackConnectionService connectService;

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

            issues = new YouTrackService(factory, store, NullLogger<YouTrackService>.Instance);
            connectService = new YouTrackConnectionService(
                factory, store, NullLogger<YouTrackConnectionService>.Instance);
        }
        catch (Exception ex) { Skip.If(true, ex.Message); return; }

        // Reachable-but-unmigrated Postgres is a skip, not a failure (see LiveEnvironment).
        await LiveEnvironment.RequireSchemaAsync(db);

        var trackers = await db.Connections
            .Where(c => c.Provider == ProviderKind.YouTrack)
            .OrderBy(c => c.Label)
            .ToListAsync();

        Skip.If(trackers.Count == 0, "no YouTrack connections stored");

        foreach (var c in trackers)
        {
            output.WriteLine($"=== '{c.Label}' -> {c.Instance} ===");
            output.WriteLine($"  account {c.AccountIdentifier}  state {c.State}" +
                             $"{(c.IsDefaultForProvider ? "  [default]" : "")}");
            output.WriteLine("  capabilities: " + string.Join(", ", c.Capabilities.Select(
                x => $"{x.Name}={(x.Usable ? "on" : "off")}")));

            // A connection that cannot say who it is leaves the user guessing which
            // identity a write will act as — the mistake that attached the wrong mailbox.
            Assert.False(string.IsNullOrWhiteSpace(c.AccountIdentifier),
                $"'{c.Label}' has no AccountIdentifier — the UI cannot say who it connects as");
            Assert.False(c.IsPersonalAccount);

            // The credential must survive a Vault round trip and still authenticate. A
            // stored token that no longer works is the failure that turns a voice turn
            // into a mystery, so health is checked before anything is asked of it.
            Assert.True(await connectService.CheckHealthAsync(c),
                $"'{c.Label}' failed its health check against {c.Instance}");

            var projects = await issues.ListProjectsAsync(c);
            output.WriteLine($"  {projects.Count} project(s): " + string.Join(", ",
                projects.Take(12).Select(p => p.ShortName)) + (projects.Count > 12 ? ", …" : ""));
            Assert.NotEmpty(projects);

            var open = await issues.SearchAsync(c, "#Unresolved", 5);
            output.WriteLine($"  {open.Count} unresolved (showing up to 5):");
            foreach (var i in open)
                output.WriteLine($"    {i.Key,-12} {i.State,-14} {Trim(i.Summary, 58)}");

            // Reading one issue in full is a different Graph-equivalent code path from the
            // search projection — custom fields are polymorphic and parsed defensively.
            if (open.Count > 0)
            {
                var detail = await issues.GetIssueAsync(c, open[0].Key);
                Assert.NotNull(detail);
                output.WriteLine($"  read {open[0].Key}: state={detail!.State} " +
                                 $"assignee={detail.Assignee ?? "unassigned"}");
            }

            output.WriteLine("");
        }

        // --- resolver behaviour that needs two connections to mean anything ---------------
        Skip.If(trackers.Count < 2, "only one tracker stored — resolver ambiguity not exercised");

        const string Read = Capabilities.IssuesRead;
        const string Write = Capabilities.IssuesWrite;

        var byName = ConnectionResolver.Resolve(trackers, Read, trackers[0].Label, isWrite: false);
        output.WriteLine($"named '{trackers[0].Label}' -> {byName.Kind}: {byName.SpokenSummary}");
        Assert.Equal(ResolutionKind.Single, byName.Kind);
        Assert.Equal(trackers[0].Label, byName.Connection!.Label);

        // No tracker is nominated, so even a read must ask. Picking the wrong tracker for
        // "what's on my plate" quietly answers about the wrong client's work.
        if (!trackers.Any(t => t.IsDefaultForProvider))
        {
            var vagueRead = ConnectionResolver.Resolve(trackers, Read, label: null, isWrite: false);
            output.WriteLine($"unlabelled READ (no default) -> {vagueRead.Kind}: {vagueRead.SpokenSummary}");
            Assert.Equal(ResolutionKind.Ambiguous, vagueRead.Kind);
        }

        var fanOut = ConnectionResolver.Resolve(trackers, Read, "all", isWrite: false);
        output.WriteLine($"'all' -> {fanOut.Kind}: {fanOut.SpokenSummary}");
        Assert.Equal(ResolutionKind.FanOut, fanOut.Kind);
        Assert.Equal(trackers.Count, fanOut.Connections.Count);

        // The one that matters. Creating an issue in the wrong tracker is visible to other
        // people and cannot be quietly undone, so silence must produce a question.
        var vagueWrite = ConnectionResolver.Resolve(trackers, Write, label: null, isWrite: true);
        output.WriteLine($"unlabelled WRITE -> {vagueWrite.Kind}: {vagueWrite.SpokenSummary}");
        Assert.Equal(ResolutionKind.Ambiguous, vagueWrite.Kind);
        Assert.False(vagueWrite.IsActionable);
        foreach (var t in trackers)
            Assert.Contains(t.Label, vagueWrite.SpokenSummary);

        var typo = ConnectionResolver.Resolve(trackers, Read, "labb", isWrite: false);
        output.WriteLine($"unknown label 'labb' -> {typo.Kind}: {typo.SpokenSummary}");
        Assert.Equal(ResolutionKind.UnknownLabel, typo.Kind);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
