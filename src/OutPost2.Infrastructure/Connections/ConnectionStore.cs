using Microsoft.EntityFrameworkCore;
using OutPost2.Core.Connections;
using OutPost2.Infrastructure.Data;
using OutPost2.Infrastructure.Security;

namespace OutPost2.Infrastructure.Connections;

public interface IConnectionStore
{
    Task<IReadOnlyList<Connection>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<Connection?> GetAsync(Guid userId, Guid connectionId, CancellationToken ct = default);
    Task<Connection?> FindByLabelAsync(Guid userId, string label, CancellationToken ct = default);

    Task<Connection> UpsertAsync(
        Guid userId, ProviderKind provider, string instance, string? homeAccountId,
        string label, AuthKind authKind, string credential,
        IEnumerable<Capability> capabilities, CancellationToken ct = default);

    Task<string?> GetCredentialAsync(Connection connection, CancellationToken ct = default);
    Task SetCredentialAsync(Connection connection, string credential, CancellationToken ct = default);

    Task SetDefaultAsync(Guid userId, Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Leaves a provider with no default, so every unlabelled request must name a
    /// connection. The right setting whenever picking wrong is worse than being asked.
    /// </summary>
    Task ClearDefaultAsync(Guid userId, ProviderKind provider, CancellationToken ct = default);
    Task SetStateAsync(Guid userId, Guid connectionId, ConnectionState state, CancellationToken ct = default);
    Task SetCapabilityEnabledAsync(Guid userId, Guid connectionId, string capability, bool enabled, CancellationToken ct = default);
    Task RenameAsync(Guid userId, Guid connectionId, string label, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid connectionId, CancellationToken ct = default);
    Task SetAccountIdentityAsync(Connection connection, string? accountIdentifier, bool isPersonal,
        CancellationToken ct = default);

    /// <summary>Records when the stored credential lapses, or clears it when unknown.</summary>
    Task SetCredentialExpiryAsync(Connection connection, DateTimeOffset? expiresAt,
        CancellationToken ct = default);

    Task TouchAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Records a connection the tenant's administrator has to approve before it can
    /// exist for real (OP2-100), holding the link the user forwards them.
    /// <para>
    /// A real row rather than a note on the side, because the durability is the feature:
    /// the user may wait days, and when they come back the label, the page entry and the
    /// answer to "check work email" all have to still be there.
    /// </para>
    /// </summary>
    Task<Connection> ParkForAdminApprovalAsync(
        Guid userId, ProviderKind provider, string instance, string label,
        IEnumerable<Capability> capabilities, Func<Guid, string> adminConsentUrl,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a parked connection as approved, by id alone and across every user.
    /// <para>
    /// Deliberately NOT scoped to a user, which every other lookup here is: the caller is
    /// an administrator arriving from Entra in their own browser, who is very often not
    /// an OutPost2 user at all. It is safe to leave unscoped only because approving
    /// grants nothing — the connection stays unusable until its owner signs in — so the
    /// worst a guessed id achieves is a wrong sentence on somebody's settings page.
    /// </para>
    /// </summary>
    /// <returns>The connection, or null if the id is unknown or was not waiting.</returns>
    Task<Connection?> MarkAdminConsentGrantedAsync(Guid connectionId, CancellationToken ct = default);
}

public sealed class ConnectionStore(OutPostDbContext db, ICredentialProtector protector) : IConnectionStore
{
    public async Task<IReadOnlyList<Connection>> ListAsync(Guid userId, CancellationToken ct = default) =>
        await db.Connections.Where(c => c.UserId == userId)
            .OrderBy(c => c.Provider).ThenBy(c => c.Label)
            .ToListAsync(ct);

    // Every read is scoped by userId as well as id. Looking a connection up by id alone
    // would let a guessed or leaked id reach another user's mailbox — the single most
    // damaging bug this class could have, so the signature makes it impossible to write.
    public Task<Connection?> GetAsync(Guid userId, Guid connectionId, CancellationToken ct = default) =>
        db.Connections.FirstOrDefaultAsync(c => c.UserId == userId && c.Id == connectionId, ct);

    // RCS1155 wants StringComparison here and it cannot have it: this lambda is an
    // expression tree that Npgsql has to turn into SQL, and string.Equals with a
    // StringComparison has no translation — EF throws at runtime rather than warning at
    // build time, so taking the analyzer's advice would swap a build-clean warning for a
    // failure the first time anybody says a connection's name out loud (OP2-146).
    //
    // ToLower() on both sides is what does translate. It is also why the comparison is
    // case-insensitive at all, which matters more here than it looks: these labels are
    // spoken, and "Work" and "work" have to be the same mailbox.
#pragma warning disable RCS1155
    public Task<Connection?> FindByLabelAsync(Guid userId, string label, CancellationToken ct = default) =>
        db.Connections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Label.ToLower() == label.ToLower(), ct);
#pragma warning restore RCS1155

    public async Task<Connection> UpsertAsync(
        Guid userId, ProviderKind provider, string instance, string? homeAccountId,
        string label, AuthKind authKind, string credential,
        IEnumerable<Capability> capabilities, CancellationToken ct = default)
    {
        // Re-authorising an account we already hold must UPDATE it, not create a duplicate
        // under a second label. Matched on identity (provider + instance + account), never
        // on the label — the label is the user's name for it and they may change it.
        var existing = await db.Connections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == provider
                 && c.Instance == instance && c.HomeAccountId == homeAccountId, ct);

        // A connection parked for admin approval (OP2-100) cannot match on identity: it
        // was created before anybody signed in, so it holds no account id and whatever
        // tenant the user happened to hint. Matching it by LABEL is the exception that
        // the rule above exists to avoid — and it is safe precisely here, because the
        // label is uniquely indexed per user, so there is exactly one candidate and no
        // ambiguity to resolve.
        //
        // Without this the first successful sign-in after approval would insert a SECOND
        // row under a name already taken, hit the unique index, and surface as a 500 the
        // moment the user finally got through. The parked row is adopted instead: its
        // placeholder tenant and null account are replaced by the real ones, and the
        // label, default and capability toggles the user chose along the way survive.
        existing ??= await db.Connections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == provider
                 && c.State == ConnectionState.NeedsAdminApproval
                 && c.HomeAccountId == null && c.Label == label, ct);

        var sealedCredential = (await protector.ProtectAsync(credential, ct)).ToJson();

        if (existing is not null)
        {
            existing.SealedCredential = sealedCredential;
            existing.State = ConnectionState.Active;
            existing.LastHealthyAt = DateTimeOffset.UtcNow;

            // Set rather than asserted, because this is also how an adopted parked row
            // learns which tenant and which account it turned out to be.
            existing.Instance = instance;
            existing.HomeAccountId = homeAccountId;

            // The approval did its job and the link is spent. Leaving them behind would
            // keep offering the user a link to forward for a mailbox that already works.
            existing.AdminConsentUrl = null;
            existing.AdminConsentGrantedAt = null;

            // Re-consent can widen what was granted; preserve the user's enabled choices
            // so a reconnect does not silently switch capabilities back on.
            foreach (var incoming in capabilities)
            {
                var current = existing.Capabilities.FirstOrDefault(x => x.Name == incoming.Name);
                if (current is null) existing.Capabilities.Add(incoming);
                else current.Granted = incoming.Granted;
            }

            await db.SaveChangesAsync(ct);
            return existing;
        }

        var created = new Connection
        {
            UserId = userId,
            Label = label,
            Provider = provider,
            Instance = instance,
            HomeAccountId = homeAccountId,
            AuthKind = authKind,
            SealedCredential = sealedCredential,
            State = ConnectionState.Active,
            LastHealthyAt = DateTimeOffset.UtcNow,
            // No connection becomes a default on its own. A default is standing
            // permission to act on an unnamed request, and that is the user's call to
            // make, not a side effect of being added first.
            IsDefaultForProvider = false,
            Capabilities = capabilities.ToList(),
        };

        db.Connections.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <inheritdoc />
    public async Task<Connection> ParkForAdminApprovalAsync(
        Guid userId, ProviderKind provider, string instance, string label,
        IEnumerable<Capability> capabilities, Func<Guid, string> adminConsentUrl,
        CancellationToken ct = default)
    {
        // Re-running a blocked sign-in must not pile up rows. Matched on label for the
        // same reason adoption is: a parked connection has no identity to match on yet.
        var existing = await db.Connections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == provider
                 && c.State == ConnectionState.NeedsAdminApproval
                 && c.HomeAccountId == null && c.Label == label, ct);

        if (existing is not null)
        {
            // Refresh the link rather than keep the first one: the configured scopes or
            // redirect URI may have moved since, and a stale link sends the administrator
            // to approve the wrong set.
            existing.Instance = instance;
            existing.AdminConsentUrl = adminConsentUrl(existing.Id);
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var parked = new Connection
        {
            UserId = userId,
            Label = label,
            Provider = provider,
            Instance = instance,
            AuthKind = AuthKind.OAuth2,

            // There is no credential and there must not be one. An empty string, not a
            // placeholder that could be mistaken for a sealed blob: GetCredentialAsync
            // already treats blank as "nothing stored" and returns null.
            SealedCredential = "",

            State = ConnectionState.NeedsAdminApproval,
            IsDefaultForProvider = false,
            Capabilities = capabilities.ToList(),
        };

        // A delegate rather than a string, because the link has to carry this
        // connection's own id as the `state` Entra echoes back — so it cannot be built
        // until the id exists, and it has to be rebuilt against the EXISTING id on the
        // branch above. Taking a ready-made string would mean either two writes or a
        // link pointing at a row that is not the one being updated.
        parked.AdminConsentUrl = adminConsentUrl(parked.Id);

        db.Connections.Add(parked);
        await db.SaveChangesAsync(ct);
        return parked;
    }

    /// <inheritdoc />
    public async Task<Connection?> MarkAdminConsentGrantedAsync(Guid connectionId, CancellationToken ct = default)
    {
        var c = await db.Connections.FirstOrDefaultAsync(
            x => x.Id == connectionId && x.State == ConnectionState.NeedsAdminApproval, ct);

        if (c is null) return null;

        // Idempotent: an administrator who opens the link twice, or whose browser
        // prefetches it, must not move the clock and must not fail.
        c.AdminConsentGrantedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<string?> GetCredentialAsync(Connection connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.SealedCredential)) return null;
        return await protector.UnprotectAsync(ProtectedCredential.FromJson(connection.SealedCredential), ct);
    }

    public async Task SetCredentialAsync(Connection connection, string credential, CancellationToken ct = default)
    {
        connection.SealedCredential = (await protector.ProtectAsync(credential, ct)).ToJson();
        await db.SaveChangesAsync(ct);
    }

    public async Task SetDefaultAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        var target = await GetAsync(userId, connectionId, ct)
                     ?? throw new InvalidOperationException("Connection not found.");

        // Clear siblings first. A filtered unique index enforces one default per provider in
        // the database, so setting without clearing would be rejected outright — which is the
        // point of having the constraint.
        var siblings = await db.Connections
            .Where(c => c.UserId == userId && c.Provider == target.Provider && c.Id != connectionId)
            .ToListAsync(ct);

        foreach (var s in siblings) s.IsDefaultForProvider = false;
        await db.SaveChangesAsync(ct);

        target.IsDefaultForProvider = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearDefaultAsync(Guid userId, ProviderKind provider, CancellationToken ct = default)
    {
        var current = await db.Connections
            .Where(c => c.UserId == userId && c.Provider == provider && c.IsDefaultForProvider)
            .ToListAsync(ct);

        if (current.Count == 0) return;

        foreach (var c in current) c.IsDefaultForProvider = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStateAsync(Guid userId, Guid connectionId, ConnectionState state, CancellationToken ct = default)
    {
        var c = await GetAsync(userId, connectionId, ct) ?? throw new InvalidOperationException("Connection not found.");
        c.State = state;
        if (state is ConnectionState.Active) c.LastHealthyAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetCapabilityEnabledAsync(
        Guid userId, Guid connectionId, string capability, bool enabled, CancellationToken ct = default)
    {
        var c = await GetAsync(userId, connectionId, ct) ?? throw new InvalidOperationException("Connection not found.");
        var cap = c.Capabilities.FirstOrDefault(x => x.Name == capability)
                  ?? throw new InvalidOperationException($"Connection has no capability '{capability}'.");

        // Enabling something ungranted cannot be done locally — it needs incremental
        // consent, and possibly an admin. Refuse rather than set a flag that lies.
        if (enabled && !cap.Granted)
            throw new InvalidOperationException(
                $"'{capability}' has not been granted for {c.Label}. Reconnect to request it.");

        cap.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(Guid userId, Guid connectionId, string label, CancellationToken ct = default)
    {
        var c = await GetAsync(userId, connectionId, ct) ?? throw new InvalidOperationException("Connection not found.");

        // ToLower for the same reason as FindByLabelAsync above — this has to become SQL.
#pragma warning disable RCS1155
        var clash = await db.Connections.AnyAsync(
            x => x.UserId == userId && x.Id != connectionId && x.Label.ToLower() == label.ToLower(), ct);
#pragma warning restore RCS1155
        if (clash) throw new InvalidOperationException($"You already have a connection called '{label}'.");

        c.Label = label;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        var c = await GetAsync(userId, connectionId, ct);
        if (c is null) return;

        // Deleting the default does NOT promote a survivor. Inheriting standing
        // permission to act unnamed, because a different connection was removed, is
        // exactly the silent substitution the whole resolver exists to prevent.
        db.Connections.Remove(c);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetCredentialExpiryAsync(
        Connection connection, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        connection.CredentialExpiresAt = expiresAt;
        await db.Connections
            .Where(c => c.Id == connection.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CredentialExpiresAt, expiresAt), ct);
    }

    public async Task SetAccountIdentityAsync(
        Connection connection, string? accountIdentifier, bool isPersonal, CancellationToken ct = default)
    {
        connection.AccountIdentifier = accountIdentifier;
        connection.IsPersonalAccount = isPersonal;
        await db.SaveChangesAsync(ct);
    }

    public async Task TouchAsync(Guid connectionId, CancellationToken ct = default) =>
        await db.Connections.Where(c => c.Id == connectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, DateTimeOffset.UtcNow), ct);
}
