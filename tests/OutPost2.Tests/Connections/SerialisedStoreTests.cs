using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Connections;
using OutPost2.Web.Mcp;

namespace OutPost2.Tests.Connections;

/// <summary>
/// One connection store, one call at a time (OP2-239).
/// <para>
/// <c>search_mail</c> asks three mailboxes at once and each one acquires a token, which
/// reads the stored credential, writes back the rotated cache and stamps <c>LastUsedAt</c> —
/// six concurrent users of a <c>DbContext</c> that is scoped per request and not thread-safe.
/// Npgsql said <c>"A second operation was started on this context instance"</c>, and the
/// surface reported <b>"Nothing found"</b> with the error as a footnote.
/// </para>
/// </summary>
public class SerialisedStoreTests
{
    /// <summary>
    /// Stands in for the DbContext: it fails loudly if two calls are ever inside it at once,
    /// which is exactly what EF does and exactly what the decorator has to prevent.
    /// </summary>
    private sealed class OneAtATimeOnly : IConnectionStore
    {
        private int _inside;

        public int Calls { get; private set; }
        public int Overlaps { get; private set; }

        private async Task<T> Enter<T>(Func<T> result)
        {
            Calls++;

            if (Interlocked.Increment(ref _inside) > 1) Overlaps++;

            // Long enough that an unguarded caller will certainly collide.
            await Task.Delay(15);

            var answer = result();
            Interlocked.Decrement(ref _inside);
            return answer;
        }

        private async Task Enter()
        {
            await Enter(() => 0);
        }

        public Task<IReadOnlyList<Connection>> ListAsync(Guid userId, CancellationToken ct = default) =>
            Enter(() => (IReadOnlyList<Connection>)[]);

        public Task<Connection?> GetAsync(Guid userId, Guid connectionId, CancellationToken ct = default) =>
            Enter(() => (Connection?)null);

        public Task<Connection?> FindByLabelAsync(Guid userId, string label, CancellationToken ct = default) =>
            Enter(() => (Connection?)null);

        public Task<Connection> UpsertAsync(
            Guid userId, ProviderKind provider, string instance, string? homeAccountId,
            string label, AuthKind authKind, string credential,
            IEnumerable<Capability> capabilities, CancellationToken ct = default) =>
            Enter(() => new Connection
            {
                UserId = userId, Label = label, Provider = provider, Instance = instance,
                AuthKind = authKind, SealedCredential = credential,
            });

        public Task<string?> GetCredentialAsync(Connection connection, CancellationToken ct = default) =>
            Enter(() => (string?)"cache");

        public Task SetCredentialAsync(Connection connection, string credential, CancellationToken ct = default) =>
            Enter();

        public Task SetDefaultAsync(Guid userId, Guid connectionId, CancellationToken ct = default) => Enter();
        public Task ClearDefaultAsync(Guid userId, ProviderKind provider, CancellationToken ct = default) => Enter();

        public Task SetStateAsync(Guid userId, Guid connectionId, ConnectionState state, CancellationToken ct = default) =>
            Enter();

        public Task SetCapabilityEnabledAsync(
            Guid userId, Guid connectionId, string capability, bool enabled, CancellationToken ct = default) =>
            Enter();

        public Task RenameAsync(Guid userId, Guid connectionId, string label, CancellationToken ct = default) => Enter();
        public Task DeleteAsync(Guid userId, Guid connectionId, CancellationToken ct = default) => Enter();

        public Task SetAccountIdentityAsync(
            Connection connection, string? accountIdentifier, bool isPersonal, CancellationToken ct = default) =>
            Enter();

        public Task SetCredentialExpiryAsync(
            Connection connection, DateTimeOffset? expiresAt, CancellationToken ct = default) => Enter();

        public Task TouchAsync(Guid connectionId, CancellationToken ct = default) => Enter();

        public Task<Connection> ParkForAdminApprovalAsync(
            Guid userId, ProviderKind provider, string instance, string label,
            IEnumerable<Capability> capabilities, Func<Guid, string> adminConsentUrl,
            CancellationToken ct = default) =>
            Enter(() => new Connection
            {
                UserId = userId, Label = label, Provider = provider, Instance = instance,
                AuthKind = AuthKind.OAuth2, SealedCredential = "",
            });

        public Task<Connection?> MarkAdminConsentGrantedAsync(Guid connectionId, CancellationToken ct = default) =>
            Enter(() => (Connection?)null);
    }

    [Fact]
    public async Task The_token_path_run_three_times_at_once_never_overlaps()
    {
        // The shape that broke: three mailboxes, each reading a credential, writing the
        // rotated cache back and stamping LastUsedAt.
        var inner = new OneAtATimeOnly();
        using var store = new SerialisedConnectionStore(inner);

        var c = new Connection
        {
            UserId = Guid.NewGuid(), Label = "work", Provider = ProviderKind.Microsoft,
            Instance = "t", AuthKind = AuthKind.OAuth2, SealedCredential = "x",
        };

        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await store.GetCredentialAsync(c);
            await store.SetCredentialAsync(c, "rotated");
            await store.TouchAsync(c.Id);
        }));

        Assert.Equal(9, inner.Calls);
        Assert.Equal(0, inner.Overlaps);
    }

    [Fact]
    public async Task Without_the_guard_the_same_shape_does_overlap()
    {
        // The control. Without this the test above proves only that the stub is slow, and a
        // guard that was quietly removed would still pass.
        var inner = new OneAtATimeOnly();

        var c = new Connection
        {
            UserId = Guid.NewGuid(), Label = "work", Provider = ProviderKind.Microsoft,
            Instance = "t", AuthKind = AuthKind.OAuth2, SealedCredential = "x",
        };

        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await inner.GetCredentialAsync(c);
            await inner.SetCredentialAsync(c, "rotated");
            await inner.TouchAsync(c.Id);
        }));

        Assert.True(inner.Overlaps > 0,
            "the stub did not collide, so the guarded test above proves nothing");
    }

    [Fact]
    public async Task Every_method_is_guarded_not_just_the_ones_that_looked_risky()
    {
        // "Which methods can be reached concurrently" is the judgement that was wrong the
        // first time, so the answer is all of them.
        var inner = new OneAtATimeOnly();
        using var store = new SerialisedConnectionStore(inner);

        var user = Guid.NewGuid();
        var id = Guid.NewGuid();
        var c = new Connection
        {
            UserId = user, Label = "work", Provider = ProviderKind.Microsoft,
            Instance = "t", AuthKind = AuthKind.OAuth2, SealedCredential = "x",
        };

        Task[] everything =
        [
            store.ListAsync(user),
            store.GetAsync(user, id),
            store.FindByLabelAsync(user, "work"),
            store.UpsertAsync(user, ProviderKind.Microsoft, "t", null, "work", AuthKind.OAuth2, "c", []),
            store.GetCredentialAsync(c),
            store.SetCredentialAsync(c, "c"),
            store.SetDefaultAsync(user, id),
            store.ClearDefaultAsync(user, ProviderKind.Microsoft),
            store.SetStateAsync(user, id, ConnectionState.Active),
            store.SetCapabilityEnabledAsync(user, id, "mail.read", true),
            store.RenameAsync(user, id, "home"),
            store.DeleteAsync(user, id),
            store.SetAccountIdentityAsync(c, "acct", false),
            store.SetCredentialExpiryAsync(c, DateTimeOffset.UtcNow),
            store.TouchAsync(id),
            store.ParkForAdminApprovalAsync(user, ProviderKind.Microsoft, "t", "work", [], _ => "u"),
            store.MarkAdminConsentGrantedAsync(id),
        ];

        await Task.WhenAll(everything);

        Assert.Equal(everything.Length, inner.Calls);
        Assert.Equal(0, inner.Overlaps);
    }

    [Fact]
    public async Task A_failure_releases_the_gate()
    {
        // A throw holding the gate would deadlock every later call in the request, which is
        // a worse failure than the one being fixed.
        var store = new SerialisedConnectionStore(new Throwing());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TouchAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TouchAsync(Guid.NewGuid()));
    }

    private sealed class Throwing : IConnectionStore
    {
        public Task<IReadOnlyList<Connection>> ListAsync(Guid u, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<Connection?> GetAsync(Guid u, Guid i, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<Connection?> FindByLabelAsync(Guid u, string l, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<Connection> UpsertAsync(Guid u, ProviderKind p, string i, string? h, string l, AuthKind a, string c, IEnumerable<Capability> caps, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<string?> GetCredentialAsync(Connection c, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task SetCredentialAsync(Connection c, string v, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task SetDefaultAsync(Guid u, Guid i, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task ClearDefaultAsync(Guid u, ProviderKind p, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task SetStateAsync(Guid u, Guid i, ConnectionState s, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task SetCapabilityEnabledAsync(Guid u, Guid i, string c, bool e, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task RenameAsync(Guid u, Guid i, string l, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task DeleteAsync(Guid u, Guid i, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task SetAccountIdentityAsync(Connection c, string? a, bool p, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task SetCredentialExpiryAsync(Connection c, DateTimeOffset? e, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task TouchAsync(Guid i, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<Connection> ParkForAdminApprovalAsync(Guid u, ProviderKind p, string i, string l, IEnumerable<Capability> c, Func<Guid, string> a, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<Connection?> MarkAdminConsentGrantedAsync(Guid i, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    // ── never report nothing when nothing was looked at ──────────────────────

    [Fact]
    public void An_empty_answer_from_a_working_mailbox_is_just_empty()
    {
        Assert.Equal("Nothing found for \"invoice\".",
            MailTools.Empty("Nothing found for \"invoice\".", 1, []));
    }

    [Fact]
    public void When_every_mailbox_failed_it_is_a_failure_not_an_empty_result()
    {
        // OP2-239 exactly: the database error was a footnote under a headline that read as
        // a confident empty answer. An empty answer gets acted on; an error gets retried.
        var said = MailTools.Empty("Nothing found for \"invoice\".", 1,
            [("home", "A second operation was started on this context instance")]);

        Assert.DoesNotContain("Nothing found", said);
        Assert.Contains("couldn't look", said);
        Assert.Contains("isn't an empty result", said);
        Assert.Contains("home", said);
    }

    [Fact]
    public void When_some_mailboxes_worked_the_empty_answer_stands_but_is_qualified()
    {
        var said = MailTools.Empty("Nothing found for \"invoice\".", 3,
            [("home", "needs re-authorising")]);

        Assert.Contains("Nothing found", said);
        Assert.Contains("2 of 3", said);
        Assert.Contains("may be more", said);
    }

    [Fact]
    public void The_error_text_is_scrubbed_like_any_other_untrusted_string()
    {
        // A provider error can carry a subject line or an address straight from the wire.
        var said = MailTools.Empty("No messages.", 1,
            [("home", "failed on ‮something‬ odd")]);

        Assert.DoesNotContain('‮', said);
    }
}
