using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Connections;
using OutPost2.Core.Users;
using OutPost2.Infrastructure.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Tests.Connections;

/// <summary>
/// Runs against a real Postgres, because the invariants being tested live in the schema —
/// the filtered unique index on one-default-per-provider cannot be exercised in memory.
/// Each test gets a disposable database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConnectionStoreTests : IAsyncLifetime
{
    private const string Admin =
        "Host=localhost;Port=5435;Database=postgres;Username=outpost2;Password=outpost2-dev";

    private readonly string _dbName = "op2_test_" + Guid.NewGuid().ToString("n")[..12];
    private OutPostDbContext _db = null!;
    private ConnectionStore _store = null!;
    private Guid _alice, _bob;
    private bool _available;

    /// <summary>Encrypts with a throwaway in-process key: this suite is about storage, not crypto.</summary>
    private sealed class LocalProtector : ICredentialProtector
    {
        public Task<ProtectedCredential> ProtectAsync(string p, CancellationToken ct = default) =>
            Task.FromResult(new ProtectedCredential
            {
                WrappedKey = "test:v1", Nonce = "", Tag = "",
                Ciphertext = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(p)),
            });

        public Task<string> UnprotectAsync(ProtectedCredential c, CancellationToken ct = default) =>
            Task.FromResult(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(c.Ciphertext)));

        public Task<ProtectedCredential> RewrapAsync(ProtectedCredential c, CancellationToken ct = default) =>
            Task.FromResult(c);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await using (var admin = new OutPostDbContext(
                new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options))
            {
                await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {_dbName}");
            }

            _db = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
                .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);
            await _db.Database.MigrateAsync();

            _store = new ConnectionStore(_db, new LocalProtector());

            var a = new User { GitHubUserId = 1, GitHubLogin = "alice" };
            var b = new User { GitHubUserId = 2, GitHubLogin = "bob" };
            _db.Users.AddRange(a, b);
            await _db.SaveChangesAsync();
            (_alice, _bob) = (a.Id, b.Id);

            _available = true;
        }
        catch (Exception)
        {
            _available = false;   // Postgres absent: every test skips.
        }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        await _db.DisposeAsync();

        await using var admin = new OutPostDbContext(
            new DbContextOptionsBuilder<OutPostDbContext>().UseNpgsql(Admin).Options);
        await admin.Database.ExecuteSqlRawAsync(
            $"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    private Task<Connection> Add(Guid user, string label, ProviderKind provider = ProviderKind.Microsoft,
                                 string? instance = null, string? account = null) =>
        _store.UpsertAsync(user, provider, instance ?? $"{label}.tenant", account ?? $"{label}-acct",
            label, AuthKind.OAuth2, "credential-blob",
            [new Capability { Name = Capabilities.MailRead, Granted = true, Enabled = true }]);

    // ── the security property ────────────────────────────────────────────────

    [SkippableFact]
    public async Task One_user_cannot_read_another_users_connection_by_id()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var alices = await Add(_alice, "work");

        // Knowing the id must not be enough. This is the single most damaging bug this
        // class could have: a guessed or leaked id reaching someone else's mailbox.
        Assert.Null(await _store.GetAsync(_bob, alices.Id));
        Assert.NotNull(await _store.GetAsync(_alice, alices.Id));
    }

    [SkippableFact]
    public async Task One_user_cannot_delete_another_users_connection()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var alices = await Add(_alice, "work");
        await _store.DeleteAsync(_bob, alices.Id);          // silently a no-op

        Assert.NotNull(await _store.GetAsync(_alice, alices.Id));
    }

    [SkippableFact]
    public async Task Two_users_may_use_the_same_label()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        await Add(_alice, "work");
        await Add(_bob, "work");   // my "work" is not your "work"

        Assert.Equal("work", (await _store.FindByLabelAsync(_alice, "work"))!.Label);
        Assert.Equal("work", (await _store.FindByLabelAsync(_bob, "work"))!.Label);
    }

    // ── defaults ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task First_connection_for_a_provider_becomes_its_default()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Add(_alice, "work");
        var second = await Add(_alice, "home");

        Assert.True(first.IsDefaultForProvider);
        Assert.False(second.IsDefaultForProvider);
    }

    [SkippableFact]
    public async Task Defaults_are_per_provider_not_per_user()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var mail = await Add(_alice, "work");
        var tracker = await Add(_alice, "lab-yt", ProviderKind.YouTrack);

        // "check email" and "my open tickets" must resolve independently.
        Assert.True(mail.IsDefaultForProvider);
        Assert.True(tracker.IsDefaultForProvider);
    }

    [SkippableFact]
    public async Task Setting_a_default_clears_the_previous_one()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Add(_alice, "work");
        var second = await Add(_alice, "home");

        await _store.SetDefaultAsync(_alice, second.Id);
        _db.ChangeTracker.Clear();

        var all = await _store.ListAsync(_alice);
        Assert.Single(all.Where(c => c.IsDefaultForProvider));
        Assert.Equal("home", all.Single(c => c.IsDefaultForProvider).Label);
        Assert.NotEqual(first.Id, all.Single(c => c.IsDefaultForProvider).Id);
    }

    [SkippableFact]
    public async Task Deleting_the_default_promotes_a_survivor()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Add(_alice, "work");
        await Add(_alice, "home");

        await _store.DeleteAsync(_alice, first.Id);
        _db.ChangeTracker.Clear();

        // Otherwise the provider silently loses its default and every unlabelled request
        // starts asking "which one?" for no visible reason.
        var all = await _store.ListAsync(_alice);
        Assert.Single(all);
        Assert.True(all[0].IsDefaultForProvider);
    }

    // ── reconnect semantics ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task Reconnecting_the_same_account_updates_rather_than_duplicating()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Add(_alice, "work", instance: "t1", account: "acct-1");
        var again = await _store.UpsertAsync(_alice, ProviderKind.Microsoft, "t1", "acct-1",
            "work-renamed", AuthKind.OAuth2, "fresh-blob",
            [new Capability { Name = Capabilities.MailRead, Granted = true, Enabled = true }]);

        Assert.Equal(first.Id, again.Id);
        Assert.Single(await _store.ListAsync(_alice));
        Assert.Equal("fresh-blob", await _store.GetCredentialAsync(again));
    }

    [SkippableFact]
    public async Task Reconnecting_preserves_a_capability_the_user_switched_off()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var c = await Add(_alice, "work", instance: "t1", account: "acct-1");
        await _store.SetCapabilityEnabledAsync(_alice, c.Id, Capabilities.MailRead, false);
        _db.ChangeTracker.Clear();

        await _store.UpsertAsync(_alice, ProviderKind.Microsoft, "t1", "acct-1", "work",
            AuthKind.OAuth2, "blob",
            [new Capability { Name = Capabilities.MailRead, Granted = true, Enabled = true }]);
        _db.ChangeTracker.Clear();

        // A reconnect must not silently switch capabilities back on behind the user.
        var reloaded = await _store.FindByLabelAsync(_alice, "work");
        Assert.False(reloaded!.Capabilities.Single(x => x.Name == Capabilities.MailRead).Enabled);
    }

    // ── capability toggles ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task Cannot_enable_a_capability_that_was_never_granted()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var c = await _store.UpsertAsync(_alice, ProviderKind.Microsoft, "t", "a", "work",
            AuthKind.OAuth2, "blob",
            [new Capability { Name = Capabilities.MailSend, Granted = false, Enabled = false }]);

        // Enabling locally would set a flag that lies — it needs incremental consent.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SetCapabilityEnabledAsync(_alice, c.Id, Capabilities.MailSend, true));

        Assert.Contains("not been granted", ex.Message);
    }

    [SkippableFact]
    public async Task Renaming_to_an_existing_label_is_refused()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        await Add(_alice, "work");
        var home = await Add(_alice, "home");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.RenameAsync(_alice, home.Id, "WORK"));
    }
}
