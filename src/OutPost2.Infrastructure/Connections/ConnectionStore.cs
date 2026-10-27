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
    Task SetStateAsync(Guid userId, Guid connectionId, ConnectionState state, CancellationToken ct = default);
    Task SetCapabilityEnabledAsync(Guid userId, Guid connectionId, string capability, bool enabled, CancellationToken ct = default);
    Task RenameAsync(Guid userId, Guid connectionId, string label, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, Guid connectionId, CancellationToken ct = default);
    Task TouchAsync(Guid connectionId, CancellationToken ct = default);
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

    public Task<Connection?> FindByLabelAsync(Guid userId, string label, CancellationToken ct = default) =>
        db.Connections.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Label.ToLower() == label.ToLower(), ct);

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

        var sealedCredential = (await protector.ProtectAsync(credential, ct)).ToJson();

        if (existing is not null)
        {
            existing.SealedCredential = sealedCredential;
            existing.State = ConnectionState.Active;
            existing.LastHealthyAt = DateTimeOffset.UtcNow;

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

        var isFirstForProvider = !await db.Connections
            .AnyAsync(c => c.UserId == userId && c.Provider == provider, ct);

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
            // First connection for a provider becomes its default, so a lone connection
            // never forces a "which one?" question that has only one possible answer.
            IsDefaultForProvider = isFirstForProvider,
            Capabilities = capabilities.ToList(),
        };

        db.Connections.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
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

        var clash = await db.Connections.AnyAsync(
            x => x.UserId == userId && x.Id != connectionId && x.Label.ToLower() == label.ToLower(), ct);
        if (clash) throw new InvalidOperationException($"You already have a connection called '{label}'.");

        c.Label = label;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid userId, Guid connectionId, CancellationToken ct = default)
    {
        var c = await GetAsync(userId, connectionId, ct);
        if (c is null) return;

        var wasDefault = c.IsDefaultForProvider;
        var provider = c.Provider;
        db.Connections.Remove(c);
        await db.SaveChangesAsync(ct);

        // Promote a survivor, or the provider silently loses its default and every
        // unlabelled request starts asking "which one?" for no reason the user can see.
        if (!wasDefault) return;

        var survivor = await db.Connections
            .Where(x => x.UserId == userId && x.Provider == provider)
            .OrderBy(x => x.AddedAt).FirstOrDefaultAsync(ct);

        if (survivor is not null)
        {
            survivor.IsDefaultForProvider = true;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task TouchAsync(Guid connectionId, CancellationToken ct = default) =>
        await db.Connections.Where(c => c.Id == connectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, DateTimeOffset.UtcNow), ct);
}
