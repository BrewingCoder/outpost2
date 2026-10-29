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
    public async Task No_connection_becomes_a_default_on_its_own()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Add(_alice, "work");
        var second = await Add(_alice, "home");

        // A default is standing permission to act on a request that named nobody. Being
        // added first is not a reason to be granted it.
        Assert.False(first.IsDefaultForProvider);
        Assert.False(second.IsDefaultForProvider);
    }

    [SkippableFact]
    public async Task Defaults_are_per_provider_not_per_user()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var mail = await Add(_alice, "work");
        var tracker = await Add(_alice, "lab-yt", ProviderKind.YouTrack);

        await _store.SetDefaultAsync(_alice, mail.Id);
        await _store.SetDefaultAsync(_alice, tracker.Id);
        _db.ChangeTracker.Clear();

        // "check email" and "my open tickets" must resolve independently: nominating a
        // tracker must not unseat the mailbox.
        var all = await _store.ListAsync(_alice);
        Assert.True(all.Single(c => c.Provider == ProviderKind.Microsoft).IsDefaultForProvider);
        Assert.True(all.Single(c => c.Provider == ProviderKind.YouTrack).IsDefaultForProvider);
    }

    [SkippableFact]
    public async Task Setting_a_default_clears_the_previous_one()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Add(_alice, "work");
        var second = await Add(_alice, "home");

        await _store.SetDefaultAsync(_alice, first.Id);
        await _store.SetDefaultAsync(_alice, second.Id);
        _db.ChangeTracker.Clear();

        var all = await _store.ListAsync(_alice);
        Assert.Single(all.Where(c => c.IsDefaultForProvider));
        Assert.Equal("home", all.Single(c => c.IsDefaultForProvider).Label);
        Assert.NotEqual(first.Id, all.Single(c => c.IsDefaultForProvider).Id);
    }

    [SkippableFact]
    public async Task Deleting_the_default_does_not_promote_a_survivor()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Add(_alice, "work");
        await Add(_alice, "home");
        await _store.SetDefaultAsync(_alice, first.Id);

        await _store.DeleteAsync(_alice, first.Id);
        _db.ChangeTracker.Clear();

        // Inheriting standing permission because a different connection was removed is
        // precisely the silent substitution the resolver exists to prevent. Asking is the
        // correct outcome here, even though it is the more annoying one.
        var all = await _store.ListAsync(_alice);
        Assert.Single(all);
        Assert.False(all[0].IsDefaultForProvider);
    }

    [SkippableFact]
    public async Task Clearing_a_default_leaves_the_provider_with_none()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var work = await Add(_alice, "work");
        await Add(_alice, "home");
        await _store.SetDefaultAsync(_alice, work.Id);

        await _store.ClearDefaultAsync(_alice, ProviderKind.Microsoft);
        _db.ChangeTracker.Clear();

        var all = await _store.ListAsync(_alice);
        Assert.All(all, c => Assert.False(c.IsDefaultForProvider));
    }

    [SkippableFact]
    public async Task The_same_account_on_two_instances_is_two_connections()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        // github.com and an Enterprise server both know you as the same login, so identity
        // is only unique once the instance is part of it. Without that, connecting the
        // second would silently overwrite the first's credential and the user would be
        // left wondering why one of their two GitHub accounts kept disappearing.
        var dotCom = await Add(_alice, "github", ProviderKind.GitHub,
            instance: "https://api.github.com", account: "BrewingCoder");

        var enterprise = await Add(_alice, "work-github", ProviderKind.GitHub,
            instance: "https://ghe.example.com/api/v3", account: "BrewingCoder");

        Assert.NotEqual(dotCom.Id, enterprise.Id);

        var all = await _store.ListAsync(_alice);
        Assert.Equal(2, all.Count(c => c.Provider == ProviderKind.GitHub));
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

    // ── waiting on an administrator (OP2-100) ────────────────────────────────

    private Task<Connection> Park(Guid user, string label, string instance = Connection.UnknownTenant) =>
        _store.ParkForAdminApprovalAsync(user, ProviderKind.Microsoft, instance, label,
            [new Capability { Name = Capabilities.MailRead, Granted = false, Enabled = true }],
            id => $"https://login.microsoftonline.com/{instance}/v2.0/adminconsent?state={id}");

    [SkippableFact]
    public async Task A_parked_connection_carries_a_link_naming_its_own_id()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var parked = await Park(_alice, "work");

        Assert.Equal(ConnectionState.NeedsAdminApproval, parked.State);
        Assert.Null(parked.AdminConsentGrantedAt);

        // The id in the link is the `state` Entra echoes back, and it is how an
        // administrator arriving days later in a browser with no session of their own
        // reaches the right connection. A link naming a different id reaches nothing.
        Assert.Contains($"state={parked.Id}", parked.AdminConsentUrl);
    }

    [SkippableFact]
    public async Task Retrying_a_blocked_sign_in_refreshes_the_link_rather_than_piling_up_rows()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var first = await Park(_alice, "work");
        var second = await Park(_alice, "work", "contoso.com");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("contoso.com", second.Instance);
        Assert.Contains("contoso.com", second.AdminConsentUrl);

        // Still one connection. The label is uniquely indexed, so a second insert would
        // not merely duplicate — it would throw, in the user's face, on a retry.
        Assert.Single(await _store.ListAsync(_alice));
    }

    [SkippableFact]
    public async Task Approval_is_recorded_without_making_the_connection_usable()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var parked = await Park(_alice, "work");
        var approved = await _store.MarkAdminConsentGrantedAsync(parked.Id);

        Assert.NotNull(approved);
        Assert.NotNull(approved!.AdminConsentGrantedAt);

        // Tenant-wide consent grants the PERMISSION; it does not mint a delegated token.
        // Flipping this to Active here would make every tool believe it could reach a
        // mailbox it holds no credential for.
        Assert.Equal(ConnectionState.NeedsAdminApproval, approved.State);
    }

    [SkippableFact]
    public async Task Approving_twice_does_not_move_the_clock()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var parked = await Park(_alice, "work");
        var first = await _store.MarkAdminConsentGrantedAsync(parked.Id);
        var second = await _store.MarkAdminConsentGrantedAsync(parked.Id);

        // An administrator refreshing the page, or a mail client prefetching the link,
        // must not be an error and must not look like a second approval.
        Assert.Equal(first!.AdminConsentGrantedAt, second!.AdminConsentGrantedAt);
    }

    [SkippableFact]
    public async Task Approving_an_unknown_id_reports_nothing_rather_than_throwing()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        Assert.Null(await _store.MarkAdminConsentGrantedAsync(Guid.CreateVersion7()));

        // A connection that is not waiting is not a candidate either, so a replayed link
        // cannot disturb one that has since been completed.
        var live = await Add(_alice, "work");
        Assert.Null(await _store.MarkAdminConsentGrantedAsync(live.Id));
    }

    [SkippableFact]
    public async Task Signing_in_after_approval_adopts_the_parked_row_instead_of_clashing_on_its_name()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var parked = await Park(_alice, "work");
        await _store.MarkAdminConsentGrantedAsync(parked.Id);

        // The real sign-in brings a tenant id and an account that the parked row could
        // not have known. Matched on identity it finds nothing; without adoption this
        // inserts a second "work" and dies on the unique label index — a 500 at the
        // exact moment the user finally got through.
        var real = await Add(_alice, "work", instance: "aaaa-tenant", account: "oid.tid");

        Assert.Equal(parked.Id, real.Id);
        Assert.Equal(ConnectionState.Active, real.State);
        Assert.Equal("aaaa-tenant", real.Instance);
        Assert.Equal("oid.tid", real.HomeAccountId);

        // The link and the approval are spent; leaving them would keep offering the user
        // a link to forward for a mailbox that already works.
        Assert.Null(real.AdminConsentUrl);
        Assert.Null(real.AdminConsentGrantedAt);

        Assert.Single(await _store.ListAsync(_alice));
    }

    [SkippableFact]
    public async Task Adoption_does_not_reach_across_users()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var alices = await Park(_alice, "work");
        var bobs = await Add(_bob, "work", instance: "aaaa-tenant", account: "oid.tid");

        Assert.NotEqual(alices.Id, bobs.Id);
        Assert.Equal(ConnectionState.NeedsAdminApproval,
            (await _store.GetAsync(_alice, alices.Id))!.State);
    }

    // ── capabilities added after the connection was made (OP2-166) ───────────

    [SkippableFact]
    public async Task A_connection_stored_before_a_capability_existed_comes_back_declaring_it()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        // Add() writes mail.read and nothing else, which is exactly the shape of the two
        // live connections that produced "No connection called work" for a calendar
        // request: the capability was absent rather than ungranted, so the resolver
        // could not see the connection at all.
        var stored = await Add(_alice, "work");
        Assert.Single(stored.Capabilities);

        foreach (var read in new[]
                 {
                     (await _store.ListAsync(_alice)).Single(),
                     (await _store.GetAsync(_alice, stored.Id))!,
                     (await _store.FindByLabelAsync(_alice, "work"))!,
                 })
        {
            var cal = Assert.Single(read.Capabilities, x => x.Name == Capabilities.CalendarRead);
            Assert.False(cal.Granted);
            Assert.True(cal.NeverRequested);
        }
    }

    [SkippableFact]
    public async Task Declaring_a_missing_capability_is_not_written_back_to_the_row()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        // The declaration is true of the running build, not of the row. Persisting it
        // would bake this build's capability list into data that outlives it — and the
        // next build to drop a capability would leave a dead entry nobody can explain.
        var stored = await Add(_alice, "work");
        _ = await _store.ListAsync(_alice);

        await using var fresh = new OutPostDbContext(new DbContextOptionsBuilder<OutPostDbContext>()
            .UseNpgsql(Admin.Replace("Database=postgres", $"Database={_dbName}")).Options);

        var row = await fresh.Connections.AsNoTracking().SingleAsync(c => c.Id == stored.Id);
        Assert.Single(row.Capabilities);
        Assert.Equal(Capabilities.MailRead, row.Capabilities[0].Name);
    }

    [SkippableFact]
    public async Task Reconnecting_stops_calling_a_capability_never_requested()
    {
        Skip.IfNot(_available, "Postgres not reachable on localhost:5435");

        var stored = await Add(_alice, "work");
        _ = await _store.GetAsync(_alice, stored.Id);   // declares calendar.read in memory

        // The reconnect the user was told to do. Whatever the tenant answered, the
        // question has now been asked — continuing to say "nobody has asked yet, try
        // reconnecting" is the one piece of advice guaranteed not to help twice.
        await _store.UpsertAsync(_alice, ProviderKind.Microsoft, "work.tenant", "work-acct",
            "work", AuthKind.OAuth2, "credential-blob",
            [new Capability { Name = Capabilities.CalendarRead, Granted = true, Enabled = true }]);

        var after = (await _store.GetAsync(_alice, stored.Id))!;
        var cal = after.Capabilities.Single(x => x.Name == Capabilities.CalendarRead);

        Assert.True(cal.Granted);
        Assert.False(cal.NeverRequested);
    }
}
