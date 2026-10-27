using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Connections;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Exercises the YouTrack provider against the real lab server, through the real
/// Postgres store and the real Vault protector. This is the end-to-end proof that the
/// provider-agnostic substrate actually carries a second, non-Microsoft provider —
/// which is the whole claim of D4.
/// </summary>
[Trait("Category", "Integration")]
public sealed class YouTrackConnectionTests : IAsyncLifetime
{
    private const string Admin =
        "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";

    private readonly string _dbName = "op2_yt_" + Guid.NewGuid().ToString("n")[..12];
    private OutPostDbContext _db = null!;
    private ConnectionStore _store = null!;
    private YouTrackConnectionService _yt = null!;
    private VaultTransitClient _vault = null!;
    private Guid _user;
    private string _url = "", _token = "";
    private bool _ready;
    private string _why = "";

    public async Task InitializeAsync()
    {
        try
        {
            var credsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "credentials.json");
            if (!File.Exists(credsPath)) { _why = "credentials.json not found"; return; }

            using var creds = JsonDocument.Parse(await File.ReadAllTextAsync(credsPath));
            _url = creds.RootElement.GetProperty("lab_youtrack_url").GetString()!;
            _token = creds.RootElement.GetProperty("lab_youtrack_token").GetString()!;

            await using (var admin = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
                await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {_dbName}");

            _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
                .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);
            await _db.Database.MigrateAsync();

            _vault = new VaultTransitClient(
                Microsoft.Extensions.Options.Options.Create(new VaultOptions
                {
                    Address = "http://localhost:8200", AuthMethod = VaultAuthMethod.Token,
                    Token = "op2-dev-root", TransitMount = "op2-transit", KeyName = "op2-kek",
                }), NullLogger<VaultTransitClient>.Instance);

            if (!await _vault.HealthyAsync()) { _why = "dev Vault unreachable"; return; }

            _store = new ConnectionStore(_db, new CredentialProtector(_vault));

            var services = new ServiceCollection();
            services.AddHttpClient("youtrack");
            var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

            _yt = new YouTrackConnectionService(factory, _store, NullLogger<YouTrackConnectionService>.Instance);

            var u = new User { GitHubUserId = 99, GitHubLogin = "tester" };
            _db.Users.Add(u);
            await _db.SaveChangesAsync();
            _user = u.Id;

            _ready = true;
        }
        catch (Exception ex) { _why = ex.Message; }
    }

    public async Task DisposeAsync()
    {
        _vault?.Dispose();
        if (_db is null) return;
        await _db.DisposeAsync();

        await using var admin = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    [SkippableFact]
    public async Task Probe_accepts_a_valid_token_and_names_the_account()
    {
        Skip.IfNot(_ready, _why);

        var probe = await _yt.ProbeAsync(_url, _token);

        Assert.True(probe.Ok, probe.Error);
        Assert.False(string.IsNullOrWhiteSpace(probe.Login));
    }

    [SkippableFact]
    public async Task Probe_rejects_a_bad_token_rather_than_storing_it()
    {
        Skip.IfNot(_ready, _why);

        var probe = await _yt.ProbeAsync(_url, "perm-obviously-not-a-real-token");

        Assert.False(probe.Ok);
        Assert.False(string.IsNullOrWhiteSpace(probe.Error));
    }

    [SkippableFact]
    public async Task Probe_reports_an_unreachable_server_without_throwing()
    {
        Skip.IfNot(_ready, _why);

        // A typo'd hostname must produce a sentence the user can act on, not an exception
        // surfacing as a 500 — this is the single most likely setup mistake.
        var probe = await _yt.ProbeAsync("yt.nonexistent-host-op2.invalid", _token);

        Assert.False(probe.Ok);
        Assert.Contains("Could not reach", probe.Error!);
    }

    [SkippableFact]
    public async Task Connect_stores_an_encrypted_token_that_round_trips()
    {
        Skip.IfNot(_ready, _why);

        var c = await _yt.ConnectAsync(_user, _url, _token, "lab");

        Assert.Equal(ProviderKind.YouTrack, c.Provider);
        Assert.Equal(AuthKind.PersonalAccessToken, c.AuthKind);
        Assert.False(c.IsDefaultForProvider);         // nothing becomes a default on its own

        // Stored form must not contain the token in the clear.
        Assert.DoesNotContain(_token, c.SealedCredential, StringComparison.Ordinal);

        // ...but must decrypt back to exactly it.
        Assert.Equal(_token, await _store.GetCredentialAsync(c));
    }

    [SkippableFact]
    public async Task Admin_capability_is_granted_but_switched_off_by_default()
    {
        Skip.IfNot(_ready, _why);

        var c = await _yt.ConnectAsync(_user, _url, _token, "lab");

        var admin = c.Capabilities.Single(x => x.Name == Capabilities.IssuesAdmin);
        var read = c.Capabilities.Single(x => x.Name == Capabilities.IssuesRead);

        // "Create a project" and "read my tickets" must not be one switch, because a voice
        // turn can trigger either.
        Assert.True(admin.Granted);
        Assert.False(admin.Enabled);
        Assert.False(admin.Usable);
        Assert.True(read.Usable);
    }

    [SkippableFact]
    public async Task Capabilities_are_flagged_as_self_imposed_for_a_PAT()
    {
        Skip.IfNot(_ready, _why);

        var c = await _yt.ConnectAsync(_user, _url, _token, "lab");

        // We cannot narrow a pasted PAT - whatever it could do, we can do. The UI must say
        // so honestly rather than implying a sandbox we do not have.
        Assert.All(c.Capabilities, cap => Assert.True(cap.IsSelfImposedOnly));
    }

    [SkippableFact]
    public async Task Health_check_passes_for_a_live_connection()
    {
        Skip.IfNot(_ready, _why);

        var c = await _yt.ConnectAsync(_user, _url, _token, "lab");

        Assert.True(await _yt.CheckHealthAsync(c));
        Assert.Equal(ConnectionState.Active, c.State);
    }

    [SkippableFact]
    public async Task Health_check_marks_a_dead_token_expired_rather_than_leaving_it_active()
    {
        Skip.IfNot(_ready, _why);

        var c = await _yt.ConnectAsync(_user, _url, _token, "lab");
        await _store.SetCredentialAsync(c, "perm-revoked-token-value");

        // PATs give no expiry warning, so polling is the only way to notice. A stale
        // "active" would only fail when the user actually asked for something — probably
        // mid-conversation, in the car.
        Assert.False(await _yt.CheckHealthAsync(c));
        Assert.Equal(ConnectionState.Expired, c.State);
    }

    [SkippableFact]
    public async Task Resolver_picks_the_youtrack_connection_for_an_issues_capability()
    {
        Skip.IfNot(_ready, _why);

        await _yt.ConnectAsync(_user, _url, _token, "lab");
        var all = await _store.ListAsync(_user);

        // The substrate claim: one resolver serves both providers, unchanged.
        var r = ConnectionResolver.Resolve(all, Capabilities.IssuesRead, "lab", isWrite: false);
        Assert.Equal(ResolutionKind.Single, r.Kind);
        Assert.Equal("lab", r.Connection!.Label);

        // Unnamed, it asks: a tracker is never picked for you.
        var unnamed = ConnectionResolver.Resolve(all, Capabilities.IssuesRead, null, isWrite: false);
        Assert.Equal(ResolutionKind.Ambiguous, unnamed.Kind);

        // ...and an admin action is refused, because that capability is switched off.
        var adminResolve = ConnectionResolver.Resolve(all, Capabilities.IssuesAdmin, "lab", isWrite: true);
        Assert.Equal(ResolutionKind.Unavailable, adminResolve.Kind);
        Assert.Contains("switched off", adminResolve.SpokenSummary);
    }
}
