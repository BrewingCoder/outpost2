using OutPost2.Core.Connections;

namespace OutPost2.Infrastructure.Connections;

/// <summary>
/// One connection store, used by one request, one call at a time (OP2-239).
/// <para>
/// A <c>DbContext</c> is scoped per request and is <b>not</b> thread-safe, and several tools
/// here deliberately fan out: <c>search_mail</c> asks three mailboxes at once, and each of
/// those acquires a token, which reads the stored credential, writes back the rotated cache
/// and stamps <c>LastUsedAt</c>. Three mailboxes became six concurrent users of one context,
/// and Npgsql said so:
/// </para>
/// <code>
/// A second operation was started on this context instance before a previous operation completed
/// A command is already in progress: UPDATE connections SET "LastUsedAt" = ...
/// </code>
/// <para>
/// Reported as OP2-239, where the symptom was far worse than the error: the search came back
/// <b>"Nothing found"</b> with the database error demoted to a footnote. A corrupted read
/// that looks like a confident empty answer is the worst thing this surface can do.
/// </para>
/// <para>
/// A decorator rather than a lock inside each of the store's eighteen methods, because
/// "which methods can be reached concurrently" is precisely the judgement that was wrong
/// before. Everything is serialised, including the reads, and no future method can be added
/// without the guard.
/// </para>
/// <para>
/// The cost is small and the concurrency that matters is kept: what runs in parallel here is
/// the Graph request, which is where the hundreds of milliseconds are. Token acquisition is
/// a cache hit and two small writes, and serialising those also stops two concurrent
/// refreshes of the <em>same</em> connection both rotating the cache and racing to store it.
/// </para>
/// </summary>
public sealed class SerialisedConnectionStore(IConnectionStore inner)
    : IConnectionStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <remarks>
    /// The cancellation token is deliberately not passed to <c>WaitAsync</c>. A caller
    /// giving up while another is mid-write must not abandon the gate: the point of it is
    /// that the context is only ever touched by one of them.
    /// </remarks>
    private async Task<T> OneAtATime<T>(Func<Task<T>> work)
    {
        await _gate.WaitAsync();
        try { return await work(); }
        finally { _gate.Release(); }
    }

    private async Task OneAtATime(Func<Task> work)
    {
        await _gate.WaitAsync();
        try { await work(); }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<Connection>> ListAsync(Guid userId, CancellationToken ct = default) =>
        OneAtATime(() => inner.ListAsync(userId, ct));

    public Task<Connection?> GetAsync(Guid userId, Guid connectionId, CancellationToken ct = default) =>
        OneAtATime(() => inner.GetAsync(userId, connectionId, ct));

    public Task<Connection?> FindByLabelAsync(Guid userId, string label, CancellationToken ct = default) =>
        OneAtATime(() => inner.FindByLabelAsync(userId, label, ct));

    public Task<Connection> UpsertAsync(
        Guid userId, ProviderKind provider, string instance, string? homeAccountId,
        string label, AuthKind authKind, string credential,
        IEnumerable<Capability> capabilities, CancellationToken ct = default) =>
        OneAtATime(() => inner.UpsertAsync(
            userId, provider, instance, homeAccountId, label, authKind, credential, capabilities, ct));

    public Task<string?> GetCredentialAsync(Connection connection, CancellationToken ct = default) =>
        OneAtATime(() => inner.GetCredentialAsync(connection, ct));

    public Task SetCredentialAsync(Connection connection, string credential, CancellationToken ct = default) =>
        OneAtATime(() => inner.SetCredentialAsync(connection, credential, ct));

    public Task SetDefaultAsync(Guid userId, Guid connectionId, CancellationToken ct = default) =>
        OneAtATime(() => inner.SetDefaultAsync(userId, connectionId, ct));

    public Task ClearDefaultAsync(Guid userId, ProviderKind provider, CancellationToken ct = default) =>
        OneAtATime(() => inner.ClearDefaultAsync(userId, provider, ct));

    public Task SetStateAsync(Guid userId, Guid connectionId, ConnectionState state, CancellationToken ct = default) =>
        OneAtATime(() => inner.SetStateAsync(userId, connectionId, state, ct));

    public Task SetCapabilityEnabledAsync(
        Guid userId, Guid connectionId, string capability, bool enabled, CancellationToken ct = default) =>
        OneAtATime(() => inner.SetCapabilityEnabledAsync(userId, connectionId, capability, enabled, ct));

    public Task RenameAsync(Guid userId, Guid connectionId, string label, CancellationToken ct = default) =>
        OneAtATime(() => inner.RenameAsync(userId, connectionId, label, ct));

    public Task DeleteAsync(Guid userId, Guid connectionId, CancellationToken ct = default) =>
        OneAtATime(() => inner.DeleteAsync(userId, connectionId, ct));

    public Task SetAccountIdentityAsync(
        Connection connection, string? accountIdentifier, bool isPersonal, CancellationToken ct = default) =>
        OneAtATime(() => inner.SetAccountIdentityAsync(connection, accountIdentifier, isPersonal, ct));

    public Task SetCredentialExpiryAsync(
        Connection connection, DateTimeOffset? expiresAt, CancellationToken ct = default) =>
        OneAtATime(() => inner.SetCredentialExpiryAsync(connection, expiresAt, ct));

    public Task TouchAsync(Guid connectionId, CancellationToken ct = default) =>
        OneAtATime(() => inner.TouchAsync(connectionId, ct));

    public Task<Connection> ParkForAdminApprovalAsync(
        Guid userId, ProviderKind provider, string instance, string label,
        IEnumerable<Capability> capabilities, Func<Guid, string> adminConsentUrl,
        CancellationToken ct = default) =>
        OneAtATime(() => inner.ParkForAdminApprovalAsync(
            userId, provider, instance, label, capabilities, adminConsentUrl, ct));

    public Task<Connection?> MarkAdminConsentGrantedAsync(Guid connectionId, CancellationToken ct = default) =>
        OneAtATime(() => inner.MarkAdminConsentGrantedAsync(connectionId, ct));
}
